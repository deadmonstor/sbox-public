using Sandbox.Network;
using System.Linq;

namespace Sandbox;

public partial class Scene : GameObject
{
	[Obsolete( "Moved to ProjectSettings.Networking.UpdateRate" )]
	public float NetworkFrequency { get; set; }

	/// <summary>
	/// One divided by ProjectSettings.Networking.UpdateRate.
	/// </summary>
	public float NetworkRate => 1.0f / ProjectSettings.Networking.UpdateRate.Clamp( 1, 500 );

	/// <summary>
	/// The total number of networked objects in this scene.
	/// </summary>
	public int NetworkObjectCount => networkedObjects.Count;

	/// <summary>
	/// The number of networked objects that are dormant and not being transmitted.
	/// </summary>
	public int NetworkDormantObjectCount => networkedObjects.Count( x => x.IsDeltaDormant );

	internal readonly HashSet<NetworkObject> networkedObjects = new();
	readonly HashSet<NetworkObject> _dirtyNetworkObjects = new();

	internal void MarkNetworkObjectDirty( NetworkObject obj )
	{
		if ( obj.IsDirty ) return;
		obj.IsDirty = true;
		_dirtyNetworkObjects.Add( obj );
	}

	internal void RegisterNetworkedObject( NetworkObject obj )
	{
		networkedObjects.Add( obj );

		if ( !obj.IsDirty )
		{
			obj.IsDirty = true;
			_dirtyNetworkObjects.Add( obj );
		}
	}

	internal void UnregisterNetworkObject( NetworkObject obj )
	{
		// When a network object is removed, we can clean up any snapshot data.
		var system = SceneNetworkSystem.Instance;
		system?.DeltaSnapshots?.ClearNetworkObject( obj );

		networkedObjects.Remove( obj );

		if ( _dirtyNetworkObjects.Remove( obj ) )
		{
			obj.IsDirty = false;
		}
	}

	RealTimeSince _timeSinceNetworkUpdate = 0f;
	RealTimeSince _timeSinceDormancyProbe = 0f;
	readonly HashSet<Guid> _lastConnectionIds = new();
	readonly List<NetworkObject> _pollBuffer = new();

	/// <summary>
	/// Send any pending network updates at our desired <see cref="NetworkRate"/>.
	/// </summary>
	internal void SceneNetworkUpdate()
	{
		_networkMapInstanceCache.Clear();
		GetAll( _networkMapInstanceCache );

		if ( SceneNetworkSystem.Instance is not { } system )
			return;

		if ( _timeSinceNetworkUpdate < NetworkRate )
			return;

		_timeSinceNetworkUpdate = 0f;

		SendClientTick( system );

		system.DeltaSnapshots.UpdateTime();

		var connections = system.GetFilteredConnections( Connection.ChannelState.Connected );
		var connectionsArray = connections as Connection[] ?? connections.ToArray();
		var shouldProbeDormancy = _timeSinceDormancyProbe >= 0.2f;

		var hasNewConnection = false;
		foreach ( var c in connectionsArray )
		{
			if ( !_lastConnectionIds.Contains( c.Id ) )
				hasNewConnection = true;
		}

		if ( hasNewConnection || _lastConnectionIds.Count != connectionsArray.Length )
		{
			_lastConnectionIds.Clear();
			foreach ( var c in connectionsArray )
			{
				_lastConnectionIds.Add( c.Id );
			}
		}

		if ( hasNewConnection )
		{
			foreach ( var n in networkedObjects )
			{
				if ( n.IsDirty ) continue;
				n.IsDirty = true;
				_dirtyNetworkObjects.Add( n );
			}
		}

		if ( shouldProbeDormancy )
			_timeSinceDormancyProbe = 0f;

		_pollBuffer.Clear();

		// Polling runs game code (property getters), which is free to spawn or destroy networked
		// objects, so work from a copy instead of enumerating the live set.
		foreach ( var n in networkedObjects )
		{
			if ( n.IsDirty )
				continue;

			_pollBuffer.Add( n );
		}

		// Some sync vars can only be change-detected by polling them, and polling only happens
		// while an object is being sent - so a clean object has to be polled here to wake back up.
		foreach ( var n in _pollBuffer )
		{
			if ( n.PollQueryValues() )
				continue;

			if ( shouldProbeDormancy )
				n.ProbeVisibility( connectionsArray );
		}

		_pollBuffer.Clear();

		// Partition objects into dirty (pending changes) and clean (fully ACK'd).
		// Dirty objects are processed first so they end up in earlier clusters,
		// which are less likely to be delayed under network congestion.
		_dirtySnapshotObjects.Clear();
		_cleanSnapshotObjects.Clear();

		foreach ( var n in _dirtyNetworkObjects )
		{
			if ( n.LocalSnapshotState.UpdatedConnections.Count == 0 )
				_dirtySnapshotObjects.Add( n );
			else
				_cleanSnapshotObjects.Add( n );
		}

		IEnumerable<IDeltaSnapshot> objects = _dirtySnapshotObjects.Concat( _cleanSnapshotObjects );

		// If we're the host, include any GameObjectSystems.
		if ( Networking.IsHost )
			objects = objects.Concat( systems.Values );

		system.DeltaSnapshots.Send( objects, connectionsArray );

		_dirtyNetworkObjects.RemoveWhere( n =>
		{
			if ( n.IsFullyUpdated || n.IsDeltaDormant )
			{
				n.IsDirty = false;
				return true;
			}

			return false;
		} );

		system.DeltaSnapshots.Tick();
	}

	internal void SerializeNetworkObjects( Connection source, List<object> collection )
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
				AddWithAncestors( source, target, included, collection );
			}
		}
	}

	/// <summary>
	/// Emit the create message for <paramref name="target"/> and every networked ancestor above it,
	/// each at most once. A networked child must never be sent without its parent chain.
	/// </summary>
	internal void AddWithAncestors( Connection source, NetworkObject target, HashSet<NetworkObject> included, List<object> collection )
	{
		var current = target;
		while ( current is not null )
		{
			if ( current.GameObject?.IsDestroyed ?? true )
				break;

			if ( !included.Add( current ) )
				break;

			collection.Add( current.GetCreateMessage() );

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

		foreach ( var connection in system.GetFilteredConnections() )
		{
			var msg = ByteStream.Create( 256 );
			msg.Write( InternalMessageType.ClientTick );

			// Broadcast our visibility origins to everyone
			{
				msg.Write( (char)localConnection.VisibilityOrigins.Length );

				for ( var i = 0; i < localConnection.VisibilityOrigins.Length; i++ )
				{
					var source = localConnection.VisibilityOrigins[i];
					msg.Write( source.x );
					msg.Write( source.y );
					msg.Write( source.z );
				}
			}

			if ( connection.IsHost )
			{
				userCommand.Serialize( ref msg );
			}

			connection.SendStream( msg, NetFlags.UnreliableNoDelay );
			msg.Dispose();
		}
	}

	/// <summary>
	/// A cache of all the MapInstances that gets updated every frame
	/// </summary>
	readonly List<MapInstance> _networkMapInstanceCache = new();
	readonly List<IDeltaSnapshot> _dirtySnapshotObjects = new();
	readonly List<IDeltaSnapshot> _cleanSnapshotObjects = new();

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
