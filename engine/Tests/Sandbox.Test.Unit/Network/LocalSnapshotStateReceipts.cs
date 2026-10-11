using System;
using System.Runtime.InteropServices;
using Sandbox.Network;

namespace NetworkTests;

[TestClass]
public class LocalSnapshotStateReceiptTest
{
	[TestMethod]
	public void AcknowledgeCreditsOnlyDeliveredCurrentValues()
	{
		var state = new LocalSnapshotState();
		state.AddSerialized( 1, [1] );
		state.AddSerialized( 2, [2] );
		state.AddSerialized( 3, [3] );
		ConnectionMask active = default;
		active.Add( 0 );
		var snapshot = new DeltaSnapshot();
		snapshot.CopyFrom( null, state, active );

		var remote = new RemoteSnapshotState();
		foreach ( ref var entry in CollectionsMarshal.AsSpan( snapshot.Entries ) )
		{
			if ( entry.Slot == 3 ) continue;
			entry.Connections.Add( 0 );
			remote.Update( entry, snapshot.SnapshotId );
		}
		state.AddSerialized( 2, [4] );

		state.Acknowledge( 0, snapshot, remote );

		Assert.IsTrue( state.Lookup[1].Connections.Contains( 0 ) );
		Assert.IsFalse( state.Lookup[2].Connections.Contains( 0 ) );
		Assert.IsFalse( state.Lookup[3].Connections.Contains( 0 ) );
		Assert.IsFalse( state.UpdatedConnections.Contains( 0 ) );
	}

	[TestMethod]
	public void RebindingClearsReceiptsFromOldIndices()
	{
		var indices = new ConnectionIndices();
		var id = Guid.NewGuid();
		var index = indices.GetOrAdd( id );
		var state = new LocalSnapshotState();
		state.BindConnections( indices );
		state.AddSerialized( 42, [1] );
		state.Lookup[42].Connections.Add( index );
		state.UpdatedConnections.Add( index );

		state.BindConnections( new ConnectionIndices() );

		Assert.AreEqual( 0, state.Lookup[42].Connections.Count );
		Assert.AreEqual( 0, state.UpdatedConnections.Count );
	}
}
