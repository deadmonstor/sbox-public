using Sandbox.Network;

namespace Sandbox;

internal sealed partial class NetworkObject
{
	ulong _scheduleRevision;
	ulong _preparedScheduleRevision;

	// Visibility and external transform proxies can change without a property notification.
	internal bool RequiresContinuousSnapshots => dataTable.RequiresPolling || !GameObject.Network.AlwaysTransmit || GameObject.Transform.Proxy is not null;

	internal void ScheduleSnapshot()
	{
		_scheduleRevision++;
		GameObject?.Scene?.ScheduleSnapshot( this );
	}

	internal bool NeedsSnapshotUpdate( Connection[] connections )
	{
		if ( IsProxy && !Networking.IsHost )
			return false;

		IDeltaSnapshot snapshotter = this;
		foreach ( var connection in connections )
		{
			if ( !snapshotter.ShouldTransmit( connection ) )
				continue;

			if ( !_hasPreparedSnapshot || _scheduleRevision != _preparedScheduleRevision
				|| dataTable != _preparedSnapshot.Table || dataTable.SnapshotRevision != _preparedSnapshot.Revision
				|| _clearInterpolationFlag || _preparedSnapshot.ClearInterpolation
				|| (LocalSnapshotState.Size > 0 && !LocalSnapshotState.UpdatedConnections.Contains( connection.Id )) )
				return true;
		}

		return false;
	}
}
