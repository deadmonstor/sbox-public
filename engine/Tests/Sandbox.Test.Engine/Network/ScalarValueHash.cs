using System;
using System.Collections.Generic;
using Sandbox.Network;

namespace NetworkTests;

[TestClass]
public class ScalarValueHashTest
{
	[TestMethod]
	public void TypedHashesMatchObjectHashes()
	{
		CheckHashes( 0, 1, -1, int.MaxValue, 0 );
		CheckHashes( false, true, false );
		CheckHashes( Vector3.Zero, Vector3.One, new Vector3( 2, 3, 4 ), Vector3.Zero );
		CheckHashes( 0.0f, -0.0f, float.NaN, float.PositiveInfinity, 1.0f );
		CheckHashes<int?>( null, 1, null );
		CheckHashes( "before", "after", null );
	}

	private static void CheckHashes<T>( params T[] values )
	{
		using var typed = new NetworkTable();
		using var boxed = new NetworkTable();
		var typedEntry = new NetworkTable.Entry { TargetType = typeof( T ), GetValue = () => values[0] };
		var boxedEntry = new NetworkTable.Entry { TargetType = typeof( T ), GetValue = () => values[0] };
		typed.Register( 42, typedEntry );
		boxed.Register( 42, boxedEntry );

		foreach ( var value in values )
		{
			var typedRevision = typed.SnapshotRevision;
			var boxedRevision = boxed.SnapshotRevision;
			typed.UpdateValueHash( typedEntry, value );
			boxed.UpdateSlotHash( 42, value );
			Assert.AreEqual( boxedEntry.HashCodeValue, typedEntry.HashCodeValue );
			Assert.AreEqual( boxed.SnapshotRevision - boxedRevision, typed.SnapshotRevision - typedRevision );
		}
	}

	[TestMethod]
	public void ScalarHashUpdatesDoNotAllocate()
	{
		using var table = new NetworkTable();
		var entry = new NetworkTable.Entry { TargetType = typeof( int ), GetValue = () => 0 };
		table.Register( 42, entry );
		for ( var i = 0; i < 1000; i++ )
			table.UpdateValueHash( entry, i );

		var allocated = GC.GetAllocatedBytesForCurrentThread();
		for ( var i = 0; i < 10000; i++ )
			table.UpdateValueHash( entry, i );
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.AreEqual( 0L, allocated );
	}

	[TestMethod]
	public void CollectionValuesKeepContentHashing()
	{
		var values = new List<int> { 1 };
		using var table = new NetworkTable();
		var entry = new NetworkTable.Entry { TargetType = values.GetType(), GetValue = () => values };
		table.Register( 42, entry );
		var revision = table.SnapshotRevision;
		values.Add( 2 );
		table.UpdateValueHash( entry, values );
		Assert.AreNotEqual( revision, table.SnapshotRevision );
		Assert.IsTrue( entry.IsDirty );
	}

	[TestMethod]
	public void StaleEntryUpdatesCurrentSlot()
	{
		using var table = new NetworkTable();
		var oldEntry = new NetworkTable.Entry { TargetType = typeof( int ), GetValue = () => 1 };
		table.Register( 42, oldEntry );
		table.Unregister( 42 );
		table.UpdateValueHash( oldEntry, 2 );
		var replacement = new NetworkTable.Entry { TargetType = typeof( int ), GetValue = () => 3 };
		table.Register( 42, replacement );
		table.UpdateValueHash( oldEntry, 4 );
		Assert.AreEqual( HashCode.Combine( 4 ), replacement.HashCodeValue );
		Assert.AreEqual( HashCode.Combine( 1 ), oldEntry.HashCodeValue );
	}

	[TestMethod]
	public void SerializationReadsScalarGetterOnce()
	{
		var calls = 0;
		using var table = new NetworkTable();
		table.Register( 42, new NetworkTable.Entry { TargetType = typeof( int ), GetValue = () => { calls++; return 7; } } );
		calls = 0;
		table.WriteSnapshotState( new LocalSnapshotState() );
		Assert.AreEqual( 1, calls );
	}
}
