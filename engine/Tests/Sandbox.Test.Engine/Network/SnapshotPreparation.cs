using Sandbox.Network;

namespace NetworkTests;

[TestClass]
public class SnapshotPreparationTest
{
	[TestMethod]
	public void ScalarMutationInvalidatesPreparedState()
	{
		var value = 7;
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry { TargetType = typeof( int ), GetValue = () => value } );
		var snapshot = new LocalSnapshotState();
		table.WriteSnapshotState( snapshot );
		var revision = table.SnapshotRevision;
		var hash = snapshot.Lookup[42].Hash;

		Assert.IsTrue( table.CanReuseSnapshotState );
		table.UpdateSlotHash( 42, value );
		Assert.AreEqual( revision, table.SnapshotRevision );
		value = 8;
		table.UpdateSlotHash( 42, value );
		Assert.AreNotEqual( revision, table.SnapshotRevision );
		table.WriteSnapshotState( snapshot );
		Assert.AreNotEqual( hash, snapshot.Lookup[42].Hash );
	}

	[TestMethod]
	public void QueryAndCustomDeltaEntriesRequirePolling()
	{
		var value = 1;
		using var table = new NetworkTable();
		table.Register( 1, new NetworkTable.Entry { TargetType = typeof( int ), NeedsQuery = true, GetValue = () => value } );
		Assert.IsFalse( table.CanReuseSnapshotState );
		var revision = table.SnapshotRevision;
		value = 2;
		table.QueryValues();
		Assert.AreNotEqual( revision, table.SnapshotRevision );
		table.Unregister( 1 );
		Assert.IsTrue( table.CanReuseSnapshotState );

		var custom = new CustomDelta();
		table.Register( 2, new NetworkTable.Entry { TargetType = typeof( CustomDelta ), GetValue = () => custom } );
		Assert.IsFalse( table.CanReuseSnapshotState );
		table.Unregister( 2 );
		Assert.IsTrue( table.CanReuseSnapshotState );
	}

	[TestMethod]
	public void RegistrationAndRemovalInvalidatePreparedState()
	{
		using var table = new NetworkTable();
		var revision = table.SnapshotRevision;
		table.Register( 1, new NetworkTable.Entry { TargetType = typeof( int ), GetValue = () => 1 } );
		Assert.AreNotEqual( revision, table.SnapshotRevision );
		revision = table.SnapshotRevision;
		table.Register( 1, new NetworkTable.Entry { TargetType = typeof( int ), GetValue = () => 2 } );
		Assert.AreEqual( revision, table.SnapshotRevision );
		table.Unregister( 1 );
		Assert.AreNotEqual( revision, table.SnapshotRevision );
	}

	private sealed class CustomDelta : INetworkDeltaSnapshot
	{
		public void WriteSnapshotState( int slot, LocalSnapshotState snapshot ) { }
		public void ReadSnapshot( int slot, DeltaSnapshot snapshot ) { }
	}
}
