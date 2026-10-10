using Sandbox.Network;

namespace Sandbox;

public partial class Scene
{
	readonly HashSet<NetworkObject> _pendingSnapshotObjects = [];
	readonly HashSet<NetworkObject> _continuousSnapshotObjects = [];
	readonly HashSet<NetworkObject> _snapshotWork = [];
	readonly List<NetworkObject> _snapshotCandidates = [];
	readonly HashSet<Guid> _snapshotConnections = [];
	SceneNetworkSystem _snapshotSystem;
	Guid _snapshotLocalConnection;
	bool _snapshotIsHost;

	internal void ScheduleSnapshot( NetworkObject obj )
	{
		if ( !networkedObjects.Contains( obj ) ) return;

		// Further notifications still advance the object's revision, but need no queue work.
		if ( !_pendingSnapshotObjects.Add( obj ) ) return;
		RefreshContinuousSnapshots( obj );
	}

	void RefreshContinuousSnapshots( NetworkObject obj )
	{
		if ( obj.RequiresContinuousSnapshots )
			_continuousSnapshotObjects.Add( obj );
		else
			_continuousSnapshotObjects.Remove( obj );
	}

	internal void ResetSnapshotScheduling()
	{
		foreach ( var obj in networkedObjects )
			obj.ScheduleSnapshot();
	}

	internal void SendScheduledSnapshots( SceneNetworkSystem system, Connection[] connections )
	{
		// Changes to recipients or authority can require delivery of otherwise unchanged state.
		if ( _snapshotSystem != system || _snapshotLocalConnection != Connection.Local.Id || _snapshotIsHost != Networking.IsHost
			|| connections.Length != _snapshotConnections.Count || connections.Any( c => !_snapshotConnections.Contains( c.Id ) ) )
		{
			_snapshotSystem = system;
			_snapshotLocalConnection = Connection.Local.Id;
			_snapshotIsHost = Networking.IsHost;
			_snapshotConnections.Clear();
			foreach ( var connection in connections )
				_snapshotConnections.Add( connection.Id );
			ResetSnapshotScheduling();
		}

		_snapshotWork.Clear();
		_snapshotWork.UnionWith( _pendingSnapshotObjects );
		_snapshotWork.UnionWith( _continuousSnapshotObjects );
		_snapshotCandidates.Clear();
		_snapshotCandidates.AddRange( _snapshotWork );

		IEnumerable<IDeltaSnapshot> objects = GetScheduledSnapshots( connections );
		if ( Networking.IsHost )
			objects = objects.Concat( systems.Values );

		try
		{
			system.DeltaSnapshots.Send( objects, connections );
		}
		catch
		{
			// A failing getter must not silently discard the remaining scheduled work.
			foreach ( var obj in _snapshotCandidates )
				if ( networkedObjects.Contains( obj ) ) _pendingSnapshotObjects.Add( obj );
			throw;
		}
		finally
		{
			_snapshotCandidates.Clear();
			_snapshotWork.Clear();
		}
	}

	IEnumerable<IDeltaSnapshot> GetScheduledSnapshots( Connection[] connections )
	{
		// Use a stable list: preparing one object can mutate, spawn or destroy another.
		foreach ( var obj in _snapshotCandidates )
		{
			if ( !networkedObjects.Contains( obj ) || !obj.IsValid ) continue;

			_pendingSnapshotObjects.Remove( obj );
			// Polling requirements may have changed while notifications were coalesced.
			RefreshContinuousSnapshots( obj );
			if ( !_continuousSnapshotObjects.Contains( obj ) && !obj.NeedsSnapshotUpdate( connections ) ) continue;

			yield return obj;

			if ( networkedObjects.Contains( obj ) && obj.NeedsSnapshotUpdate( connections ) )
				_pendingSnapshotObjects.Add( obj );
		}
	}
}
