using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Internal;
using Sandbox.Network;

namespace SceneTests.GameObjects;

using static GlobalGameNamespace;

/// <summary>
/// Exercise dormancy scheduling with the real cluster sender and acknowledgements of its wire messages.
/// </summary>
[TestClass]
public class NetworkDormancyTest
{
	private const float StartTime = 1000f;
	private TypeLibrary _oldTypeLibrary;
	private float _oldTimeNow;
	private double _oldTimeNowDouble;
	private double? _oldRealTime;
	private Scene _scene;
	private IDisposable _scope;
	private ClientAndHost _peers;

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

		_scene = new Scene();
		_scope = _scene.Push();

		_peers = new ClientAndHost( TypeLibrary );
		_peers.BecomeHost();
	}

	[TestCleanup]
	public void TestCleanup()
	{
		_peers.Dispose();
		_scope.Dispose();

		Game.TypeLibrary = _oldTypeLibrary;
		Time.Now = _oldTimeNow;
		Time.NowDouble = _oldTimeNowDouble;
		RealTime.SimulatedNow = _oldRealTime;
	}

	[DataTestMethod]
	[DataRow( "Transform" )]
	[DataRow( "Nested" )]
	[DataRow( "Enabled" )]
	[DataRow( "Parent" )]
	[DataRow( "NetList" )]
	[DataRow( "NetDictionary" )]
	[DataRow( "Query" )]
	[DataRow( "List" )]
	[DataRow( "Dictionary" )]
	public void ReplicatedChangesWakeImmediatelyAndRestartGrace( string change )
	{
		var parent = new GameObject();
		parent.NetworkSpawn();

		var go = change == "Nested" ? new GameObject( parent ) : new GameObject();
		var containers = go.Components.Create<DormancyTestComponent>();
		containers.SyncList = new NetList<int>();
		containers.SyncDictionary = new NetDictionary<int, int>();
		go.NetworkSpawn();

		object value = change switch
		{
			"List" => new List<int>(),
			"Dictionary" => new Dictionary<int, int>(),
			_ => 0
		};

		if ( change is "Query" or "List" or "Dictionary" )
			RegisterQuery( go, value.GetType(), () => value );

		Settle();
		Assert.IsFalse( go._net.IsDirty );

		var now = RealTime.NowDouble;

		for ( var step = 1; step <= 2; step++ )
		{
			if ( step == 2 )
			{
				RealTime.SimulatedNow = now + 0.75;
				UpdateScene();

				Assert.IsTrue( go._net.IsDirty, "Acknowledgement must not end the awake grace" );
			}

			switch ( change )
			{
				case "Transform" or "Nested": go.LocalPosition = Vector3.Up * 128f * step; break;
				case "Enabled": go.Enabled = step == 2; break;
				case "Parent": go.Parent = step == 1 ? parent : null; break;
				case "NetList": containers.SyncList.Add( step ); break;
				case "NetDictionary": containers.SyncDictionary[step] = step; break;
				case "List": ((List<int>)value).Add( step ); break;
				case "Dictionary": ((Dictionary<int, int>)value)[step] = step; break;
				default: value = step; break;
			}

			_peers.Client.Messages.Clear();
			UpdateScene();

			Assert.IsTrue( change is "NetList" or "NetDictionary"
				? _peers.Client.Messages.Any( x => x.Payload is ObjectNetworkTableMsg )
				: ClusterCount( _peers.Client ) > 0, $"{change} change {step} must send immediately" );
			Assert.IsTrue( go._net.IsDirty, "A change must stay scheduled until acknowledged" );

			if ( change == "Nested" )
				Assert.IsFalse( parent._net.IsDirty, "The child must own its snapshot" );

			Acknowledge( _peers.Client );
			UpdateScene();

			Assert.IsTrue( go._net.IsFullyUpdated );
		}

		RealTime.SimulatedNow = now + 1.25;
		UpdateScene();

		Assert.IsTrue( go._net.IsDirty, "The latest change must restart the grace period" );

		RealTime.SimulatedNow = now + 1.8;
		UpdateScene();

		Assert.IsFalse( go._net.IsDirty, "Must sleep after a second without changes, even with game time frozen" );

		for ( var i = 0; i < 3; i++ )
			UpdateScene();

		Assert.AreEqual( 0, ClusterCount( _peers.Client ), "Idle objects must not resend acknowledged state" );
	}

	[TestMethod]
	public void ObjectStaysScheduledUntilLatestStateIsAcknowledged()
	{
		var go = new GameObject();
		go.NetworkSpawn();
		Settle();

		go.WorldPosition = Vector3.Up * 128f;
		UpdateScene();

		go.WorldPosition = Vector3.Up * 256f;
		Acknowledge( _peers.Client );
		UpdateScene();

		Assert.IsFalse( go._net.IsFullyUpdated, "An ACK for the old position must not acknowledge the new position" );
		Assert.IsTrue( ClusterCount( _peers.Client ) > 0 );

		_peers.Client.Messages.Clear();
		SetTime( Time.Now + 1.1f );
		UpdateScene();

		Assert.IsTrue( ClusterCount( _peers.Client ) > 0, "The lost final update must be retransmitted" );
		Assert.IsTrue( go._net.IsDirty );

		Acknowledge( _peers.Client );
		UpdateScene();

		Assert.IsTrue( go._net.IsFullyUpdated );
		Assert.IsFalse( go._net.IsDirty );
	}

	[TestMethod]
	public void VisibilityProbeWakesHiddenObjectWhileGameTimeIsPaused()
	{
		var go = new GameObject();
		var visibility = go.Components.Create<VisibilityController>();
		go.NetworkSpawn();
		go.Network.AlwaysTransmit = false;

		for ( var i = 0; i < 45; i++ )
		{
			SetTime( StartTime + i * 0.1f );
			UpdateScene();
			Acknowledge( _peers.Client );
		}

		go.WorldPosition = Vector3.Up * 50f;
		for ( var i = 0; i < 15 && !go.Network.IsDeltaDormant; i++ )
		{
			SetTime( Time.Now + 0.1f );
			UpdateScene();
		}

		Assert.IsTrue( go.Network.IsDeltaDormant );

		_peers.Client.Messages.Clear();
		visibility.Visible = true;
		var now = RealTime.NowDouble;

		for ( var i = 1; i <= 4; i++ )
		{
			RealTime.SimulatedNow = now + i * 0.25;
			UpdateScene();
		}

		Assert.IsFalse( go.Network.IsDeltaDormant, "Visibility must wake in real time even while paused" );
		Assert.IsTrue( ClusterCount( _peers.Client ) > 0 );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void ChangesWhileAloneAreNotReplayedAfterJoinBaseline( bool dictionary )
	{
		var go = new GameObject();
		go.NetworkSpawn();

		using IDisposable source = dictionary ? new NetDictionary<int, int>() : new NetList<int>();
		using IDisposable replica = dictionary ? new NetDictionary<int, int>() : new NetList<int>();

		go._net.dataTable.Register( 100, new NetworkTable.Entry
		{
			TargetType = source.GetType(),
			GetValue = () => source
		} );

		using var receiver = new NetworkTable();
		receiver.Register( 100, new NetworkTable.Entry
		{
			TargetType = replica.GetType(),
			GetValue = () => replica
		} );

		void Apply( byte[] data )
		{
			var reader = ByteStream.CreateReader( data );
			receiver.Read( ref reader );
			reader.Dispose();
		}

		Settle();
		_peers.Client.State = Connection.ChannelState.Unconnected;
		UpdateScene();

		if ( source is NetList<int> list )
			list.Add( 10 );
		else
			((NetDictionary<int, int>)source)[1] = 10;

		UpdateScene();

		Assert.IsFalse( ((INetworkSerializer)source).HasChanges, "No-recipient changes must be retired before a peer joins" );

		var baseline = ByteStream.Create( 256 );
		go._net.dataTable.WriteAllReliable( ref baseline );
		Apply( baseline.ToArray() );
		baseline.Dispose();

		_peers.Client.Messages.Clear();
		_peers.Client.State = Connection.ChannelState.Connected;
		UpdateScene();

		foreach ( var message in _peers.Client.Messages.Select( x => x.Payload ).OfType<ObjectNetworkTableMsg>() )
			Apply( message.TableData );

		CollectionAssert.AreEqual( ((IEnumerable)source).Cast<object>().ToArray(), ((IEnumerable)replica).Cast<object>().ToArray() );
	}

	[TestMethod]
	public void NewConnectionAndReconnectionReceiveSleepingObject()
	{
		var go = new GameObject();
		go.NetworkSpawn();
		Settle();

		var latecomer = new TestConnection( Guid.NewGuid() );
		var network = SceneNetworkSystem.Instance.NetworkSystem;
		network.OnConnected( latecomer );
		latecomer.State = Connection.ChannelState.Connected;

		UpdateScene();
		Assert.IsTrue( ClusterCount( latecomer ) > 0, "A new connection must receive an already sleeping object" );
		Assert.IsFalse( go._net.IsFullyUpdated );

		Settle( latecomer );
		Assert.IsFalse( go._net.IsDirty );

		go._net.RemoveConnection( latecomer.Id );
		SceneNetworkSystem.Instance.DeltaSnapshots.RemoveConnection( latecomer );
		network.OnDisconnected( latecomer );
		UpdateScene();

		var reconnected = new TestConnection( latecomer.Id );
		network.OnConnected( reconnected );
		reconnected.State = Connection.ChannelState.Connected;
		UpdateScene();

		Assert.IsTrue( ClusterCount( reconnected ) > 0, "A reconnected peer must not inherit old acknowledgements" );
		Assert.IsFalse( go._net.IsFullyUpdated );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void DestroyedPendingObjectsAreRetired( bool offline )
	{
		var survivor = new GameObject();
		survivor.NetworkSpawn();
		Settle();

		var pending = (List<NetworkObject>)typeof( Scene )
			.GetField( "_dirtyNetworkObjects", BindingFlags.Instance | BindingFlags.NonPublic ).GetValue( _scene );
		var system = SceneNetworkSystem.Instance;

		try
		{
			if ( offline )
				SceneNetworkSystem.Instance = null;

			for ( var i = 0; i < 4; i++ )
			{
				var go = new GameObject();
				go.Components.Create<DormancyTestComponent>();
				go.NetworkSpawn();
				go.DestroyImmediate();
			}

			survivor.WorldPosition = Vector3.Up * 128f;
			_peers.Client.Messages.Clear();
			UpdateScene();

			CollectionAssert.AreEqual( new[] { survivor._net }, pending.ToArray(), "Cleanup must retain only live pending state" );
		}
		finally
		{
			SceneNetworkSystem.Instance = system;
		}

		UpdateScene();

		Assert.AreEqual( 1, _scene.NetworkObjectCount );
		Assert.IsTrue( ClusterCount( _peers.Client ) > 0, "Surviving pending state must replicate" );

		Settle();

		Assert.AreEqual( 0, pending.Count, "Acknowledged survivors and destroyed entries must leave the pending list" );
	}

	[DataTestMethod]
	[DataRow( false, false )]
	[DataRow( false, true )]
	[DataRow( true, false )]
	[DataRow( true, true )]
	public void QueryGetterCanDestroyNetworkObjects( bool sleeping, bool destroySelf )
	{
		var objects = new[] { new GameObject(), new GameObject() };
		var armed = false;
		var destroyed = false;

		foreach ( var go in objects )
		{
			go.NetworkSpawn();
			RegisterQuery( go, typeof( int ), () =>
			{
				if ( armed && !destroyed )
				{
					destroyed = true;
					(destroySelf ? go : objects.First( x => x != go )).DestroyImmediate();
				}

				return 0;
			} );
		}

		Settle();
		Assert.IsTrue( objects.All( x => !x._net.IsDirty ) );

		if ( !sleeping )
		{
			foreach ( var go in objects )
				go.WorldPosition = Vector3.Up;
		}

		armed = true;
		UpdateScene();

		Assert.IsTrue( destroyed );
		Assert.AreEqual( 1, _scene.NetworkObjectCount );
	}

	[TestMethod]
	public void MutationAfterCaptureCannotBeDroppedByAcknowledgements()
	{
		var subject = new GameObject();
		var later = new GameObject();
		subject.NetworkSpawn();
		later.NetworkSpawn();

		var armed = false;
		RegisterQuery( later, typeof( int ), () =>
		{
			if ( armed )
			{
				armed = false;
				subject.WorldPosition = Vector3.Up * 256f;
			}

			return 0;
		} );

		Settle();

		subject.WorldPosition = Vector3.Up * 128f;
		var targets = new Connection[] { _peers.Client };
		SceneNetworkSystem.Instance.DeltaSnapshots.Send( new IDeltaSnapshot[] { subject._net }, targets );
		Acknowledge( _peers.Client );

		armed = true;
		SceneNetworkSystem.Instance.DeltaSnapshots.Send( new IDeltaSnapshot[] { subject._net, later._net }, targets );

		Assert.IsFalse( subject._net.IsFullyUpdated, "An earlier capture cannot acknowledge a later mutation" );

		_peers.Client.Messages.Clear();
		UpdateScene();

		Assert.IsTrue( ClusterCount( _peers.Client ) > 0, "The later mutation must still be transmitted" );

		Settle();

		Assert.IsFalse( subject._net.IsDirty );
	}

	private static void RegisterQuery( GameObject go, Type type, Func<object> getter )
		=> go._net.dataTable.Register( 100, new NetworkTable.Entry
		{
			TargetType = type,
			NeedsQuery = true,
			GetValue = getter
		} );

	private static void SetTime( float now )
	{
		Time.Now = now;
		Time.NowDouble = now;
		RealTime.SimulatedNow = now;
	}

	private void UpdateScene()
	{
		// Bypass only the real-time rate limiter, not the scene scheduler or sender.
		typeof( Scene ).GetField( "_nextNetworkUpdate", BindingFlags.Instance | BindingFlags.NonPublic )
			.SetValue( _scene, double.NegativeInfinity );

		_scene.SceneNetworkUpdate();
	}

	private static int ClusterCount( TestConnection connection )
		=> connection.Messages.Count( x => x.Type == InternalMessageType.DeltaSnapshotCluster );

	private void Settle( params TestConnection[] extra )
	{
		for ( var i = 0; i < 3; i++ )
		{
			UpdateScene();
			Acknowledge( _peers.Client );

			foreach ( var connection in extra )
				Acknowledge( connection );
		}

		SetTime( (float)RealTime.NowDouble + 1.1f );
		UpdateScene();
	}

	private static void Acknowledge( TestConnection connection )
	{
		connection.ProcessMessages( InternalMessageType.DeltaSnapshotCluster, reader =>
		{
			reader.Read<int>();

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
