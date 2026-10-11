using System;
using Sandbox.Network;

namespace NetworkTests;

[TestClass]
public class ConnectionMaskTest
{
	[TestMethod]
	public void PartialAckCannotCreditChangedUnsentOrRecreatedSlot()
	{
		var state = new LocalSnapshotState();
		state.AddSerialized( 1, [1] );
		state.AddSerialized( 2, [2] );
		ConnectionMask active = default;
		active.Add( 0 );
		var snapshot = DeltaSnapshot.Pool.Rent();
		try
		{
			snapshot.CopyFrom( null, state, active );
			ref var sent = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan( snapshot.Entries )[0];
			sent.Connections.Add( 0 );
			var remote = new RemoteSnapshotState();
			foreach ( var entry in snapshot.Entries ) remote.Update( entry, 0 );
			state.AddSerialized( 2, [3] );
			Assert.IsFalse( state.Acknowledge( 0, snapshot, remote ) );
			Assert.IsTrue( state.Lookup[1].Connections.Contains( 0 ) );
			Assert.IsFalse( state.Lookup[2].Connections.Contains( 0 ) );
			state.Remove( 1 );
			state.AddSerialized( 1, [4] );
			Assert.IsFalse( state.Acknowledge( 0, snapshot, remote ) );
			Assert.IsFalse( state.Lookup[1].Connections.Contains( 0 ), "An old snapshot must compare the recreated slot's current hash." );
		}
		finally { snapshot.Release(); }
	}

	[TestMethod]
	[DataRow( 0 )]
	[DataRow( 63 )]
	[DataRow( 64 )]
	[DataRow( 127 )]
	[DataRow( 128 )]
	public void MembershipClearAndActiveSubset( int index )
	{
		ConnectionMask receipts = default, active = default;
		receipts.Add( index );
		receipts.Add( index + 1 );
		active.Add( index );
		Assert.IsTrue( receipts.ContainsAll( active ) );
		Assert.IsTrue( receipts.Contains( index ) );
		receipts.Remove( index );
		Assert.IsFalse( receipts.ContainsAll( active ), "Stale bits must not substitute for an active peer." );
		Assert.IsTrue( receipts.Contains( index + 1 ) );
		receipts.Clear();
		Assert.AreEqual( 0, receipts.Count );
		Assert.IsFalse( receipts.Contains( index + 1 ) );
	}

	[TestMethod]
	public void RebindingClearsOldIndexNamespace()
	{
		var oldIndices = new ConnectionIndices();
		var id = Guid.NewGuid();
		var state = new LocalSnapshotState();
		state.BindConnections( oldIndices );
		var index = oldIndices.GetOrAdd( id );
		state.AddSerialized( 42, [1] );
		state.Lookup[42].Connections.Add( index );
		state.UpdatedConnections.Add( index );
		Assert.IsTrue( state.IsUpdated( id ) );
		state.BindConnections( new ConnectionIndices() );
		Assert.AreEqual( 0, state.Lookup[42].Connections.Count );
		Assert.AreEqual( 0, state.UpdatedConnections.Count );
		Assert.IsFalse( state.IsUpdated( id ) );
	}

	[TestMethod]
	public void CopyFromRequiresActivePeerRatherThanMatchingCount()
	{
		var state = new LocalSnapshotState();
		state.AddSerialized( 42, [1] );
		state.Lookup[42].Connections.Add( 0 );
		ConnectionMask active = default;
		active.Add( 64 );
		var snapshot = DeltaSnapshot.Pool.Rent();
		try
		{
			snapshot.CopyFrom( null, state, active );
			Assert.AreEqual( 1, snapshot.Entries.Count );
		}
		finally { snapshot.Release(); }
	}
}
