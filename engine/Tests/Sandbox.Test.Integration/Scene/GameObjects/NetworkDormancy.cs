using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Internal;
using Sandbox.Network;

namespace SceneTests.GameObjects;

using static GlobalGameNamespace;

/// <summary>
/// ACK-clean objects sleep automatically; visibility dormancy additionally stops visibility work
/// until the next probe. Exercise the real cluster sender and acknowledge its wire messages.
/// </summary>
[TestClass]
public class NetworkDormancyTest
{
	private const float StartTime = 1000f;
	private TypeLibrary _oldTypeLibrary;
	private float _oldTimeNow;
	private double _oldTimeNowDouble;
	private double? _oldRealTime;

	[TestInitialize]
	public void TestInitialize()
	{
		_oldTypeLibrary = Game.TypeLibrary;
		Game.TypeLibrary = SharedTypeLibrary.EngineAndTests;
		JsonUpgrader.UpdateUpgraders( Game.TypeLibrary );
		_oldTimeNow = Time.Now;
		_oldTimeNowDouble = Time.NowDouble;
		_oldRealTime = RealTime.SimulatedNow;
		SetTime( StartTime );
	}

	[TestCleanup]
	public void TestCleanup()
	{
		Game.TypeLibrary = _oldTypeLibrary;
		Time.Now = _oldTimeNow;
		Time.NowDouble = _oldTimeNowDouble;
		RealTime.SimulatedNow = _oldRealTime;
	}

	[TestMethod]
	public void AcknowledgedVisibleObjectAutomaticallySleeps()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		Settle( scene, peers.Client );

		Assert.IsTrue( go._net.IsFullyUpdated );
		Assert.IsFalse( go._net.IsDirty, "An acknowledged object must leave the send set automatically" );
		Assert.IsFalse( go.Network.IsDeltaDormant, "Visibility dormancy is distinct from ACK-clean sleep" );
		peers.Client.Messages.Clear();
		for ( var i = 0; i < 5; i++ )
			UpdateScene( scene );
		Assert.IsFalse( go._net.IsDirty );
		Assert.AreEqual( 0, ClusterCount( peers.Client ), "Idle objects must not send more snapshot clusters" );
	}

	[TestMethod]
	public void UnacknowledgedFinalStateStaysScheduledAndRetries()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		Settle( scene, peers.Client );

		go.WorldPosition = new Vector3( 0f, 0f, 128f );
		peers.Client.Messages.Clear();
		UpdateScene( scene );
		Assert.IsTrue( ClusterCount( peers.Client ) > 0 );
		Assert.IsFalse( go._net.IsFullyUpdated );
		Assert.IsTrue( go._net.IsDirty );

		// Lose the final update, not just its acknowledgement. Nothing changes afterward to wake it.
		peers.Client.Messages.Clear();
		SetTime( StartTime + 1f );
		UpdateScene( scene );
		Assert.IsTrue( ClusterCount( peers.Client ) > 0, "The unacknowledged final state must be retransmitted" );
		Assert.IsTrue( go._net.IsDirty );
		Acknowledge( peers.Client );
		UpdateScene( scene );
		Assert.IsTrue( go._net.IsFullyUpdated );
		Assert.IsFalse( go._net.IsDirty );
	}

	[TestMethod]
	public void OldAcknowledgementCannotPutNewStateToSleep()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		UpdateScene( scene );
		go.WorldPosition = Vector3.Up * 128f;
		Acknowledge( peers.Client );
		UpdateScene( scene );
		Assert.IsTrue( go._net.IsDirty, "An ACK for the old position must not acknowledge the new position" );
		Assert.IsFalse( go._net.IsFullyUpdated );
		Assert.IsTrue( ClusterCount( peers.Client ) > 0 );
		Settle( scene, peers.Client );
		Assert.IsFalse( go._net.IsDirty );
	}

	[DataTestMethod]
	[DataRow( "Transform" )]
	[DataRow( "Sync" )]
	[DataRow( "Enabled" )]
	[DataRow( "Parent" )]
	[DataRow( "NetList" )]
	[DataRow( "NetDictionary" )]
	public void SleepingObjectWakesOnReplicatedChange( string change )
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var parent = new GameObject();
		parent.NetworkSpawn();
		var go = new GameObject();
		var controller = go.Components.Create<CharacterController>();
		var containers = go.Components.Create<DormancyTestComponent>();
		containers.SyncList = new NetList<int>();
		containers.SyncDictionary = new NetDictionary<int, int>();
		go.NetworkSpawn();
		Settle( scene, peers.Client );
		Assert.IsFalse( go._net.IsDirty );

		switch ( change )
		{
			case "Transform": go.WorldPosition = Vector3.Up * 128f; break;
			case "Sync": controller.Velocity = Vector3.Up * 100f; break;
			case "Enabled": go.Enabled = false; break;
			case "Parent": go.Parent = parent; break;
			case "NetList": containers.SyncList.Add( 1 ); break;
			case "NetDictionary": containers.SyncDictionary[1] = 2; break;
		}

		peers.Client.Messages.Clear();
		UpdateScene( scene );
		if ( change is "NetList" or "NetDictionary" )
			Assert.IsTrue( peers.Client.Messages.Any( x => x.Payload is ObjectNetworkTableMsg ),
				$"{change} must send its reliable container delta" );
		else
			Assert.IsTrue( ClusterCount( peers.Client ) > 0, $"{change} must reach the sender" );
		Settle( scene, peers.Client );
		Assert.IsFalse( go._net.IsDirty );
	}

	[DataTestMethod]
	[DataRow( "Transform" )]
	[DataRow( "Enabled" )]
	[DataRow( "Parent" )]
	public void NestedNetworkObjectWakesItsOwnSnapshot( string change )
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var parent = new GameObject();
		parent.NetworkSpawn();
		var otherParent = new GameObject();
		otherParent.NetworkSpawn();
		var child = new GameObject( parent );
		var component = child.Components.Create<DormancyTestComponent>();
		child.NetworkSpawn();
		Settle( scene, peers.Client );
		Assert.IsFalse( child._net.IsDirty );

		switch ( change )
		{
			case "Transform": child.LocalPosition = Vector3.Up; break;
			case "Enabled": child.Enabled = false; break;
			case "Parent": child.Parent = otherParent; break;
		}

		Assert.IsTrue( child._net.IsDirty, "A nested network object owns its own snapshot" );
		peers.Client.Messages.Clear();
		UpdateScene( scene );
		Assert.IsTrue( ClusterCount( peers.Client ) > 0 );
		Settle( scene, peers.Client );
		Assert.IsFalse( child._net.IsDirty );
	}

	[DataTestMethod]
	[DataRow( "Query" )]
	[DataRow( "List" )]
	[DataRow( "Dictionary" )]
	public void PollingWakesSleepingObjectForRepeatedChanges( string kind )
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		object value = kind switch
		{
			"List" => new List<int>(),
			"Dictionary" => new Dictionary<int, int>(),
			_ => 0
		};
		go._net.dataTable.Register( 100, new NetworkTable.Entry
		{
			TargetType = value.GetType(),
			NeedsQuery = true,
			GetValue = () => value
		} );
		Settle( scene, peers.Client );

		for ( var i = 1; i <= 2; i++ )
		{
			Assert.IsFalse( go._net.IsDirty );
			switch ( value )
			{
				case List<int> list: list.Add( i ); break;
				case Dictionary<int, int> dictionary: dictionary[i] = i; break;
				default: value = i; break;
			}
			peers.Client.Messages.Clear();
			UpdateScene( scene );
			Assert.IsTrue( ClusterCount( peers.Client ) > 0, $"Polling must detect {kind} mutation {i}" );
			Assert.IsTrue( go._net.IsDirty, "A changed polled value must remain scheduled until acknowledged" );
			Settle( scene, peers.Client );
		}
	}

	[TestMethod]
	public void HiddenObjectSleepsAndVisibilityProbeWakesIt()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		var visibility = go.Components.Create<VisibilityController>();
		go.NetworkSpawn();
		go.Network.AlwaysTransmit = false;
		var targets = new Connection[] { peers.Client };
		Send( go, targets );
		for ( var i = 1; i <= 40 && !go.Network.IsDeltaDormant; i++ )
		{
			SetTime( StartTime + i * 0.1f );
			Send( go, targets );
		}
		Assert.IsTrue( go.Network.IsDeltaDormant );

		visibility.Visible = true;
		SetTime( Time.Now + 0.3f );
		Assert.IsTrue( go._net.ProbeVisibility( targets ) );
		Assert.IsFalse( go.Network.IsDeltaDormant );
		Assert.IsTrue( go._net.IsDirty );
		peers.Client.Messages.Clear();
		Send( go, targets );
		Assert.IsTrue( ClusterCount( peers.Client ) > 0 );
	}

	[TestMethod]
	public void NewConnectionAndReconnectionReceiveSleepingObject()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var go = new GameObject();
		go.NetworkSpawn();
		Settle( scene, peers.Client );
		var latecomer = new TestConnection( Guid.NewGuid() );
		var network = SceneNetworkSystem.Instance.NetworkSystem;
		network.OnConnected( latecomer );
		latecomer.State = Connection.ChannelState.Connected;
		var targets = new Connection[] { peers.Client, latecomer };
		Send( go, targets );
		Assert.IsTrue( ClusterCount( latecomer ) > 0, "A new connection must receive an already sleeping object" );
		Assert.IsFalse( go._net.IsFullyUpdated );
		Acknowledge( latecomer );
		Send( go, targets );
		Assert.IsTrue( go._net.IsFullyUpdated );

		go._net.RemoveConnection( latecomer.Id );
		SceneNetworkSystem.Instance.DeltaSnapshots.RemoveConnection( latecomer );
		network.OnDisconnected( latecomer );
		Send( go, new Connection[] { peers.Client } );
		var reconnected = new TestConnection( latecomer.Id );
		network.OnConnected( reconnected );
		typeof( Connection ).GetProperty( nameof( Connection.Id ) ).SetValue( reconnected, latecomer.Id );
		reconnected.State = Connection.ChannelState.Connected;
		Send( go, new Connection[] { peers.Client, reconnected } );
		Assert.IsTrue( ClusterCount( reconnected ) > 0, "A reconnected peer must not inherit its previous acknowledgements" );
		Assert.IsFalse( go._net.IsFullyUpdated );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void ScenePollingToleratesObjectsDestroyedByGetters( bool destroySelf )
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var objects = new[] { new GameObject(), new GameObject() };
		var shouldDestroy = false;
		var destroyed = false;
		foreach ( var go in objects )
		{
			go.NetworkSpawn();
			go._net.dataTable.Register( 100, new NetworkTable.Entry
			{
				TargetType = typeof( int ),
				NeedsQuery = true,
				GetValue = () =>
				{
					if ( shouldDestroy && !destroyed )
					{
						destroyed = true;
						(destroySelf ? go : objects.First( x => x != go )).DestroyImmediate();
					}
					return 0;
				}
			} );
		}
		Settle( scene, peers.Client );
		Assert.IsTrue( objects.All( x => !x._net.IsDirty ) );
		shouldDestroy = true;
		UpdateScene( scene );
		Assert.IsTrue( destroyed, "Clean objects with Query getters must still be polled" );
		Assert.AreEqual( 1, scene.NetworkObjectCount );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void SnapshotSendingToleratesObjectsDestroyedByGetters( bool destroySelf )
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var subject = new GameObject();
		var victim = new GameObject();
		subject.NetworkSpawn();
		victim.NetworkSpawn();
		var snapshots = new IDeltaSnapshot[] { subject._net, victim._net };
		var armed = false;
		var destroyed = false;
		subject._net.dataTable.Register( 100, new NetworkTable.Entry
		{
			TargetType = typeof( int ),
			NeedsQuery = true,
			GetValue = () =>
			{
				if ( !armed ) return 0;
				destroyed = true;
				(destroySelf ? subject : victim).DestroyImmediate();
				return 1;
			}
		} );
		armed = true;
		SceneNetworkSystem.Instance.DeltaSnapshots.Send( snapshots, new Connection[] { peers.Client } );
		Assert.IsTrue( destroyed );
		Assert.AreEqual( 1, scene.NetworkObjectCount );
		Assert.IsFalse( snapshots[destroySelf ? 0 : 1].IsValid );
	}

	[TestMethod]
	public void MutationAfterCaptureCannotBeDroppedByAcknowledgements()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var subject = new GameObject();
		var later = new GameObject();
		subject.NetworkSpawn();
		later.NetworkSpawn();
		var armed = false;
		later._net.dataTable.Register( 100, new NetworkTable.Entry
		{
			TargetType = typeof( int ),
			NeedsQuery = true,
			GetValue = () =>
			{
				if ( armed )
				{
					armed = false;
					subject.WorldPosition = Vector3.Up * 256f;
				}
				return 0;
			}
		} );
		Settle( scene, peers.Client );

		subject.WorldPosition = Vector3.Up * 128f;
		Send( subject, new Connection[] { peers.Client } );
		Acknowledge( peers.Client );

		// The earlier object's captured state is acknowledged, but a later getter changes it.
		armed = true;
		SceneNetworkSystem.Instance.DeltaSnapshots.Send(
			new IDeltaSnapshot[] { subject._net, later._net }, new Connection[] { peers.Client } );
		Assert.IsFalse( subject._net.IsFullyUpdated, "The earlier capture cannot acknowledge the later mutation" );
		Assert.IsTrue( subject._net.IsDirty );

		peers.Client.Messages.Clear();
		UpdateScene( scene );
		Assert.IsTrue( ClusterCount( peers.Client ) > 0, "The later mutation must still be transmitted" );
		Settle( scene, peers.Client );
		Assert.IsFalse( subject._net.IsDirty );
	}

	private static void SetTime( float now )
	{
		Time.Now = now;
		Time.NowDouble = now;
		RealTime.SimulatedNow = now;
	}

	private static void UpdateScene( Scene scene )
	{
		// Bypass only the real-time rate limiter, not the scene scheduler or sender.
		typeof( Scene ).GetField( "_nextNetworkUpdate", BindingFlags.Instance | BindingFlags.NonPublic )
			.SetValue( scene, double.NegativeInfinity );
		scene.SceneNetworkUpdate();
	}

	private static void Send( GameObject go, Connection[] targets )
		=> SceneNetworkSystem.Instance.DeltaSnapshots.Send( new IDeltaSnapshot[] { go._net }, targets );

	private static int ClusterCount( TestConnection connection )
		=> connection.Messages.Count( x => x.Type == InternalMessageType.DeltaSnapshotCluster );

	private static void Settle( Scene scene, TestConnection connection )
	{
		for ( var i = 0; i < 3; i++ )
		{
			UpdateScene( scene );
			Acknowledge( connection );
		}
		UpdateScene( scene );
	}

	private static void Acknowledge( TestConnection connection )
	{
		connection.ProcessMessages( InternalMessageType.DeltaSnapshotCluster, reader =>
		{
			// The receiver accepts every object in this cluster. Use the actual sent cluster id,
			// not the sender's private per-object ACK arrays or snapshot generation counters.
			reader.Read<int>(); // GameNetworkSystem's payload-length envelope.
			using var ack = ByteStream.Create( 6 );
			ack.Write( (ushort)1 );
			ack.Write( reader.Read<ushort>() );
			ack.Write( (ushort)0 );
			using var incoming = ByteStream.CreateReader( ack.ToSpan() );
			SceneNetworkSystem.Instance.DeltaSnapshots.OnDeltaSnapshotClusterAck( connection, incoming );
		} );
		connection.Messages.Clear();
	}

	private class VisibilityController : Component, Component.INetworkVisible
	{
		public bool Visible;
		public bool IsVisibleToConnection( Connection connection, in BBox worldBounds ) => Visible;
	}

	private class DormancyTestComponent : Component
	{
		[Sync] public NetList<int> SyncList { get; set; }
		[Sync] public NetDictionary<int, int> SyncDictionary { get; set; }
	}
}
