using Sandbox.Network;

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

	internal void RegisterNetworkedObject( NetworkObject obj )
	{
		networkedObjects.Add( obj );
		ScheduleSnapshot( obj );
	}

	internal void UnregisterNetworkObject( NetworkObject obj )
	{
		// When a network object is removed, we can clean up any snapshot data.
		var system = SceneNetworkSystem.Instance;
		system?.DeltaSnapshots?.ClearNetworkObject( obj );

		networkedObjects.Remove( obj );
		_pendingSnapshotObjects.Remove( obj );
		_continuousSnapshotObjects.Remove( obj );
	}

	RealTimeSince _timeSinceNetworkUpdate = 0f;

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

		SendScheduledSnapshots( system, connectionsArray );

		system.DeltaSnapshots.Tick();
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
