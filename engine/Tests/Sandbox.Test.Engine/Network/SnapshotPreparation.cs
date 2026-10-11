using System;
using System.Collections.Generic;
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

	[TestMethod]
	public void PollingEntryDoesNotPrepareUnchangedDeltaProperties()
	{
		var values = new CountingDeltaTable();
		values.Set( 1, 7 );
		var list = new List<int> { 1 };
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry { TargetType = values.GetType(), GetValue = () => values } );
		table.Register( 43, new NetworkTable.Entry { TargetType = list.GetType(), GetValue = () => list } );
		var snapshot = new LocalSnapshotState();
		table.QueryValues();
		table.WriteSnapshotState( snapshot );
		Assert.IsFalse( table.CanReuseSnapshotState );
		var listHash = snapshot.Lookup[43].Hash;
		var slot = "42.1".FastHash();
		var deltaEntry = snapshot.Lookup[slot];
		var connection = 0;
		deltaEntry.Connections.Add( connection );

		list.Add( 2 );
		table.QueryValues();
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 1, values.WriteCalls );
		Assert.AreNotEqual( listHash, snapshot.Lookup[43].Hash );
		Assert.IsTrue( deltaEntry.Connections.Contains( connection ), "Reuse must preserve acknowledgements for retransmission filtering." );

		values.Set( 1, 8 );
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 2, values.WriteCalls );
		Assert.IsFalse( deltaEntry.Connections.Contains( connection ) );
	}

	[TestMethod]
	public void MutationOnlyPreparesTheChangedDeltaEntry()
	{
		var first = new CountingDeltaTable();
		var second = new CountingDeltaTable();
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry { TargetType = first.GetType(), GetValue = () => first } );
		table.Register( 43, new NetworkTable.Entry { TargetType = second.GetType(), GetValue = () => second } );
		var snapshot = new LocalSnapshotState();
		table.WriteSnapshotState( snapshot );
		first.Set( 1, 7 );
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 2, first.WriteCalls );
		Assert.AreEqual( 1, second.WriteCalls );
	}

	[TestMethod]
	public void DeltaPreparationInvalidatesOnContextAndAuthorityChanges()
	{
		var values = new CountingDeltaTable();
		values.Set( 1, 7 );
		var controlled = true;
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry
		{
			TargetType = values.GetType(),
			GetValue = () => values,
			ControlCondition = _ => controlled
		} );
		var snapshot = new LocalSnapshotState();
		table.WriteSnapshotState( snapshot );
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 1, values.WriteCalls );

		snapshot.ParentId = Guid.NewGuid();
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 2, values.WriteCalls );
		snapshot.Flags = NetworkFlags.NoPositionSync;
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 3, values.WriteCalls );
		snapshot.Version++;
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 4, values.WriteCalls );
		controlled = false;
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 4, values.WriteCalls );
		controlled = true;
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 5, values.WriteCalls );

		var otherSnapshot = new LocalSnapshotState();
		table.WriteSnapshotState( otherSnapshot );
		Assert.AreEqual( 6, values.WriteCalls );
		Assert.IsTrue( otherSnapshot.Lookup.ContainsKey( "42.1".FastHash() ) );
	}

	[TestMethod]
	public void CachedBodyTransformsRehashWhenParentOrFlagsChange()
	{
		var values = new NetworkTransforms();
		values.Set( 1, new Transform( Vector3.One, Rotation.Identity ) );
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry { TargetType = values.GetType(), GetValue = () => values } );
		var snapshot = new LocalSnapshotState { ParentId = Guid.NewGuid(), Flags = NetworkFlags.None };
		snapshot.Begin();
		table.WriteSnapshotState( snapshot );
		var slot = "42.1.position".FastHash();
		var hash = snapshot.Lookup[slot].Hash;

		snapshot.Begin();
		snapshot.ParentId = Guid.NewGuid();
		table.WriteSnapshotState( snapshot );
		Assert.AreNotEqual( hash, snapshot.Lookup[slot].Hash );
		hash = snapshot.Lookup[slot].Hash;
		snapshot.Begin();
		snapshot.Flags = NetworkFlags.NoRotationSync;
		table.WriteSnapshotState( snapshot );
		Assert.AreNotEqual( hash, snapshot.Lookup[slot].Hash );

		var otherSnapshot = new LocalSnapshotState();
		table.WriteSnapshotState( otherSnapshot );
		Assert.IsTrue( otherSnapshot.Lookup.ContainsKey( slot ), "A different snapshot needs the cached body transforms inserted again." );
	}

	[TestMethod]
	public void MutationDuringDeltaPreparationRemainsPending()
	{
		var values = new CountingDeltaTable();
		values.Set( 1, 7 );
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry { TargetType = values.GetType(), GetValue = () => values } );
		values.AfterWrite = () => values.Set( 1, 8 );
		var snapshot = new LocalSnapshotState();
		table.WriteSnapshotState( snapshot );
		var hash = snapshot.Lookup["42.1".FastHash()].Hash;
		values.AfterWrite = null;
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 2, values.WriteCalls );
		Assert.AreNotEqual( hash, snapshot.Lookup["42.1".FastHash()].Hash );
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 2, values.WriteCalls );
	}

	[TestMethod]
	public void UntrackedCustomDeltaPropertiesAlwaysPrepare()
	{
		var values = new CustomDelta();
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry { TargetType = values.GetType(), GetValue = () => values } );
		var snapshot = new LocalSnapshotState();
		table.WriteSnapshotState( snapshot );
		table.WriteSnapshotState( snapshot );
		Assert.AreEqual( 2, values.WriteCalls );
	}

	private sealed class CountingDeltaTable : Sandbox.NetworkTable<int>
	{
		public int WriteCalls;
		public Action AfterWrite;

		protected override void WriteSnapshot( int slot, LocalSnapshotState snapshot )
		{
			WriteCalls++;
			base.WriteSnapshot( slot, snapshot );
			AfterWrite?.Invoke();
		}
	}

	private sealed class CustomDelta : INetworkDeltaSnapshot
	{
		public int WriteCalls;
		public void WriteSnapshotState( int slot, LocalSnapshotState snapshot ) { WriteCalls++; }
		public void ReadSnapshot( int slot, DeltaSnapshot snapshot ) { }
	}
}
