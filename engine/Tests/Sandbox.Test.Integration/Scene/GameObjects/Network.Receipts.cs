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
	private const int ReceiptSlot = 42;

	/// <summary>
	/// Spawns an object with its initial interpolation state already written, plus an int sync slot read from <paramref name="getValue"/>.
	/// </summary>
	private static GameObject SpawnReceiptObject( Func<object> getValue )
	{
		var go = new GameObject();
		go.NetworkSpawn();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		((IDeltaSnapshot)go._net).WriteSnapshotState();
		go._net.dataTable.Register( ReceiptSlot, new Sandbox.Network.NetworkTable.Entry { TargetType = typeof( int ), GetValue = getValue } );
		return go;
	}

	/// <summary>
	/// Sends <paramref name="obj"/> and returns the cluster now outstanding for the first peer: the new one,
	/// or the existing one if nothing was resent. <c>SentClusters</c> is swap-removed, so order is not send order.
	/// </summary>
	private static DeltaSnapshotCluster SendReceiptCluster( DeltaSnapshotSystem system, NetworkObject obj, params Connection[] peers )
	{
		var sent = system.GetConnection( peers[0] ).SentClusters;
		var before = sent.Select( s => s.Cluster ).ToHashSet();
		system.Send( [obj], peers );
		return sent.Select( s => s.Cluster ).FirstOrDefault( c => !before.Contains( c ) ) ?? sent.Single().Cluster;
	}

	private static void AckReceiptCluster( DeltaSnapshotSystem system, Connection peer, ushort clusterId )
	{
		using var message = ByteStream.Create( 16 );
		message.Write( clusterId );
		message.Write( (ushort)0 );
		using var reader = ByteStream.CreateReader( message.ToSpan() );
		system.OnDeltaSnapshotClusterAck( peer, reader );
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void ReceiptAckForChangedOrDestroyedSourceGrantsNoCredit( bool destroy )
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();
		var client = clientAndHost.Client;
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;

		var go = SpawnReceiptObject( () => 1 );
		var obj = go._net;
		var id = SendReceiptCluster( system, obj, client ).Id;

		if ( destroy )
		{
			go.Destroy();
			go.Scene.ProcessDeletes();
		}
		else
		{
			obj.LocalSnapshotState.Version++;
		}

		AckReceiptCluster( system, client, id );

		Assert.AreEqual( 0, obj.LocalSnapshotState.UpdatedConnections.Count );
		Assert.AreEqual( 0, system.GetConnection( client ).SentClusters.Count );
	}

	[TestMethod]
	public void ReceiptClusterIdCollisionKeepsOutstandingSend()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();
		var client = clientAndHost.Client;
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;

		var go = SpawnReceiptObject( () => 1 );
		var id = SendReceiptCluster( system, go._net, client ).Id;

		var nextId = typeof( DeltaSnapshotCluster ).GetField( "s_nextAvailableId", BindingFlags.Static | BindingFlags.NonPublic );
		var previousId = nextId.GetValue( null );
		try
		{
			DeltaSnapshotCluster.Pool.Rent().Release();
			nextId.SetValue( null, unchecked((ushort)(id - 1)) );
			go.WorldPosition = Vector3.One;
			system.Send( [go._net], [client] );
			Assert.AreEqual( 1, system.GetConnection( client ).SentClusters.Count );

			AckReceiptCluster( system, client, id );
			Assert.IsFalse( go._net.LocalSnapshotState.IsUpdated( client.Id ) );

			AckReceiptCluster( system, client, SendReceiptCluster( system, go._net, client ).Id );
			Assert.IsTrue( go._net.LocalSnapshotState.IsUpdated( client.Id ) );
		}
		finally
		{
			nextId.SetValue( null, previousId );
		}
	}

	[TestMethod]
	[DataRow( 0, false )]
	[DataRow( 0, true )]
	[DataRow( 65535, false )]
	[DataRow( 65535, true )]
	public void ReceiptStaleAndDuplicateAcksKeepNewerCredit( int firstSnapshotId, bool ackNewerFirst )
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();
		var client = clientAndHost.Client;
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;

		var value = 1;
		var obj = SpawnReceiptObject( () => value )._net;
		var lastSentIds = (Dictionary<Guid, ushort>)typeof( DeltaSnapshotSystem ).GetProperty( "LastSentSnapshotIds", BindingFlags.Instance | BindingFlags.NonPublic ).GetValue( system );
		lastSentIds[obj.Id] = unchecked((ushort)(firstSnapshotId - 1));

		var older = SendReceiptCluster( system, obj, client ).Id;
		value = 2;
		obj.dataTable.UpdateSlotHash( ReceiptSlot, value );
		var newer = SendReceiptCluster( system, obj, client ).Id;

		if ( !ackNewerFirst ) AckReceiptCluster( system, client, older );
		AckReceiptCluster( system, client, newer );
		AckReceiptCluster( system, client, older );
		AckReceiptCluster( system, client, newer );

		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( client.Id ) );
	}

	[TestMethod]
	public void ReceiptLateJoinerReceivesSlotsAlreadyAcknowledgedByOthers()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();
		var client = clientAndHost.Client;
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;

		var obj = SpawnReceiptObject( () => 1 )._net;
		AckReceiptCluster( system, client, SendReceiptCluster( system, obj, client ).Id );

		var late = new TestConnection( Guid.NewGuid() );
		AckReceiptCluster( system, late, SendReceiptCluster( system, obj, late, client ).Id );

		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( late.Id ) );
		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( client.Id ) );
		Assert.AreEqual( 0, system.GetConnection( client ).SentClusters.Count );
	}

	[TestMethod]
	public void ReceiptRecycledIndexStartsWithoutCredit()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();
		var client = clientAndHost.Client;
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;

		var go = SpawnReceiptObject( () => 1 );
		var obj = go._net;
		var late = new TestConnection( Guid.NewGuid() );
		AckReceiptCluster( system, client, SendReceiptCluster( system, obj, client ).Id );
		go.WorldPosition = Vector3.One;
		var held = SendReceiptCluster( system, obj, late, client );
		var oldIndex = system.GetConnection( client ).Index;
		bool HeldForOldIndex() => held.Snapshots.SelectMany( s => s.Entries ).Any( e => e.Connections.Contains( oldIndex ) );
		Assert.IsTrue( HeldForOldIndex() );

		system.RemoveConnection( client );
		var replacement = new TestConnection( Guid.NewGuid() );

		Assert.AreEqual( oldIndex, system.GetConnection( replacement ).Index );
		Assert.IsFalse( HeldForOldIndex() );
		Assert.IsFalse( obj.LocalSnapshotState.Lookup[ReceiptSlot].Connections.Contains( oldIndex ) );

		AckReceiptCluster( system, replacement, SendReceiptCluster( system, obj, replacement ).Id );
		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( replacement.Id ) );
		AckReceiptCluster( system, late, held.Id );
		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( late.Id ) );
	}

	[TestMethod]
	public void ReceiptOverflowIndicesReceiveCreditAndRecycle()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;

		var obj = SpawnReceiptObject( () => 1 )._net;
		var targets = Enumerable.Range( 0, 70 ).Select( _ => new TestConnection( Guid.NewGuid() ) ).ToArray();
		var id = SendReceiptCluster( system, obj, targets ).Id;
		foreach ( var target in targets ) AckReceiptCluster( system, target, id );
		Assert.IsTrue( targets.All( t => obj.LocalSnapshotState.IsUpdated( t.Id ) ) );

		var recycled = system.GetConnection( targets[65] ).Index;
		Assert.IsTrue( recycled >= 64 );
		system.RemoveConnection( targets[65] );
		var replacement = new TestConnection( Guid.NewGuid() );

		Assert.AreEqual( recycled, system.GetConnection( replacement ).Index );
		Assert.IsFalse( obj.LocalSnapshotState.Lookup[ReceiptSlot].Connections.Contains( recycled ) );

		AckReceiptCluster( system, replacement, SendReceiptCluster( system, obj, replacement ).Id );
		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( replacement.Id ) );
	}

	[TestMethod]
	public void ReceiptMissingAckResendsAfterPredictionExpires()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();
		var client = clientAndHost.Client;
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;

		var obj = SpawnReceiptObject( () => 1 )._net;
		var first = SendReceiptCluster( system, obj, client ).Id;
		Assert.AreEqual( first, SendReceiptCluster( system, obj, client ).Id );

		typeof( DeltaSnapshotSystem ).GetProperty( "Time", BindingFlags.Instance | BindingFlags.NonPublic ).SetValue( system, 1f );
		var resent = SendReceiptCluster( system, obj, client ).Id;
		Assert.AreNotEqual( first, resent );

		AckReceiptCluster( system, client, resent );
		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( client.Id ) );
	}

	[TestMethod]
	public void ReceiptUnchangedSlotsRegainCreditAfterUncull()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();
		var client = clientAndHost.Client;
		var system = SceneNetworkSystem.Instance.DeltaSnapshots;

		var go = SpawnReceiptObject( () => 1 );
		var obj = go._net;
		AckReceiptCluster( system, client, SendReceiptCluster( system, obj, client ).Id );

		obj.LocalSnapshotState.RemoveConnection( client.Id );
		go.WorldPosition = Vector3.One;
		AckReceiptCluster( system, client, SendReceiptCluster( system, obj, client ).Id );

		Assert.IsTrue( obj.LocalSnapshotState.IsUpdated( client.Id ) );
	}
}
