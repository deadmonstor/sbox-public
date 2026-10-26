using System;
using System.Linq;
using Sandbox.Internal;
using Sandbox.Network;

namespace SceneTests.GameObjects;

using static GlobalGameNamespace;

/// <summary>
/// Initial snapshots requested in the same tick are captured together, after every message in it.
/// </summary>
[TestClass]
public class JoinSnapshotsTest
{
	private TypeLibrary _oldTypeLibrary;

	[TestInitialize]
	public void TestInitialize()
	{
		_oldTypeLibrary = Game.TypeLibrary;
		Game.TypeLibrary = SharedTypeLibrary.EngineAndTests;
		JsonUpgrader.UpdateUpgraders( Game.TypeLibrary );
	}

	[TestCleanup]
	public void TestCleanup()
	{
		Game.TypeLibrary = _oldTypeLibrary;
	}

	static TestConnection Joiner()
	{
		var joiner = new TestConnection( Guid.NewGuid() );
		Networking.System.OnConnected( joiner );
		joiner.HandshakeId = Guid.NewGuid();
		joiner.State = Connection.ChannelState.MountVPKs;
		return joiner;
	}

	static void RequestSnapshot( TestConnection joiner )
		=> HostMigrationTest.Receive( joiner, new RequestInitialSnapshot { HandshakeId = joiner.HandshakeId } );

	static ObjectCreateMsg SnapshotOf( TestConnection joiner, GameObject go )
	{
		HostMigrationTest.DrainSnapshot( joiner );
		var response = joiner.Messages.Select( m => m.Payload ).OfType<InitialSnapshotResponse>().Single();
		Assert.AreEqual( joiner.HandshakeId, response.HandshakeId );
		return response.Snapshot.NetworkObjects.OfType<ObjectCreateMsg>().Single( m => m.Guid == go.Id );
	}

	/// <summary>
	/// Joining runs game code (spawning, RPCs) between two joiners' requests in the same tick. Neither may get
	/// a snapshot from before that change while the other gets one from after it.
	/// </summary>
	[TestMethod]
	public void JoinersInOneTickGetTheSceneAfterEveryMessage()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();

		var go = new GameObject( "Before" );
		var counter = go.Components.Create<CounterComponent>();
		counter.Value = 1;
		go.NetworkSpawn( null );

		var first = Joiner();
		var second = Joiner();

		RequestSnapshot( first );
		go.Name = "After";
		counter.Value = 5;
		RequestSnapshot( second );

		Networking.System.SendPendingSnapshots();

		var expected = go._net.GetCreateMessage();
		foreach ( var joiner in new[] { first, second } )
		{
			var created = SnapshotOf( joiner, go );
			Assert.AreEqual( expected.JsonData, created.JsonData );
			CollectionAssert.AreEqual( expected.TableData, created.TableData );
		}
	}

	/// <summary>
	/// A capture that throws for one joiner only loses that joiner's snapshot. The others in the batch
	/// still get theirs, and the failed request isn't retried on the next tick.
	/// </summary>
	[TestMethod]
	public void FailedCaptureDoesNotBlockOtherJoiners()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();

		var broken = Joiner();
		var healthy = Joiner();

		var go = new GameObject();
		var visibility = go.Components.Create<ThrowingVisibility>();
		visibility.Broken = broken.Id;
		go.NetworkSpawn( null );
		go.Network.AlwaysTransmit = false;

		RequestSnapshot( broken );
		RequestSnapshot( healthy );

		Networking.System.SendPendingSnapshots();
		HostMigrationTest.DrainSnapshot( healthy );
		Assert.AreEqual( 1, healthy.Messages.Count( m => m.Payload is InitialSnapshotResponse ) );
		Assert.IsFalse( broken.Messages.Any( m => m.Payload is InitialSnapshotResponse ) );

		// Not left queued to throw again every tick
		var attempts = visibility.Throws;
		Networking.System.SendPendingSnapshots();
		Assert.AreEqual( attempts, visibility.Throws );
	}

	[TestMethod]
	public void RequestFromAnEndedHandshakeIsDropped()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();

		var joiner = Joiner();
		RequestSnapshot( joiner );

		// The host restarted their handshake before the snapshot went out
		joiner.HandshakeId = Guid.NewGuid();
		Networking.System.SendPendingSnapshots();

		HostMigrationTest.DrainSnapshot( joiner );
		Assert.IsFalse( joiner.Messages.Any( m => m.Payload is InitialSnapshotResponse ) );
	}

	private class CounterComponent : Component
	{
		[Sync] public int Value { get; set; }
	}

	private class ThrowingVisibility : Component, Component.INetworkVisible
	{
		public Guid Broken;
		public int Throws;

		public bool IsVisibleToConnection( Connection connection, in BBox bounds )
		{
			if ( connection.Id != Broken )
				return true;

			Throws++;
			throw new InvalidOperationException( "Synthetic capture failure" );
		}
	}
}
