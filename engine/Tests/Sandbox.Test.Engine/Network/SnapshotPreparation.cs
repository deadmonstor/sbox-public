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

	[TestMethod]
	public void DeltaTableMutationsNotifyParentWithoutPolling()
	{
		var values = new Sandbox.NetworkTable<int>();
		var getterCalls = 0;
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry
		{
			TargetType = values.GetType(),
			GetValue = () => { getterCalls++; return values; }
		} );

		Assert.IsTrue( table.CanReuseSnapshotState );
		var revision = table.SnapshotRevision;
		var calls = getterCalls;
		table.QueryValues();
		Assert.AreEqual( calls, getterCalls );
		Assert.AreEqual( revision, table.SnapshotRevision );

		values.Set( 1, 7 );
		Assert.AreNotEqual( revision, table.SnapshotRevision );
		var snapshot = new LocalSnapshotState();
		table.WriteSnapshotState( snapshot );
		var slot = "42.1".FastHash();
		Assert.IsTrue( snapshot.Lookup.ContainsKey( slot ), "The property must bind to its registered slot, not slot zero." );
		var hash = snapshot.Lookup[slot].Hash;

		revision = table.SnapshotRevision;
		values.Set( 1, 7 );
		Assert.AreEqual( revision, table.SnapshotRevision );
		values.Set( 1, 8 );
		Assert.AreNotEqual( revision, table.SnapshotRevision );
		table.WriteSnapshotState( snapshot );
		Assert.AreNotEqual( hash, snapshot.Lookup[slot].Hash );

		revision = table.SnapshotRevision;
		values.Clear();
		Assert.AreNotEqual( revision, table.SnapshotRevision );
	}

	[TestMethod]
	public void ReplacedAndUnregisteredDeltaTablesStopNotifyingParent()
	{
		var previous = new Sandbox.NetworkTable<int>();
		var current = previous;
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry { TargetType = current.GetType(), GetValue = () => current } );
		var revision = table.SnapshotRevision;
		current = new();
		table.UpdateSlotHash( 42, current );
		Assert.AreNotEqual( revision, table.SnapshotRevision );

		revision = table.SnapshotRevision;
		previous.Set( 1, 1 );
		Assert.AreEqual( revision, table.SnapshotRevision );
		current.Set( 1, 2 );
		Assert.AreNotEqual( revision, table.SnapshotRevision );

		table.Unregister( 42 );
		revision = table.SnapshotRevision;
		current.Set( 1, 3 );
		Assert.AreEqual( revision, table.SnapshotRevision );
	}

	[TestMethod]
	public void ExplicitQueryStillPollsTrackedDeltaProperties()
	{
		var values = new Sandbox.NetworkTable<int>();
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry { TargetType = values.GetType(), NeedsQuery = true, GetValue = () => values } );
		Assert.IsFalse( table.CanReuseSnapshotState );
	}

	[TestMethod]
	public void ReliableQueriesDoNotPreventSnapshotReuse()
	{
		using var values = new NetDictionary<string, int>();
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry { TargetType = values.GetType(), GetValue = () => values } );
		Assert.IsTrue( table.CanReuseSnapshotState );
		var stream = ByteStream.Create( 128 );
		try
		{
			table.WriteReliableChanged( ref stream );
			Assert.IsFalse( table.HasReliableChanges() );
			values["ammo"] = 7;
			table.QueryValues();
			Assert.IsTrue( table.HasReliableChanges() );
		}
		finally
		{
			stream.Dispose();
		}
	}

	[TestMethod]
	public void ReceivedDeltaChangesInvalidatePreparedState()
	{
		var source = new Sandbox.NetworkTable<int>();
		var target = new Sandbox.NetworkTable<int>();
		using var sourceTable = new NetworkTable();
		using var targetTable = new NetworkTable();
		sourceTable.Register( 42, new NetworkTable.Entry { TargetType = source.GetType(), GetValue = () => source } );
		targetTable.Register( 42, new NetworkTable.Entry { TargetType = target.GetType(), GetValue = () => target } );
		source.Set( 1, 7 );
		var snapshot = new LocalSnapshotState();
		sourceTable.WriteSnapshotState( snapshot );
		var delta = DeltaSnapshot.From( snapshot.Lookup.ToDictionary( x => x.Key, x => x.Value.Value ) );
		try
		{
			var revision = targetTable.SnapshotRevision;
			((INetworkDeltaSnapshot)target).ReadSnapshot( 42, delta );
			Assert.AreNotEqual( revision, targetTable.SnapshotRevision );
			Assert.AreEqual( 7, target.Get( 1 ) );
		}
		finally
		{
			delta.Release();
		}
	}

	private sealed class CustomDelta : INetworkDeltaSnapshot
	{
		public void WriteSnapshotState( int slot, LocalSnapshotState snapshot ) { }
		public void ReadSnapshot( int slot, DeltaSnapshot snapshot ) { }
	}
}
