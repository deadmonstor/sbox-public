using Sandbox.Network;

namespace NetworkTests;

[TestClass]
public class ConnectionMaskTest
{
	[TestMethod]
	[DataRow( 0 )]
	[DataRow( 63 )]
	[DataRow( 64 )]
	[DataRow( 127 )]
	[DataRow( 128 )]
	public void AddRemoveAndContainsAll( int index )
	{
		ConnectionMask receipts = default;
		ConnectionMask active = default;
		receipts.Add( index );
		receipts.Add( index + 1 );
		active.Add( index );

		Assert.IsTrue( receipts.ContainsAll( active ) );
		Assert.AreEqual( 2, receipts.Count );

		receipts.Remove( index );

		Assert.IsFalse( receipts.ContainsAll( active ) );
		Assert.IsTrue( receipts.Contains( index + 1 ) );
		Assert.AreEqual( 1, receipts.Count );

		receipts.Clear();

		Assert.IsFalse( receipts.Contains( index + 1 ) );
		Assert.AreEqual( 0, receipts.Count );
	}

	[TestMethod]
	public void CopyFromSendsSlotMissingAnActiveReceipt()
	{
		var state = new LocalSnapshotState();
		state.AddSerialized( 42, [1] );
		state.Lookup[42].Connections.Add( 0 );
		ConnectionMask active = default;
		active.Add( 64 );

		var snapshot = new DeltaSnapshot();
		snapshot.CopyFrom( null, state, active );

		Assert.AreEqual( 1, snapshot.Entries.Count );
	}
}
