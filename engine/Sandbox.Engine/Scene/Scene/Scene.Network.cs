using Sandbox.Network;
using System.Runtime.InteropServices;

namespace Sandbox;

public partial class Scene : GameObject
{
	[Obsolete( "Moved to ProjectSettings.Networking.UpdateRate" )]
	public float NetworkFrequency { get; set; }

	/// <summary>
	/// One divided by ProjectSettings.Networking.UpdateRate.
	/// </summary>
	public float NetworkRate => 1.0f / ProjectSettings.Networking.UpdateRate.Clamp( 1, 500 );

	internal readonly HashSet<NetworkObject> networkedObjects = new();
	readonly List<NetworkObject> _dirtyNetworkObjects = new();
	readonly List<NetworkObject> _networkPollBuffer = new();
	RealTimeSince _timeSinceDormancyProbe;

	/// <summary>
	/// Cumulative counts of the work done to find changes on sleeping network objects.
	/// </summary>
	internal struct NetworkPollStats
	{
		public long Ticks;
		public long ObjectsVisited;
		public long ContainerEntries;
		public long CollectionEntries;
		public long QueryEntries;
		public long VisibilityProbes;
		public long PollWakes;
		public long ProbeWakes;
	}

	internal NetworkPollStats NetworkPollTotals;

	/// <summary>
	/// The number of registered network objects in this scene.
	/// </summary>
	public int NetworkObjectCount => networkedObjects.Count;

	/// <summary>
	/// The number of objects whose visibility checks are suspended between dormancy probes.
	/// Acknowledged, visible objects also skip snapshot capture, but are not included here.
	/// </summary>
	public int NetworkDormantObjectCount => networkedObjects.Count( x => x.IsDeltaDormant );

	internal void MarkNetworkObjectDirty( NetworkObject obj )
	{
		if ( obj.IsDirty ) return;
		obj.IsDirty = true;
		_dirtyNetworkObjects.Add( obj );
	}

	internal void RegisterNetworkedObject( NetworkObject obj )
	{
		networkedObjects.Add( obj );
		MarkNetworkObjectDirty( obj );
	}

	internal void UnregisterNetworkObject( NetworkObject obj )
	{
		// When a network object is removed, we can clean up any snapshot data.
		var system = SceneNetworkSystem.Instance;
		system?.DeltaSnapshots?.ClearNetworkObject( obj );

		networkedObjects.Remove( obj );
		obj.IsDirty = false;
	}

	double _nextNetworkUpdate;

	/// <summary>
	/// Send any pending network updates at our desired <see cref="NetworkRate"/>.
	/// Retire destroyed pending objects even when no network system is active.
	/// </summary>
	internal void SceneNetworkUpdate()
	{
		_networkMapInstanceCache.Clear();
		GetAll( _networkMapInstanceCache );

		if ( SceneNetworkSystem.Instance is not { } system )
		{
			_dirtyNetworkObjects.RemoveAll( static obj => !obj.IsValid );
			return;
		}

		var now = RealTime.NowDouble;
		if ( now < _nextNetworkUpdate )
			return;

		// Kept on a schedule, waiting a full interval from whichever frame we got to would send less often than asked
		_nextNetworkUpdate += NetworkRate;
		if ( _nextNetworkUpdate <= now )
			_nextNetworkUpdate = now + NetworkRate;

		SendClientTick( system );

		system.DeltaSnapshots.UpdateTime();

		// The array is only rebuilt when the connections change
		system.CollectConnections( _sendConnectionList, Connection.ChannelState.Connected );
		if ( !CollectionsMarshal.AsSpan( _sendConnectionList ).SequenceEqual( _sendConnections ) )
		{
			var hasNewConnection = false;
			foreach ( var connection in _sendConnectionList )
			{
				if ( Array.IndexOf( _sendConnections, connection ) < 0 )
				{
					hasNewConnection = true;
					break;
				}
			}

			_sendConnections = _sendConnectionList.ToArray();

			// Leaving peers don't need a baseline; a new or reconnected peer does.
			if ( hasNewConnection )
			{
				foreach ( var obj in networkedObjects )
					obj.MarkDirty();
			}
		}

		PollCleanNetworkObjects( _sendConnections );

		// Partition objects into dirty (pending changes) and clean (fully ACK'd).
		// Dirty objects are processed first so they end up in earlier clusters,
		// which are less likely to be delayed under network congestion.
		_snapshotObjects.Clear();
		_cleanSnapshotObjects.Clear();

		foreach ( var n in _dirtyNetworkObjects )
		{
			if ( n.LocalSnapshotState.AckedByConnections < 0 )
				_snapshotObjects.Add( n );
			else
				_cleanSnapshotObjects.Add( n );
		}

		_snapshotObjects.AddRange( _cleanSnapshotObjects );

		// If we're the host, include any GameObjectSystems.
		if ( Networking.IsHost )
		{
			foreach ( var gameObjectSystem in systems.Values )
				_snapshotObjects.Add( gameObjectSystem );
		}

		system.DeltaSnapshots.Send( CollectionsMarshal.AsSpan( _snapshotObjects ), _sendConnections );

		_dirtyNetworkObjects.RemoveAll( static obj =>
		{
			if ( obj.IsValid && !obj.IsDeltaDormant && (!obj.IsFullyUpdated || !obj.CanSleep) )
				return false;

			obj.IsDirty = false;
			return true;
		} );

		system.DeltaSnapshots.Tick();
	}

	private void PollCleanNetworkObjects( Connection[] connections )
	{
		var probeVisibility = _timeSinceDormancyProbe >= NetworkObject.DormancyProbeInterval;
		if ( probeVisibility )
			_timeSinceDormancyProbe = 0f;

		_networkPollBuffer.Clear();
		foreach ( var obj in networkedObjects )
		{
			if ( !obj.IsDirty )
				_networkPollBuffer.Add( obj );
		}

		ref var stats = ref NetworkPollTotals;
		stats.Ticks++;

		// Getters can spawn or destroy objects, so never enumerate the live registration set here.
		foreach ( var obj in _networkPollBuffer )
		{
			if ( !obj.IsValid )
				continue;

			stats.ObjectsVisited++;

			if ( obj.CanPollQueryValues )
			{
				var table = obj.dataTable;
				stats.ContainerEntries += table.QueryEntryCount( NetworkTable.PollKind.Container );
				stats.CollectionEntries += table.QueryEntryCount( NetworkTable.PollKind.Collection );
				stats.QueryEntries += table.QueryEntryCount( NetworkTable.PollKind.Query );

				if ( obj.PollQueryValues() )
				{
					stats.PollWakes++;
					continue;
				}
			}

			if ( probeVisibility && obj.IsValid && obj.CanProbeVisibility )
			{
				stats.VisibilityProbes++;
				if ( obj.ProbeVisibility( connections ) )
					stats.ProbeWakes++;
			}
		}

		_networkPollBuffer.Clear();
	}

	/// <summary>
	/// Create messages for what <paramref name="source"/> should see; null source means everything.
	/// </summary>
	internal void SerializeNetworkObjects( Connection source, List<object> collection, bool includeLocalObjects = false, SnapshotCapture capture = null )
	{
		var included = new HashSet<NetworkObject>();

		foreach ( var target in networkedObjects )
		{
			if ( target.GameObject?.IsDestroyed ?? true )
				continue;

			// Filter on the root's real visibility, not ShouldTransmit which is stale for a fresh join.
			var root = target.RootNetworkObject;
			if ( source is null || target.ShouldIncludeInSnapshot( source )
				|| (root != target && (root?.ShouldIncludeInSnapshot( source ) ?? false)) )
			{
				AddWithAncestors( source, target, included, collection, includeLocalObjects, capture );
			}
		}
	}

	/// <summary>
	/// Emit the create message for <paramref name="target"/> and every networked ancestor above it,
	/// each at most once. A networked child must never be sent without its parent chain.
	/// </summary>
	internal void AddWithAncestors( Connection source, NetworkObject target, HashSet<NetworkObject> included, List<object> collection, bool includeLocalObjects, SnapshotCapture capture = null )
	{
		var current = target;
		while ( current is not null )
		{
			if ( current.GameObject?.IsDestroyed ?? true )
				break;

			if ( !included.Add( current ) )
				break;

			collection.Add( current.GetCreateMessage( includeLocalObjects, capture ) );

			if ( source is not null )
				current.MarkCreateMessageSent( source );

			current = current.GameObject.Parent?._net;
		}
	}

	/// <summary>
	/// Do appropriate actions based on the <see cref="NetworkOrphaned"/> mode for all networked objects owned by a specific connection.
	/// </summary>
	internal void DoOrphanedActions( Connection connection )
	{
		var objects = networkedObjects.Where( n => n.Owner == connection.Id ).ToArray();

		foreach ( var o in objects )
		{
			o.DoOrphanedAction();
		}
	}

	/// <summary>
	/// Send all of our visibility origins to other clients. These are points we can observe from, which helps
	/// to determine the visibility of network objects and sends a user command.
	/// </summary>
	internal void SendClientTick( SceneNetworkSystem system )
	{
		var localConnection = Connection.Local;

		// Conna: in the future we might support multiple visibility origins. For now though, we'll just
		// use the main camera position as our primary viewing source.
		if ( localConnection.VisibilityOrigins.Length == 0 )
			localConnection.VisibilityOrigins = new Vector3[1];

		localConnection.VisibilityOrigins[0] = Camera?.WorldPosition ?? default;

		var userCommand = UserCommand.Create();
		localConnection.BuildUserCommand( ref userCommand );

		// Everyone we're connected to gets our visibility origins, the host also gets our user command. The message for
		// everyone else is the same, so it's encoded once.
		NetPacket shared = null;

		system.CollectConnections( _clientTickConnections, Connection.ChannelState.Snapshot );

		foreach ( var connection in _clientTickConnections )
		{
			// Not to peers behind the host: only the host gets to know where everyone looks from
			if ( connection is MockConnection )
				continue;

			if ( connection.IsHost )
			{
				var msg = WriteClientTick( localConnection );
				userCommand.Serialize( ref msg );
				connection.SendStream( msg, NetFlags.UnreliableNoDelay );
				msg.Dispose();
				continue;
			}

			if ( shared is null )
			{
				var msg = WriteClientTick( localConnection );
				shared = Connection.EncodePacket( msg );
				msg.Dispose();
			}

			connection.Send( shared, NetFlags.UnreliableNoDelay );
		}

		shared?.Release();
	}

	static ByteStream WriteClientTick( Connection localConnection )
	{
		var msg = ByteStream.Create( 256 );
		msg.Write( InternalMessageType.ClientTick );
		msg.Write( (char)localConnection.VisibilityOrigins.Length );

		for ( var i = 0; i < localConnection.VisibilityOrigins.Length; i++ )
		{
			var source = localConnection.VisibilityOrigins[i];
			msg.Write( source.x );
			msg.Write( source.y );
			msg.Write( source.z );
		}

		return msg;
	}

	/// <summary>
	/// A cache of all the MapInstances that gets updated every frame
	/// </summary>
	readonly List<MapInstance> _networkMapInstanceCache = new();
	readonly List<IDeltaSnapshot> _snapshotObjects = new();
	readonly List<IDeltaSnapshot> _cleanSnapshotObjects = new();
	readonly List<Connection> _sendConnectionList = new();
	Connection[] _sendConnections = [];
	readonly List<Connection> _clientTickConnections = new();

	/// <summary>
	/// Are these bounds visible to the specified <see cref="Connection"/>?
	/// </summary>
	public unsafe bool IsBBoxVisibleToConnection( Connection target, BBox box )
	{
		var sources = target.VisibilityOrigins;

		foreach ( var pvs in _networkMapInstanceCache.Select( x => x.GetNetworkPvs() ) )
		{
			if ( !pvs.IsValid || pvs.IsEmptyPVS() )
				continue;

			fixed ( Vector3* sourcePtr = sources )
			{
				if ( !pvs.IsAbsBoxInPVS( sources.Length, (IntPtr)sourcePtr, box.Mins, box.Maxs ) )
					return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Is a position visible to the specified <see cref="Connection"/>?
	/// </summary>
	public unsafe bool IsPointVisibleToConnection( Connection target, Vector3 position )
	{
		var sources = target.VisibilityOrigins;

		foreach ( var pvs in _networkMapInstanceCache.Select( x => x.GetNetworkPvs() ) )
		{
			if ( !pvs.IsValid || pvs.IsEmptyPVS() )
				continue;

			fixed ( Vector3* sourcePtr = sources )
			{
				if ( !pvs.IsInPVS( sources.Length, (IntPtr)sourcePtr, position ) )
					return false;
			}
		}

		return true;
	}
}
