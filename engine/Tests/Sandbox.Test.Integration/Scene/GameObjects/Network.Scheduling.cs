using System;
using System.Collections.Generic;
using Sandbox.Internal;
using Sandbox.Network;

namespace SceneTests.GameObjects;

using static GlobalGameNamespace;

public partial class NetworkTest
{
	static void AcknowledgeScheduledSnapshot( NetworkObject obj, Connection connection )
	{
		var local = obj.LocalSnapshotState;
		var snapshot = new DeltaSnapshot();
		snapshot.CopyFrom( obj, local, 1 );
		var remote = new RemoteSnapshotState();
		foreach ( var entry in local.Entries )
			remote.Data[entry.Slot] = new RemoteSnapshotState.Entry( local.SnapshotId, entry.Value, entry.Hash );
		((IDeltaSnapshot)obj).OnSnapshotAck( connection, snapshot, remote );
		Assert.IsTrue( local.UpdatedConnections.Contains( connection.Id ) );
	}

	static void SettleScheduledSnapshot( Scene scene, NetworkObject obj, Connection connection )
	{
		for ( var i = 0; i < 2; i++ )
		{
			scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [connection] );
			AcknowledgeScheduledSnapshot( obj, connection );
		}
		var id = obj.LocalSnapshotState.SnapshotId;
		scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [connection] );
		Assert.AreEqual( id, obj.LocalSnapshotState.SnapshotId, "Acknowledged idle objects should leave the send pass." );
	}

	[TestMethod]
	[DataRow( "scalar" )]
	[DataRow( "delta" )]
	[DataRow( "transform" )]
	[DataRow( "fast transform" )]
	[DataRow( "enabled" )]
	[DataRow( "flags" )]
	[DataRow( "parent" )]
	[DataRow( "interpolation" )]
	[DataRow( "ownership" )]
	[DataRow( "transmit" )]
	public void IdleSnapshotWakesOnMutation( string mutation )
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		var scene = go.Scene;
		var value = 1;
		go._net.dataTable.Register( 42, new Sandbox.Network.NetworkTable.Entry { TargetType = typeof( int ), GetValue = () => value } );
		var values = new Sandbox.NetworkTable<int>();
		go._net.dataTable.Register( 43, new Sandbox.Network.NetworkTable.Entry { TargetType = values.GetType(), GetValue = () => values } );
		SettleScheduledSnapshot( scene, go._net, peers.Client );
		var id = go._net.LocalSnapshotState.SnapshotId;

		switch ( mutation )
		{
			case "scalar": value++; go._net.dataTable.UpdateSlotHash( 42, value ); break;
			case "delta": values.Set( 1, 7 ); break;
			case "transform": go.LocalPosition = Vector3.One; break;
			case "fast transform": go.Transform.SetLocalTransformFast( new Transform( Vector3.One ) ); break;
			case "enabled": go.Enabled = false; break;
			case "flags": go.Network.Flags |= NetworkFlags.NoPositionSync; break;
			case "parent": go.Parent = new GameObject(); break;
			case "interpolation": go._net.ClearInterpolation(); break;
			case "ownership": go._net.Owner = peers.Client.Id; break;
			case "transmit": go.Network.AlwaysTransmit = false; break;
		}

		scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		if ( mutation == "ownership" )
		{
			// Ownership starts a fresh sequence at zero. The unacknowledged state must keep advancing.
			go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
			Assert.AreEqual( (ushort)1, go._net.LocalSnapshotState.SnapshotId );
		}
		else
			Assert.AreNotEqual( id, go._net.LocalSnapshotState.SnapshotId, mutation );
	}

	[TestMethod]
	public void UnacknowledgedSnapshotsRemainScheduled()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		for ( var i = 0; i < 5; i++ )
		{
			var id = go._net.LocalSnapshotState.SnapshotId;
			go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
			if ( i > 0 ) Assert.AreNotEqual( id, go._net.LocalSnapshotState.SnapshotId );
		}
		SettleScheduledSnapshot( go.Scene, go._net, peers.Client );
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void PollingAndVisibilityObjectsRemainScheduled( bool visibility )
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		var values = new List<int> { 1 };
		if ( visibility )
			go.Network.AlwaysTransmit = false;
		else
			go._net.dataTable.Register( 42, new Sandbox.Network.NetworkTable.Entry { TargetType = values.GetType(), GetValue = () => values } );
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		AcknowledgeScheduledSnapshot( go._net, peers.Client );
		var id = go._net.LocalSnapshotState.SnapshotId;
		values.Add( 2 );
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		Assert.AreNotEqual( id, go._net.LocalSnapshotState.SnapshotId );
		if ( !visibility ) Assert.IsFalse( go._net.LocalSnapshotState.UpdatedConnections.Contains( peers.Client.Id ) );
	}

	[TestMethod]
	public void IdleSnapshotsWakeForConnectionChangesAndHotload()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		SettleScheduledSnapshot( go.Scene, go._net, peers.Client );
		var id = go._net.LocalSnapshotState.SnapshotId;
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [] );
		go._net.RemoveConnection( peers.Client.Id );
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		Assert.AreNotEqual( id, go._net.LocalSnapshotState.SnapshotId );
		SettleScheduledSnapshot( go.Scene, go._net, peers.Client );
		id = go._net.LocalSnapshotState.SnapshotId;
		SceneNetworkSystem.Instance.OnHotload();
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		Assert.AreNotEqual( id, go._net.LocalSnapshotState.SnapshotId );
	}

	[TestMethod]
	public void ChangesDuringPreparationRemainScheduled()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		var mutate = false;
		go._net.dataTable.Register( 42, new Sandbox.Network.NetworkTable.Entry
		{
			TargetType = typeof( int ),
			GetValue = () =>
			{
				if ( mutate )
				{
					mutate = false;
					go.LocalPosition = Vector3.One;
				}
				return 7;
			}
		} );
		SettleScheduledSnapshot( go.Scene, go._net, peers.Client );
		go._net.dataTable.UpdateSlotHash( 42, 8 );
		mutate = true;
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		AcknowledgeScheduledSnapshot( go._net, peers.Client );
		var hash = go._net.LocalSnapshotState.Lookup[1].Hash;
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		Assert.AreNotEqual( hash, go._net.LocalSnapshotState.Lookup[1].Hash );
	}

	[TestMethod]
	public void ReliableQueryPropertiesKeepPolling()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		using var values = new NetDictionary<string, int>();
		var queries = 0;
		go._net.dataTable.Register( 42, new Sandbox.Network.NetworkTable.Entry
		{
			TargetType = values.GetType(),
			GetValue = () => { queries++; return values; }
		} );
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		AcknowledgeScheduledSnapshot( go._net, peers.Client );
		var calls = queries;
		values["changed"] = 7;
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		Assert.IsTrue( queries > calls );
		Assert.IsFalse( go._net.dataTable.HasReliableChanges(), "Polled reliable changes should be sent." );
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void QueuedNotificationsRefreshVisibilityPolling( bool alwaysTransmit )
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		SettleScheduledSnapshot( go.Scene, go._net, peers.Client );
		go._net.ScheduleSnapshot();
		go.Network.AlwaysTransmit = !alwaysTransmit;
		go.Network.AlwaysTransmit = alwaysTransmit;
		for ( var i = 0; i < 3; i++ )
		{
			go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
			AcknowledgeScheduledSnapshot( go._net, peers.Client );
		}
		var id = go._net.LocalSnapshotState.SnapshotId;
		go.Scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		Assert.AreEqual( alwaysTransmit, id == go._net.LocalSnapshotState.SnapshotId );
	}

	[TestMethod]
	public void DestroyedObjectsLeaveSnapshotQueues()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		var scene = go.Scene;
		var obj = go._net;
		go.Destroy();
		scene.ProcessDeletes();
		scene.SendScheduledSnapshots( SceneNetworkSystem.Instance, [peers.Client] );
		Assert.IsFalse( scene.networkedObjects.Contains( obj ) );
	}
}
