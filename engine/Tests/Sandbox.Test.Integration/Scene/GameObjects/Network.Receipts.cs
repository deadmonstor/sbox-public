using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Internal;
using Sandbox.Network;

namespace SceneTests.GameObjects;

using static GlobalGameNamespace;

public partial class NetworkTest
{
	private static ushort SendReceiptSnapshot( DeltaSnapshotSystem system, NetworkObject obj, Connection[] peers )
	{
		system.Send( [obj], peers );
		return system.GetConnection( peers[0] ).SentClusters.Last().Cluster.Id;
	}

	private static void AckReceiptCluster( DeltaSnapshotSystem system, Connection peer, ushort id )
	{
		using var message = ByteStream.Create( 16 );
		message.Write( id );
		message.Write( (ushort)0 );
		using var reader = ByteStream.CreateReader( message.ToSpan() );
		system.OnDeltaSnapshotClusterAck( peer, reader );
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void ReceiptAckRejectsInvalidOrChangedVersionSource( bool destroyed )
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		var obj = go._net;
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;
		var id = SendReceiptSnapshot( system, obj, [peers.Client] );
		if ( destroyed ) { go.Destroy(); go.Scene.ProcessDeletes(); }
		else obj.LocalSnapshotState.Version++;
		AckReceiptCluster( system, peers.Client, id );
		Assert.AreEqual( 0, obj.LocalSnapshotState.UpdatedConnections.Count );
		Assert.AreEqual( 0, system.GetConnection( peers.Client ).SentClusters.Count );
		system.Reset();
	}

	[TestMethod]
	public void ReceiptClusterIdCollisionDoesNotOverwriteOutstandingSend()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;
		var field = typeof( DeltaSnapshotCluster ).GetField( "s_nextAvailableId", BindingFlags.Static | BindingFlags.NonPublic );
		var id = SendReceiptSnapshot( system, go._net, [peers.Client] );
		var previous = field.GetValue( null );
		try
		{
			// A fresh cluster increments the ID in both its constructor and OnRented.
			// Ensure the forced collision uses an already constructed pooled instance.
			var spare = DeltaSnapshotCluster.Pool.Rent();
			spare.Release();
			field.SetValue( null, unchecked((ushort)(id - 1)) );
			go.WorldPosition = Vector3.One;
			system.Send( [go._net], [peers.Client] );
			Assert.AreEqual( 1, system.GetConnection( peers.Client ).SentClusters.Count );
			AckReceiptCluster( system, peers.Client, id );
			Assert.IsFalse( go._net.LocalSnapshotState.IsUpdated( peers.Client.Id ) );
			var fresh = SendReceiptSnapshot( system, go._net, [peers.Client] );
			AckReceiptCluster( system, peers.Client, fresh );
			Assert.IsTrue( go._net.LocalSnapshotState.IsUpdated( peers.Client.Id ) );
		}
		finally { field.SetValue( null, previous ); system.Reset(); }
	}

	[TestMethod]
	[DataRow( 0, true )]
	[DataRow( 65535, true )]
	[DataRow( 0, false )]
	[DataRow( 65535, false )]
	public void ReceiptReorderedAndDuplicateAcksPreserveNewerCredit( int firstId, bool newerFirst )
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		var obj = go._net;
		var value = 1;
		obj.dataTable.Register( 42, new Sandbox.Network.NetworkTable.Entry { TargetType = typeof( int ), GetValue = () => value } );
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;
		var ids = (Dictionary<Guid, ushort>)typeof( DeltaSnapshotSystem ).GetProperty( "LastSentSnapshotIds", BindingFlags.Instance | BindingFlags.NonPublic ).GetValue( system );
		ids[obj.Id] = unchecked((ushort)(firstId - 1));
		var old = SendReceiptSnapshot( system, obj, [peers.Client] );
		value = 2;
		obj.dataTable.UpdateSlotHash( 42, value );
		var newer = SendReceiptSnapshot( system, obj, [peers.Client] );
		if ( !newerFirst ) AckReceiptCluster( system, peers.Client, old );
		AckReceiptCluster( system, peers.Client, newer );
		var index = system.GetConnection( peers.Client ).Index;
		Assert.IsTrue( obj.LocalSnapshotState.Lookup[42].Connections.Contains( index ) );
		AckReceiptCluster( system, peers.Client, old );
		AckReceiptCluster( system, peers.Client, newer );
		Assert.IsTrue( obj.LocalSnapshotState.Lookup[42].Connections.Contains( index ), "A stale or duplicate ACK cannot revoke newer receipt credit." );
		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( peers.Client.Id ) );
		system.Reset();
	}

	[TestMethod]
	public void ReceiptLateJoinDisconnectAndInFlightIndexReuse()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;
		var late = new TestConnection( Guid.NewGuid() );
		var first = SendReceiptSnapshot( system, go._net, [peers.Client] );
		AckReceiptCluster( system, peers.Client, first );
		go.WorldPosition = Vector3.One;
		system.Send( [go._net], [peers.Client, late] );
		var next = system.GetConnection( late ).SentClusters.Last().Cluster.Id;
		var oldIndex = system.GetConnection( peers.Client ).Index;
		var held = system.GetConnection( late ).SentClusters.Last().Cluster;
		Assert.IsTrue( held.Snapshots.SelectMany( s => s.Entries ).Any( e => e.Connections.Contains( oldIndex ) ) );
		system.RemoveConnection( peers.Client );
		var replacement = new TestConnection( Guid.NewGuid() );
		Assert.AreEqual( oldIndex, system.GetConnection( replacement ).Index );
		foreach ( var snapshot in held.Snapshots )
			foreach ( var entry in snapshot.Entries ) Assert.IsFalse( entry.Connections.Contains( oldIndex ) );
		Assert.IsFalse( go._net.LocalSnapshotState.UpdatedConnections.Contains( oldIndex ) );
		AckReceiptCluster( system, peers.Client, next );
		var sent = SendReceiptSnapshot( system, go._net, [replacement, late] );
		AckReceiptCluster( system, replacement, sent );
		Assert.IsTrue( go._net.LocalSnapshotState.IsUpdated( replacement.Id ) );
		system.Reset();
	}

	[TestMethod]
	public void ReceiptMoreThan64PeersAndOverflowIndexReuse()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;
		var targets = Enumerable.Range( 0, 70 ).Select( _ => new TestConnection( Guid.NewGuid() ) ).ToArray();
		var id = SendReceiptSnapshot( system, go._net, targets );
		foreach ( var target in targets ) AckReceiptCluster( system, target, id );
		foreach ( var target in targets ) Assert.IsTrue( go._net.LocalSnapshotState.IsUpdated( target.Id ) );
		var recycled = system.GetConnection( targets[65] ).Index;
		system.RemoveConnection( targets[65] );
		var replacement = new TestConnection( Guid.NewGuid() );
		Assert.AreEqual( recycled, system.GetConnection( replacement ).Index );
		Assert.IsFalse( go._net.LocalSnapshotState.UpdatedConnections.Contains( recycled ) );
		id = SendReceiptSnapshot( system, go._net, [replacement] );
		AckReceiptCluster( system, replacement, id );
		Assert.IsTrue( go._net.LocalSnapshotState.IsUpdated( replacement.Id ) );
		system.Reset();
	}

	[TestMethod]
	public void ReceiptExpiredPredictionResendsThroughProductionSend()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;
		SendReceiptSnapshot( system, go._net, [peers.Client] );
		var data = system.GetConnection( peers.Client );
		var before = data.SentClusters.Count;
		system.Send( [go._net], [peers.Client] );
		Assert.AreEqual( before, data.SentClusters.Count, "An unexpired prediction suppresses duplicate sending." );
		typeof( DeltaSnapshotSystem ).GetProperty( "Time", BindingFlags.Instance | BindingFlags.NonPublic ).SetValue( system, 0.3f );
		system.Send( [go._net], [peers.Client] );
		Assert.IsTrue( data.SentClusters.Count > before, "Missing ACKs must cause a resend after prediction expiry." );
		system.Reset();
	}

	[TestMethod]
	public void ReceiptUnsentSlotsRegainCreditAfterUncull()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		var obj = go._net;
		obj.dataTable.Register( 42, new Sandbox.Network.NetworkTable.Entry { TargetType = typeof( int ), GetValue = () => 1 } );
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;
		AckReceiptCluster( system, peers.Client, SendReceiptSnapshot( system, obj, [peers.Client] ) );
		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( peers.Client.Id ) );

		// Uncull clears receipts but keeps the peer's received state, so slot 42 is never resent.
		obj.LocalSnapshotState.RemoveConnection( peers.Client.Id );
		go.WorldPosition = Vector3.One;
		AckReceiptCluster( system, peers.Client, SendReceiptSnapshot( system, obj, [peers.Client] ) );

		var index = system.GetConnection( peers.Client ).Index;
		Assert.IsTrue( obj.LocalSnapshotState.Lookup[42].Connections.Contains( index ) );
		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( peers.Client.Id ), "Unchanged slots must regain credit so the object can go idle." );
		system.Reset();
	}
}
