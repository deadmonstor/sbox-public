using System;
using Sandbox.Network;

namespace NetworkTests;

[TestClass]
[DoNotParallelize] // LocalSnapshotState uses a shared hasher on the engine's main thread.
public class SnapshotAcknowledgementTest
{
	private static DeltaSnapshot Snapshot( ushort id, int slot, byte value )
	{
		var snapshot = new DeltaSnapshot { SnapshotId = id };
		snapshot.AddSerialized( slot, [value] );
		return snapshot;
	}

	private static void ReceiveAck( Guid peer, LocalSnapshotState local, RemoteSnapshotState remote, DeltaSnapshot snapshot )
	{
		foreach ( var entry in snapshot.Entries )
			remote.Update( entry, snapshot.SnapshotId );
		local.OnSnapshotAck( peer, remote );
	}

	[TestMethod]
	public void ReorderedAckPreservesNewerAcknowledgedSlots()
	{
		var peer = Guid.NewGuid();
		var local = new LocalSnapshotState();
		var remote = new RemoteSnapshotState();
		local.AddSerialized( 1, [1] );
		local.AddSerialized( 2, [2] );
		ReceiveAck( peer, local, remote, Snapshot( 10, 1, 1 ) );
		ReceiveAck( peer, local, remote, Snapshot( 11, 2, 2 ) );
		Assert.IsTrue( local.UpdatedConnections.Contains( peer ) );

		ReceiveAck( peer, local, remote, Snapshot( 9, 1, 0 ) );
		Assert.IsTrue( local.UpdatedConnections.Contains( peer ) );
		Assert.IsTrue( local.Lookup[1].Connections.Contains( peer ) );
		Assert.IsTrue( local.Lookup[2].Connections.Contains( peer ) );
	}

	[TestMethod]
	public void DelayedAndDuplicateAcksDoNotConfirmAnUnacknowledgedMutation()
	{
		var peer = Guid.NewGuid();
		var local = new LocalSnapshotState();
		var remote = new RemoteSnapshotState();
		local.AddSerialized( 1, [1] );
		var initial = Snapshot( 1, 1, 1 );
		ReceiveAck( peer, local, remote, initial );
		local.AddSerialized( 1, [2] );
		ReceiveAck( peer, local, remote, initial );
		Assert.IsFalse( local.UpdatedConnections.Contains( peer ) );
		Assert.IsFalse( local.Lookup[1].Connections.Contains( peer ) );

		var latest = Snapshot( 2, 1, 2 );
		ReceiveAck( peer, local, remote, latest );
		ReceiveAck( peer, local, remote, latest );
		Assert.IsTrue( local.UpdatedConnections.Contains( peer ) );
		Assert.AreEqual( 1, local.Lookup[1].Connections.Count );
	}

	[TestMethod]
	public void MissingAckAndPredictionDoNotCountAsAcknowledged()
	{
		var peer = Guid.NewGuid();
		var local = new LocalSnapshotState();
		var remote = new RemoteSnapshotState();
		local.AddSerialized( 1, [1] );
		local.AddSerialized( 2, [2] );
		var missing = Snapshot( 1, 2, 2 );
		remote.AddPredicted( missing.Entries[0], 1, 0 );
		ReceiveAck( peer, local, remote, Snapshot( 2, 1, 1 ) );
		Assert.IsFalse( local.UpdatedConnections.Contains( peer ) );
		Assert.IsFalse( local.Lookup[2].Connections.Contains( peer ) );
		ReceiveAck( peer, local, remote, Snapshot( 3, 2, 2 ) );
		Assert.IsTrue( local.UpdatedConnections.Contains( peer ) );
	}

	[TestMethod]
	public void MatchingOlderValueDoesNotConfirmValuesStillInFlight()
	{
		var peer = Guid.NewGuid();
		var local = new LocalSnapshotState();
		var remote = new RemoteSnapshotState();
		local.AddSerialized( 1, [1] );
		local.AddSerialized( 2, [2] );
		ReceiveAck( peer, local, remote, Snapshot( 19, 2, 2 ) );
		ReceiveAck( peer, local, remote, Snapshot( 20, 1, 1 ) );

		local.AddSerialized( 1, [3] );
		remote.AddPredicted( Snapshot( 21, 1, 3 ).Entries[0], 21, 0 );
		local.AddSerialized( 1, [1] );
		var latest = Snapshot( 22, 1, 1 );
		remote.AddPredicted( latest.Entries[0], 22, 0 );
		ReceiveAck( peer, local, remote, Snapshot( 18, 2, 2 ) );
		Assert.IsFalse( local.UpdatedConnections.Contains( peer ) );
		Assert.IsFalse( local.Lookup[1].Connections.Contains( peer ) );

		ReceiveAck( peer, local, remote, latest );
		Assert.IsTrue( local.UpdatedConnections.Contains( peer ) );
	}

	[TestMethod]
	public void WrappedAckKeepsNewerValueAfterDelayedPreWrapAck()
	{
		var peer = Guid.NewGuid();
		var local = new LocalSnapshotState();
		var remote = new RemoteSnapshotState();
		local.AddSerialized( 1, [1] );
		var beforeWrap = Snapshot( ushort.MaxValue, 1, 1 );
		ReceiveAck( peer, local, remote, beforeWrap );
		local.AddSerialized( 1, [2] );
		ReceiveAck( peer, local, remote, Snapshot( 0, 1, 2 ) );
		ReceiveAck( peer, local, remote, beforeWrap );
		Assert.IsTrue( local.UpdatedConnections.Contains( peer ) );
		Assert.AreEqual( (ushort)0, remote.Data[1].SnapshotId );
	}

	[TestMethod]
	public void RemovedAndReusedSlotRequiresItsCurrentValue()
	{
		var peer = Guid.NewGuid();
		var local = new LocalSnapshotState();
		var remote = new RemoteSnapshotState();
		local.AddSerialized( 1, [1] );
		ReceiveAck( peer, local, remote, Snapshot( 1, 1, 1 ) );
		local.Remove( 1 );
		local.AddSerialized( 1, [2] );
		ReceiveAck( peer, local, remote, Snapshot( 1, 1, 1 ) );
		Assert.IsFalse( local.UpdatedConnections.Contains( peer ) );
		ReceiveAck( peer, local, remote, Snapshot( 2, 1, 2 ) );
		Assert.IsTrue( local.UpdatedConnections.Contains( peer ) );
	}

	[TestMethod]
	public void ValueReturningToKnownStateCanBeConfirmedByAnotherSlotAck()
	{
		var peer = Guid.NewGuid();
		var local = new LocalSnapshotState();
		var remote = new RemoteSnapshotState();
		local.AddSerialized( 1, [1] );
		local.AddSerialized( 2, [2] );
		ReceiveAck( peer, local, remote, Snapshot( 1, 1, 1 ) );
		local.AddSerialized( 1, [3] );
		local.AddSerialized( 1, [1] );
		ReceiveAck( peer, local, remote, Snapshot( 2, 2, 2 ) );
		Assert.IsTrue( local.UpdatedConnections.Contains( peer ) );
	}
}
