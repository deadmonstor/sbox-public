using System;
using Sandbox.Hashing;
using Sandbox.Network;

namespace NetworkTests;

[TestClass]
[DoNotParallelize]
public class LocalSnapshotStateTest
{
	[TestMethod]
	[DataRow( 0 )]
	[DataRow( 1 )]
	[DataRow( 4 )]
	[DataRow( 12 )]
	[DataRow( 16 )]
	[DataRow( 17 )]
	[DataRow( 128 )]
	[DataRow( 129 )]
	[DataRow( 240 )]
	[DataRow( 241 )]
	[DataRow( 1024 )]
	public void UnsaltedHashMatchesIncrementalHash( int length )
	{
		var value = new byte[length];
		new Random( length ).NextBytes( value );
		var hasher = new XxHash3();
		hasher.Append( value );
		var state = new LocalSnapshotState { ParentId = Guid.NewGuid(), Flags = (NetworkFlags)1 };
		state.AddSerialized( 42, value );
		Assert.AreEqual( hasher.GetCurrentHashAsUInt64(), state.Lookup[42].Hash );
		Assert.AreEqual( length, state.Size );
	}

	[TestMethod]
	[DataRow( 1 )]
	[DataRow( 2 )]
	[DataRow( 3 )]
	public void SaltedHashIncludesRequestedMetadata( int flags )
	{
		var parent = Guid.NewGuid();
		var state = new LocalSnapshotState { ParentId = parent, Flags = (NetworkFlags)1 };
		byte[] value = [1, 2, 3];
		var hasher = new XxHash3();
		hasher.Append( value );
		if ( (flags & 1) != 0 ) hasher.Append( parent.ToByteArray() );
		if ( (flags & 2) != 0 ) hasher.Append( [(byte)state.Flags] );
		state.AddSerialized( 42, value, (LocalSnapshotState.HashFlags)flags );
		Assert.AreEqual( hasher.GetCurrentHashAsUInt64(), state.Lookup[42].Hash );
	}

	[TestMethod]
	public void UnsaltedMutationInvalidatesReceiptsAndUpdatesSize()
	{
		var connection = Guid.NewGuid();
		var state = new LocalSnapshotState();
		state.AddSerialized( 42, [1, 2] );
		var entry = state.Lookup[42];
		entry.Connections.Add( connection );
		state.UpdatedConnections.Add( connection );
		state.AddSerialized( 42, [1, 2] );
		Assert.IsTrue( entry.Connections.Contains( connection ) );
		Assert.IsTrue( state.UpdatedConnections.Contains( connection ) );
		state.AddSerialized( 42, [3, 4, 5] );
		Assert.AreSame( entry, state.Lookup[42] );
		Assert.AreEqual( 3, state.Size );
		Assert.AreEqual( 0, entry.Connections.Count );
		Assert.AreEqual( 0, state.UpdatedConnections.Count );
		CollectionAssert.AreEqual( new byte[] { 3, 4, 5 }, entry.Value );
	}
}
