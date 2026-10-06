using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Sandbox.Internal;
using Sandbox.Mapping;
using Sandbox.Network;
using SceneTests;

namespace SceneTests.GameObjects;

using static GlobalGameNamespace;

[TestClass]
public class NetworkTest
{
	[Expose]
	public sealed class PredictionEventCounter : Component, PlayerController.IEvents
	{
		public int Jumps { get; private set; }
		public void OnJumped() => Jumps++;
	}

	[Expose]
	public sealed class PredictionPushSource : Component, IScenePhysicsEvents
	{
		void IScenePhysicsEvents.PrePhysicsStep()
		{
			var controller = GetComponent<PlayerController>();
			if ( controller.HasPredictionPhysicsAuthority ) controller.Body.PhysicsBody.Velocity = Vector3.Right * 60;
		}
	}
	[TestMethod]
	[DataRow( false, false, false )]
	[DataRow( true, false, false )]
	[DataRow( true, true, false )]
	[DataRow( false, true, false )]
	[DataRow( false, true, true )]
	public void PredictedMovementDoesNotCorrectEveryTick( bool pushing, bool moving, bool landing )
	{
		var hostScene = new Scene();
		var clientScene = new Scene();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.Host.Messages.Clear();
		peers.Client.Messages.Clear();
		var oldMove = Input.AnalogMove;
		Input.AnalogMove = Vector3.Zero;
		try
		{
			GameObject hostPlatform;
			PlayerController hostPlayer;
			using ( hostScene.Push() )
			{
				peers.BecomeHost();
				hostPlatform = new GameObject();
				hostPlatform.WorldPosition = Vector3.Up * 12;
				var collider = hostPlatform.Components.Create<BoxCollider>();
				collider.Scale = new Vector3( 600, 600, 24 );
				hostPlatform.NetworkSpawn();
				var player = new GameObject();
				player.WorldPosition = new Vector3( 40, 20, landing ? 250 : 25 );
				hostPlayer = player.Components.Create<PlayerController>();
				hostPlayer.UseClientPrediction = true;
				hostPlayer.EnableFootstepSounds = false;
				if ( pushing ) player.Components.Create<PredictionPushSource>();
				player.NetworkSpawn( peers.Client );
				using ( Time.PushScope( 0.02, 0.02 ) ) hostScene.InternalFixedUpdate();
			}
			PlayerController clientPlayer;
			GameObject clientPlatform;
			using ( clientScene.Push() )
			{
				peers.BecomeClient();
				peers.ProcessMessages();
				peers.Client.Messages.Clear();
				clientPlayer = clientScene.GetComponentInChildren<PlayerController>();
				clientPlatform = clientScene.Directory.FindByGuid( hostPlatform.Id );
			}
			static DeltaSnapshot Snapshot( GameObject go )
			{
				IDeltaSnapshot source = go._net;
				var result = new DeltaSnapshot();
				result.CopyFrom( source, source.WriteSnapshotState(), 2 );
				return result;
			}
			static Transform Pose( int tick ) => new( new Vector3( tick * 2, tick, 12 ), Rotation.FromYaw( tick * 0.5f ) );
			DeltaSnapshot snapshot;
			using ( hostScene.Push() )
			{
				peers.BecomeHost();
				snapshot = Snapshot( hostPlayer.GameObject );
			}
			var delayedSnapshots = new Queue<DeltaSnapshot>();
			int warmCorrections = 0;
			for ( int tick = 2; tick <= 100; tick++ )
			{
				using ( clientScene.Push() )
				{
					peers.BecomeClient();
					Input.AnalogMove = moving && tick >= 25 && tick < 50 ? Vector3.Forward : Vector3.Zero;
					// Explicitly sample the displayed platform five ticks behind the host.
					clientPlatform.Transform.SetLocalTransformFast( Pose( pushing ? 0 : Math.Max( 0, tick - 5 ) ) );
					if ( tick == 2 || delayedSnapshots.Count > 3 )
						((IDeltaSnapshot)clientPlayer.GameObject._net).OnSnapshot( peers.Host, tick > 2 ? delayedSnapshots.Dequeue() : snapshot );
					using ( Time.PushScope( tick * 0.02, 0.02 ) ) clientScene.InternalFixedUpdate();
					if ( tick == (landing ? 70 : 20) ) warmCorrections = clientPlayer.PredictionCorrections;
				}
				using ( hostScene.Push() )
				{
					peers.BecomeHost();
					hostPlatform.WorldTransform = Pose( pushing ? 0 : tick );
					peers.ProcessMessages();
					peers.Host.Messages.Clear();
					using ( Time.PushScope( tick * 0.02, 0.02 ) ) hostScene.InternalFixedUpdate();
					snapshot = Snapshot( hostPlayer.GameObject );
					delayedSnapshots.Enqueue( snapshot );
				}
			}
			Assert.IsTrue( clientPlayer.IsOnGround );
			Assert.AreEqual( 0, clientPlayer.PredictionCorrections - warmCorrections,
				$"Expected movement generated {clientPlayer.PredictionCorrections - warmCorrections} corrections after warmup (pushing={pushing}, moving={moving}, landing={landing})" );
			var hostLocal = hostPlatform.WorldTransform.PointToLocal( hostPlayer.WorldPosition );
			var clientLocal = clientPlatform.WorldTransform.PointToLocal( clientPlayer.WorldPosition );
			Assert.IsTrue( (hostLocal - clientLocal).Length < (pushing ? 8 : 0.1f), $"Platform-relative drift: host {hostLocal}, client {clientLocal}" );
		}
		finally { Input.AnalogMove = oldMove; }
	}
	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void PredictedPlayerReceivesNativePhysicsPushes( bool remoteOwner )
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeHost();
		var oldMove = Input.AnalogMove;
		Input.AnalogMove = Vector3.Zero;
		try
		{
			var floor = new GameObject();
			floor.WorldPosition = Vector3.Down * 50;
			var floorCollider = floor.Components.Create<BoxCollider>();
			floorCollider.Scale = new Vector3( 2000, 2000, 100 );
			floorCollider.Static = true;
			var player = new GameObject();
			player.WorldPosition = Vector3.Up;
			var controller = player.Components.Create<PlayerController>();
			controller.UseClientPrediction = true;
			controller.EnableFootstepSounds = false;
			player.NetworkSpawn( remoteOwner ? peers.Client : peers.Host );
			var pusher = new GameObject();
			pusher.WorldPosition = new Vector3( -60, 0, 40 );
			var collider = pusher.Components.Create<BoxCollider>();
			collider.Scale = new Vector3( 40, 100, 80 );
			var body = pusher.Components.Create<Rigidbody>();
			body.Gravity = false;
			body.MassOverride = 1000;
			for ( int tick = 1; tick <= 50; tick++ )
			{
				body.Velocity = Vector3.Forward * 120;
				using ( Time.PushScope( tick * 0.02f, 0.02f ) ) Game.ActiveScene.InternalFixedUpdate();
			}
			Assert.IsTrue( controller.Body.PhysicsBody.MotionEnabled, "The host must simulate contacts even for a client-owned player" );
			Assert.IsTrue( player.WorldPosition.x > 10, $"Moving rigidbody did not push predicted player: {player.WorldPosition}" );
		}
		finally { Input.AnalogMove = oldMove; }
	}
	[TestMethod]
	public void HostTransformPredictionConvergesWithDelayedCommandsAndDroppedSnapshots()
	{
		var hostScene = new Scene();
		var clientScene = new Scene();
		using var peers = new ClientAndHost( TypeLibrary );
		// The helper puts peers in Connected state; discard its bootstrap handshake
		// packets instead of replaying a second handshake into these test scenes.
		peers.Client.Messages.Clear();
		peers.Host.Messages.Clear();
		// Input.Pressed is disabled in headless runs; emulate an owning game client.
		var headless = typeof( Application ).GetProperty( nameof( Application.IsHeadless ) );
		var oldHeadless = Application.IsHeadless;
		headless.SetValue( null, false );
		Input.ClearActions();
		var oldMove = Input.AnalogMove;
		var oldSettings = Input.InputSettings;
		var inputSettings = new InputSettings();
		inputSettings.InitDefault();
		Input.InputSettings = inputSettings;
		PlayerController hostPlayer;
		PlayerController clientPlayer;
		PredictionEventCounter hostEvents;
		const float delta = 0.02f;

		static DeltaSnapshot Snapshot( PlayerController player )
		{
			IDeltaSnapshot networkObject = player.GameObject._net;
			var snapshot = new DeltaSnapshot();
			snapshot.CopyFrom( networkObject, networkObject.WriteSnapshotState(), 2 );
			return snapshot;
		}

		try
		{
			using ( hostScene.Push() )
			{
				peers.BecomeHost();
				var floor = hostScene.CreateObject();
				floor.WorldPosition = new Vector3( 0, 0, -50 );
				var box = floor.Components.Create<BoxCollider>();
				box.Scale = new Vector3( 4000, 4000, 100 );
				box.Static = true;
				floor.NetworkSpawn();
				var player = hostScene.CreateObject();
				player.WorldPosition = Vector3.Up;
				hostPlayer = player.Components.Create<PlayerController>();
				hostPlayer.EnableFootstepSounds = false;
				hostPlayer.UseClientPrediction = true;
				hostEvents = player.Components.Create<PredictionEventCounter>();
				player.NetworkSpawn( peers.Client );
				using ( Time.PushScope( delta, delta ) ) hostScene.InternalFixedUpdate();
			}

			using ( clientScene.Push() )
			{
				peers.BecomeClient();
				peers.ProcessMessages();
				peers.Client.Messages.Clear();
				clientPlayer = clientScene.GetComponentInChildren<PlayerController>();
				Assert.IsNotNull( clientPlayer );
			}

			DeltaSnapshot latest;
			using ( hostScene.Push() )
			{
				peers.BecomeHost();
				latest = Snapshot( hostPlayer );
			}

			for ( int tick = 2; tick < 100; tick++ )
			{
				using ( clientScene.Push() )
				{
					peers.BecomeClient();
					// Drop two out of every three state snapshots. The remaining state
					// must be sufficient to reconcile without receiving earlier snapshots.
					if ( tick % 3 == 2 ) ((IDeltaSnapshot)clientPlayer.GameObject._net).OnSnapshot( peers.Host, latest );
					Input.AnalogMove = tick < 40 ? Vector3.Forward : Vector3.Zero;
					// Scene fixed updates push their own accumulated input context.
					var context = (Input.Context)typeof( Scene ).GetProperty( "FixedUpdateInputContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic ).GetValue( clientScene );
					context.AccumActionsPressed = tick == 10 ? 1UL << Input.GetActionIndex( "Jump" ) : 0;
					using ( Time.PushScope( tick * delta, delta ) ) clientScene.InternalFixedUpdate();
				}
				using ( hostScene.Push() )
				{
					peers.BecomeHost();
					// Drop input packets as well as delaying delivery. Redundant batches
					// must recover gaps without skipping a jump or simulating input twice.
					if ( tick % 4 == 0 ) peers.Host.Messages.Clear();
					// Deliver input in bursts to exercise host catch-up and replay.
					if ( tick % 3 == 0 )
					{
						peers.ProcessMessages();
						peers.Host.Messages.Clear();
					}
					if ( tick == 50 ) hostPlayer.WorldPosition += Vector3.Right * 50;
					using ( Time.PushScope( tick * delta, delta ) ) hostScene.InternalFixedUpdate();
					latest = Snapshot( hostPlayer );
				}
			}

			using ( clientScene.Push() )
			{
				peers.BecomeClient();
				Assert.IsTrue( clientPlayer.IsPredictingLocally );
			}
			Assert.IsTrue( hostPlayer.Body.MotionEnabled );
			Assert.IsTrue( clientPlayer.Body.MotionEnabled );
			Assert.IsTrue( hostPlayer.WorldPosition.x > 40, $"Host did not simulate movement: {hostPlayer.WorldPosition}" );
			Assert.IsTrue( hostPlayer.WorldPosition.x < 100, $"Duplicate inputs advanced simulation twice: {hostPlayer.WorldPosition}" );
			Assert.IsTrue( (hostPlayer.WorldPosition - clientPlayer.WorldPosition).Length < 0.1f,
				$"Host {hostPlayer.WorldPosition}, client {clientPlayer.WorldPosition}" );
			Assert.AreEqual( 1, hostEvents.Jumps, "Retransmitted jump input must execute exactly once on the host" );
			using ( hostScene.Push() )
			{
				peers.BecomeHost();
				hostPlayer.UseClientPrediction = false;
				using ( Time.PushScope( 100 * delta, delta ) ) hostScene.InternalFixedUpdate();
				Assert.IsTrue( hostPlayer.Body.MotionEnabled );
				Assert.AreEqual( NetworkFlags.None, hostPlayer.Network.Flags & NetworkFlags.HostTransformAuthority );
				latest = Snapshot( hostPlayer );
			}
			using ( clientScene.Push() )
			{
				peers.BecomeClient();
				((IDeltaSnapshot)clientPlayer.GameObject._net).OnSnapshot( peers.Host, latest );
				using ( Time.PushScope( 100 * delta, delta ) ) clientScene.InternalFixedUpdate();
				Assert.IsFalse( clientPlayer.IsPredictingLocally );
				Assert.IsTrue( clientPlayer.Body.MotionEnabled );
			}
		}
		finally
		{
			headless.SetValue( null, oldHeadless );
			Input.AnalogMove = oldMove;
			Input.ClearActions();
			Input.InputSettings = oldSettings;
		}
	}

	[TestMethod]
	public void HostTransformAuthorityRejectsOwnerTransforms()
	{
		using var scope = new Scene().Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeClient();
		var go = new GameObject();
		go.Network.Interpolation = false;
		go.NetworkSpawn();
		go.WorldPosition = Vector3.Forward * 100;
		IDeltaSnapshot networkObject = go._net;
		var ownerSnapshot = new DeltaSnapshot();
		ownerSnapshot.CopyFrom( networkObject, networkObject.WriteSnapshotState(), 2 );

		peers.BecomeHost();
		go.Network.Flags |= NetworkFlags.HostTransformAuthority;
		go.WorldPosition = Vector3.Zero;
		Assert.IsTrue( go._net.HasTransformControl( peers.Host ) );
		Assert.IsFalse( go._net.HasTransformControl( peers.Client ) );
		Assert.IsTrue( networkObject.OnSnapshot( peers.Client, ownerSnapshot ) );
		Assert.AreEqual( Vector3.Zero, go.WorldPosition );

		go.WorldPosition = Vector3.Up * 50;
		var hostSnapshot = new DeltaSnapshot();
		hostSnapshot.CopyFrom( networkObject, networkObject.WriteSnapshotState(), 2 );
		peers.BecomeClient();
		go.Transform.SetLocalTransformFast( Transform.Zero );
		Assert.IsTrue( networkObject.OnSnapshot( peers.Host, hostSnapshot ) );
		Assert.AreEqual( Vector3.Up * 50, go.WorldPosition );
		// The owner retains ordinary input/property control.
		Assert.IsTrue( go._net.HasControl( peers.Client ) );
	}

	private TypeLibrary _oldTypeLibrary;

	[TestInitialize]
	public void TestInitialize()
	{
		_oldTypeLibrary = Game.TypeLibrary;

		Game.TypeLibrary = new Sandbox.Internal.TypeLibrary();
		Game.TypeLibrary.AddAssembly( typeof( PrefabFile ).Assembly, false );
		Game.TypeLibrary.AddAssembly( typeof( ModelRenderer ).Assembly, false );
		Game.TypeLibrary.AddAssembly( typeof( NetworkTestComponent ).Assembly, false );

		JsonUpgrader.UpdateUpgraders( Game.TypeLibrary );
	}

	[TestCleanup]
	public void TestCleanup()
	{
		Game.TypeLibrary = _oldTypeLibrary;
	}

	[TestMethod]
	public void DisableTransformSync()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		using var clientAndHost = new ClientAndHost( TypeLibrary );

		// Become the client
		clientAndHost.BecomeClient();

		var go = new GameObject();

		// Disable interpolation for this test to help prove the bug, because
		// otherwise when we test the position later, it'll be in an interpolation
		// buffer
		go.Network.Interpolation = false;
		go.Network.Flags |= NetworkFlags.NoPositionSync;
		go.Network.Flags |= NetworkFlags.NoRotationSync;
		go.Network.Flags |= NetworkFlags.NoScaleSync;
		go.NetworkSpawn();

		var transform = go.Transform.Local;
		transform.Rotation = Rotation.From( 180f, 0f, 0f );
		transform.Position = Vector3.One * 16f;
		transform.Scale = Vector3.One * 16f;
		go.Transform.Local = transform;

		// Create a snapshot
		IDeltaSnapshot networkObject = go._net;
		var state = networkObject.WriteSnapshotState();
		var snapshot = new DeltaSnapshot();
		snapshot.CopyFrom( networkObject, state, 2 );

		// Become the host
		clientAndHost.BecomeHost();

		// We need to set the transform back to what the host would have it as
		// before it receives the snapshot
		transform = go.Transform.Local;
		transform.Rotation = Rotation.Identity;
		transform.Position = Vector3.Zero;
		transform.Scale = Vector3.One;
		go.Transform.Local = transform;

		// Now we'll process the snapshot from the client
		using ( var reader = ByteStream.CreateReader( SerializeSnapshot( snapshot ) ) )
		{
			SceneNetworkSystem.Instance.DeltaSnapshots.OnDeltaSnapshot( clientAndHost.Client, reader );
		}

		Assert.AreEqual( Rotation.Identity, go.Transform.Local.Rotation );
		Assert.AreEqual( Vector3.Zero, go.Transform.Local.Position );
		Assert.AreEqual( Vector3.One, go.Transform.Local.Scale );
	}

	[TestMethod]
	[DataRow( NetworkFlags.None )]
	[DataRow( NetworkFlags.NoPositionSync )]
	[DataRow( NetworkFlags.NoRotationSync )]
	[DataRow( NetworkFlags.NoScaleSync )]
	public void ProxyKeepsUnsyncedComponentLocal( NetworkFlags flags )
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		using var clientAndHost = new ClientAndHost( TypeLibrary );

		clientAndHost.BecomeClient();

		var go = new GameObject();
		go.Network.Flags |= flags;

		go.NetworkSpawn( clientAndHost.Host );
		Assert.IsTrue( go.IsProxy );

		var localTransform = new Transform( new Vector3( 100f, 0f, 0f ), Rotation.From( 0f, 45f, 0f ), Vector3.One * 2f );
		var networkTransform = new Transform( new Vector3( -100f, 0f, 0f ), Rotation.From( 0f, 90f, 0f ), Vector3.One * 4f );

		go.Transform.Local = localTransform;

		var time = Time.NowDouble;
		go.Transform.FromNetwork( networkTransform, false );

		go.Transform.Update( time + Networking.InterpolationTime * 2f, time );

		var expected = new Transform(
			(flags & NetworkFlags.NoPositionSync) != 0 ? localTransform.Position : networkTransform.Position,
			(flags & NetworkFlags.NoRotationSync) != 0 ? localTransform.Rotation : networkTransform.Rotation,
			(flags & NetworkFlags.NoScaleSync) != 0 ? localTransform.Scale : networkTransform.Scale );

		var local = go.Transform.InterpolatedLocal;

		Assert.IsTrue( local.Position.AlmostEqual( expected.Position ), $"Position was {local.Position}" );
		Assert.IsTrue( local.Rotation.Distance( expected.Rotation ) < 0.01f, $"Rotation was {local.Rotation}" );
		Assert.IsTrue( local.Scale.AlmostEqual( expected.Scale ), $"Scale was {local.Scale}" );
	}

	[TestMethod]
	public void NetworkedInput()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		using var clientAndHost = new ClientAndHost( TypeLibrary );

		// Become the client
		clientAndHost.BecomeClient();

		var inputSettings = new InputSettings();
		inputSettings.InitDefault();

		Input.InputSettings = inputSettings;
		Input.SetAction( "Jump", true );

		// Send a client tick - this will build a user command as well
		Game.ActiveScene.SendClientTick( SceneNetworkSystem.Instance );

		// Become the host
		clientAndHost.BecomeHost();

		clientAndHost.Host.ProcessMessages( InternalMessageType.ClientTick, bs =>
		{
			Networking.System.OnReceiveClientTick( bs, clientAndHost.Client );
		} );

		// Clear both sides so this tick can't be replayed by a later ProcessMessages call
		clientAndHost.Client.Messages.Clear();
		clientAndHost.Host.Messages.Clear();

		Assert.AreEqual( true, clientAndHost.Client.Pressed( "Jump" ) );
		Assert.AreEqual( true, clientAndHost.Client.Down( "Jump" ) );

		// Become the client
		clientAndHost.BecomeClient();

		Input.ClearActions();

		// Send a client tick - this will build a user command as well
		Game.ActiveScene.SendClientTick( SceneNetworkSystem.Instance );

		// Become the host
		clientAndHost.BecomeHost();

		clientAndHost.Host.ProcessMessages( InternalMessageType.ClientTick, bs =>
		{
			Networking.System.OnReceiveClientTick( bs, clientAndHost.Client );
		} );

		// Clear both sides so this tick can't be replayed by a later ProcessMessages call
		clientAndHost.Client.Messages.Clear();
		clientAndHost.Host.Messages.Clear();

		Assert.AreEqual( true, clientAndHost.Client.Released( "Jump" ) );
		Assert.AreEqual( false, clientAndHost.Client.Down( "Jump" ) );

		// Let's test wrap-aware command number processing
		var userCommand = new UserCommand( uint.MaxValue );

		clientAndHost.Client.Input.ApplyUserCommand( userCommand );

		// Become the client
		clientAndHost.BecomeClient();

		Input.SetAction( "Jump", true );

		Assert.AreEqual( true, Connection.Local.Pressed( "Jump" ) );
		Assert.AreEqual( true, Connection.Local.Down( "Jump" ) );

		// Send a client tick - this will build a user command as well
		Game.ActiveScene.SendClientTick( SceneNetworkSystem.Instance );

		// Become the host
		clientAndHost.BecomeHost();

		clientAndHost.Host.ProcessMessages( InternalMessageType.ClientTick, bs =>
		{
			Networking.System.OnReceiveClientTick( bs, clientAndHost.Client );
		} );

		Assert.AreEqual( false, clientAndHost.Client.Pressed( "Forward" ) );
		Assert.AreEqual( true, clientAndHost.Client.Pressed( "Jump" ) );
		Assert.AreEqual( true, clientAndHost.Client.Down( "Jump" ) );

		Input.ClearActions();
		Input.SetAction( "Forward", true );

		Assert.AreEqual( true, Connection.Local.Pressed( "Forward" ) );
		Assert.AreEqual( true, Connection.Local.Down( "Forward" ) );

		// Don't leak pressed input state into later tests
		Input.ClearActions();
	}

	[TestMethod]
	public void MalformedClientTickIsDroppedAndDoesNotThrow()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );

		// Only the host receives client ticks.
		clientAndHost.BecomeHost();

		// Malformed ClientTick: the count claims 4 origins (48 bytes) but only 1 (12 bytes) follows.
		// The receiver must drop it, not read past the buffer and throw.
		var write = ByteStream.Create( 64 );
		write.Write( (char)4 );
		write.Write( 1.0f ); write.Write( 2.0f ); write.Write( 3.0f ); // only one origin of data
		using var reader = ByteStream.CreateReader( write.ToArray() );
		write.Dispose();

		// Must not throw.
		Networking.System.OnReceiveClientTick( reader, clientAndHost.Client );

		// Dropped: visibility origins left untouched, not allocated from the bogus count.
		Assert.AreEqual( 0, clientAndHost.Client.VisibilityOrigins.Length,
			"Malformed ClientTick should have been dropped without touching VisibilityOrigins" );
	}

	[TestMethod]
	public void RegisterSyncProps()
	{
		Assert.IsNotNull( Game.TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		var testComponentType = Game.TypeLibrary.GetType<NetworkTestComponent>();
		Assert.IsNotNull( testComponentType );

		var testSyncPropertyType = testComponentType.GetProperty( "SyncInt" );
		Assert.IsNotNull( testSyncPropertyType );

		var testPropertyId = testSyncPropertyType.Identity;

		var go = new GameObject();
		var comp1 = go.Components.Create<NetworkTestComponent>();
		comp1.SyncInt = 1;

		var prop1Id = NetworkObject.GetPropertySlot( testPropertyId, comp1.Id );

		var go2 = new GameObject();
		go2.Parent = go;
		var comp2 = go2.Components.Create<NetworkTestComponent>();
		comp2.SyncInt = 2;

		var prop2Id = NetworkObject.GetPropertySlot( testPropertyId, comp2.Id );

		var go3 = new GameObject();
		go3.Parent = go2;
		var comp3 = go3.Components.Create<NetworkTestComponent>();
		comp3.SyncInt = 3;

		var prop3Id = NetworkObject.GetPropertySlot( testPropertyId, comp3.Id );

		go.NetworkSpawn();

		Assert.IsTrue( go._net.dataTable.IsRegistered( prop1Id ) );
		Assert.IsTrue( go._net.dataTable.IsRegistered( prop2Id ) );
		Assert.IsTrue( go._net.dataTable.IsRegistered( prop3Id ) );

		Assert.AreEqual( 1, comp1.SyncInt );
		Assert.AreEqual( 2, comp2.SyncInt );
		Assert.AreEqual( 3, comp3.SyncInt );
	}

	[TestMethod]
	public void NetworkRefreshWithParentChangeHasCorrectPosition()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		var client = new NetworkSystem( "client", TypeLibrary );
		Networking.System = client;

		var sceneSystem = new SceneNetworkSystem( TypeLibrary, client );
		client.GameSystem = sceneSystem;

		var client1 = new MockConnection( Guid.NewGuid() );
		var client2 = new MockConnection( Guid.NewGuid() );

		Connection.Local = client1;

		var go1 = new GameObject();
		var go2 = new GameObject( go1 )
		{
			WorldPosition = new Vector3( 100f, 100f, 100f )
		};

		go1.NetworkSpawn( Connection.Local );

		var go3 = new GameObject();
		go3.NetworkSpawn( Connection.Local );

		go3.Parent = go2;

		var refreshMsg = go3._net.GetRefreshMessage();

		Connection.Local = client2;

		// Reset the transform to default as it would be when client first constructs it
		go3.SetParentFromNetwork( null );

		// Now simulate the refresh message from the owner
		go3._net.OnRefreshMessage( client1, refreshMsg );

		Assert.AreEqual( go2, go3.Parent );
		Assert.AreEqual( go2.WorldPosition, go3.WorldPosition );
		Assert.AreEqual( Vector3.Zero, go3.LocalPosition );
	}

	[TestMethod]
	public void RemoteObjectParentToSceneKeepsTransform()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		var client = new NetworkSystem( "client", TypeLibrary );
		Networking.System = client;

		var sceneSystem = new SceneNetworkSystem( TypeLibrary, client );
		client.GameSystem = sceneSystem;

		var client1 = new MockConnection( Guid.NewGuid() );
		var client2 = new MockConnection( Guid.NewGuid() );

		Connection.Local = client1;

		var go1 = new GameObject();
		var go2 = new GameObject( go1 )
		{
			WorldPosition = new Vector3( 100f, 100f, 100f )
		};

		go1.NetworkSpawn( Connection.Local );

		var go3 = new GameObject( go2 );
		go3.NetworkSpawn( Connection.Local );

		Connection.Local = client2;

		// Receive a parent message from the network
		go3.SetParentFromNetwork( null, true );

		Assert.AreEqual( go2.WorldPosition, go3.WorldPosition );
	}

	[TestMethod]
	public void RemoteObjectChildSpawnShouldHaveCorrectTransform()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		var client = new NetworkSystem( "client", TypeLibrary );
		Networking.System = client;

		var sceneSystem = new SceneNetworkSystem( TypeLibrary, client );
		client.GameSystem = sceneSystem;

		var client1 = new MockConnection( Guid.NewGuid() );
		var client2 = new MockConnection( Guid.NewGuid() );

		Connection.Local = client1;

		var go1 = new GameObject();
		var go2 = new GameObject( go1 )
		{
			WorldPosition = new Vector3( 100f, 100f, 100f )
		};

		go1.NetworkSpawn( Connection.Local );

		var go3 = new GameObject( go2 );
		go3.NetworkSpawn( Connection.Local );

		var createMsg = go3._net.GetCreateMessage();

		Connection.Local = client2;

		// Reset the transform to default as it would be when client first constructs it
		go3.SetParentFromNetwork( null );

		// Now simulate the creation message from the owner
		go3._net.OnCreateMessage( createMsg );

		Assert.AreEqual( go2.WorldPosition, go3.WorldPosition );
		Assert.AreEqual( Vector3.Zero, go3.LocalPosition );
	}

	[TestMethod]
	public void HostCanParentToAnything()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		var server = new NetworkSystem( "server", TypeLibrary );
		server.InitializeHost();

		Networking.System = server;
		server.GameSystem = new SceneNetworkSystem( TypeLibrary, server );

		var go = new GameObject();
		go.NetworkSpawn( Connection.Local );

		var go2 = new GameObject();
		go2.NetworkSpawn( new MockConnection( Guid.NewGuid() ) );

		var go3 = new GameObject();
		go3.NetworkSpawn( Connection.Local );

		// We should be able to parent to go3 because we're the host.
		go.Parent = go3;
		Assert.AreEqual( go3, go.Parent );

		// We should be able to parent to go2, even though we don't own it, because we're the host.
		go.Parent = go2;
		Assert.AreEqual( go2, go.Parent );
	}

	[TestMethod]
	public void NetworkChildShouldReplicateWhenParentIsDisabled()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		var client = new NetworkSystem( "client", TypeLibrary );
		Networking.System = client;

		var sceneSystem = new SceneNetworkSystem( TypeLibrary, client );
		client.GameSystem = sceneSystem;

		var parentObject = new GameObject
		{
			NetworkMode = NetworkMode.Object
		};

		var childObject = new GameObject( parentObject )
		{
			NetworkMode = NetworkMode.Object
		};

		parentObject.NetworkSpawn( new NetworkSpawnOptions
		{
			StartEnabled = false
		} );

		Assert.IsFalse( parentObject.Enabled );
		Assert.IsTrue( childObject.Enabled );

		Assert.IsNotNull( childObject._net );
	}

	[TestMethod]
	public void SnapshotVersionBlocksOldSnapshots()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		var client = new NetworkSystem( "client", TypeLibrary );
		Networking.System = client;

		var sceneSystem = new SceneNetworkSystem( TypeLibrary, client );
		client.GameSystem = sceneSystem;

		var client1 = new MockConnection( Guid.NewGuid() );
		var client2 = new MockConnection( Guid.NewGuid() );

		// Become client1
		Connection.Local = client1;

		var go = new GameObject();
		go.Network.SetOwnerTransfer( OwnerTransfer.Takeover );

		// Disable interpolation for this test to help prove the bug, because
		// otherwise when we test the position later, it'll be in an interpolation
		// buffer
		go.Network.Interpolation = false;

		go.NetworkSpawn( client2 );

		var go2 = new GameObject();
		IDeltaSnapshot networkObject = go._net;

		// Become client2
		Connection.Local = client2;

		// client2 now owns it, let's have it record a snapshot in this state
		var state = networkObject.WriteSnapshotState();
		var snapshot = new DeltaSnapshot();
		snapshot.CopyFrom( networkObject, state, 2 );

		// Become client1 again
		Connection.Local = client1;

		// Assume control of the network object
		go.Network.TakeOwnership();

		// Change its parent and set position
		go.Parent = go2;
		go.WorldPosition = new Vector3( 0f, 0f, 100f );

		// Drop control of the object, give it back to client2
		go.Network.AssignOwnership( client2 );

		// Now we'll process that old snapshot from client2
		using ( var reader = ByteStream.CreateReader( SerializeSnapshot( snapshot ) ) )
		{
			sceneSystem.DeltaSnapshots.OnDeltaSnapshot( client2, reader );
		}

		// These should be equal, because the old snapshot did NOT apply
		Assert.AreEqual( new Vector3( 0f, 0f, 100f ), go.WorldPosition );
	}

	byte[] SerializeSnapshot( DeltaSnapshot snapshot )
	{
		using var writer = new ByteStream( DeltaSnapshotCluster.MaxSize * 4 );

		writer.Write( snapshot.ObjectId );
		writer.Write( snapshot.Version );
		writer.Write( snapshot.SnapshotId );
		writer.Write( (ushort)snapshot.Entries.Count );

		foreach ( var entry in snapshot.Entries )
		{
			writer.Write( entry.Slot );
			writer.WriteArray( entry.Value );
		}

		return writer.ToArray();
	}

	[TestMethod]
	public void ClientCanOnlyParentToObjectsTheyOwn()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		var server = new NetworkSystem( "client", TypeLibrary );
		Networking.System = server;
		server.GameSystem = new SceneNetworkSystem( TypeLibrary, server );

		var go = new GameObject();
		go.NetworkSpawn( Connection.Local );

		var go2 = new GameObject();
		go2.NetworkSpawn( new MockConnection( Guid.NewGuid() ) );

		var go3 = new GameObject();
		go3.NetworkSpawn( Connection.Local );

		// We should be able to parent to go3 because we own it also.
		go.Parent = go3;
		Assert.AreEqual( go3, go.Parent );

		// We should still be equal to go3, because we don't own go2.
		go.Parent = go2;
		Assert.AreEqual( go3, go.Parent );
	}

	[TestMethod]
	public void ObjectRefreshRegister()
	{
		Assert.IsNotNull( TypeLibrary.GetType<ModelRenderer>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();

		var testComponentType = TypeLibrary.GetType<NetworkTestComponent>();
		Assert.IsNotNull( testComponentType );

		var testSyncPropertyType = testComponentType.GetProperty( "SyncInt" );
		Assert.IsNotNull( testSyncPropertyType );

		var testPropertyId = testSyncPropertyType.Identity;

		var go = new GameObject();
		var comp1 = go.Components.Create<NetworkTestComponent>();
		comp1.SyncInt = 1;

		var prop1Id = NetworkObject.GetPropertySlot( testPropertyId, comp1.Id );

		go.NetworkSpawn();

		var go2 = new GameObject();
		go2.Parent = go;
		var comp2 = go2.Components.Create<NetworkTestComponent>();
		comp2.SyncInt = 2;

		var prop2Id = NetworkObject.GetPropertySlot( testPropertyId, comp2.Id );

		Assert.IsTrue( go._net.dataTable.IsRegistered( prop1Id ) );
		Assert.IsFalse( go._net.dataTable.IsRegistered( prop2Id ) );

		go.Network.Refresh();

		Assert.IsTrue( go._net.dataTable.IsRegistered( prop2Id ) );

		Assert.AreEqual( 1, comp1.SyncInt );
		Assert.AreEqual( 2, comp2.SyncInt );
	}

	/// <summary>
	/// When destroying a scene with networked objects, those objects must not emit <see cref="ObjectDestroyMsg"/>
	/// inside a <see cref="SceneNetworkSystem.SuppressDestroyMessages"/> scope.
	/// </summary>
	[TestMethod]
	public void TestSuppressDestroyMessages()
	{
		using var testSystem = Helpers.InitializeHostWithTestConnection();
		using var _ = SceneNetworkSystem.SuppressDestroyMessages();

		// Scene contains a networked game object

		var scene = Helpers.LoadSceneFromJson( "example.scene",
			"""
			{
				"__guid": "86b89011-9646-4ee7-ad30-c0e11d258674",
				"Name": "Networked Object",
				"Enabled": true,
				"NetworkMode": 1
			}
			""" );

		scene.Destroy();

		Assert.AreEqual( 0, testSystem.GetMessageCount<ObjectDestroyMsg>() );
	}

	/// <summary>
	/// When loading a scene with networked objects, those objects must not emit <see cref="ObjectCreateMsg"/>
	/// inside a <see cref="SceneNetworkSystem.SuppressSpawnMessages"/> scope.
	/// </summary>
	[TestMethod]
	public void TestSuppressSpawnMessages()
	{
		using var testSystem = Helpers.InitializeHostWithTestConnection();
		using var _ = SceneNetworkSystem.SuppressSpawnMessages();

		// Scene contains a networked game object

		var scene = Helpers.LoadSceneFromJson( "example.scene",
			"""
			{
				"__guid": "86b89011-9646-4ee7-ad30-c0e11d258674",
				"Name": "Networked Object",
				"Enabled": true,
				"NetworkMode": 1
			}
			""" );

		Assert.AreEqual( 0, testSystem.GetMessageCount<ObjectCreateMsg>() );

		scene.Destroy();
	}

	[TestMethod]
	public void FromHostPropertyNotOverwrittenByRefresh()
	{
		var scene = new Scene();
		using var scope = scene.Push();

		using var clientAndHost = new ClientAndHost( TypeLibrary );

		clientAndHost.BecomeHost();

		var go = new GameObject();
		go.Parent = scene;

		var comp = go.Components.Create<FromHostPropertyComponent>();
		go.NetworkSpawn( clientAndHost.Client );

		Assert.AreEqual( 1, comp.FromHostInt );

		comp.FromHostInt = 2;
		Assert.AreEqual( 2, comp.FromHostInt );

		var refreshMsg = go._net.GetRefreshMessage();
		var rootJson = JsonNode.Parse( refreshMsg.JsonData ).AsObject();

		if ( rootJson[GameObject.JsonKeys.Components] is JsonArray components )
		{
			foreach ( var node in components )
			{
				if ( node is not JsonObject componentJson )
					continue;

				if ( componentJson.TryGetPropertyValue( Component.JsonKeys.Id, out var idNode )
					&& idNode.GetValue<Guid>() == comp.Id )
				{
					componentJson[nameof( FromHostPropertyComponent.FromHostInt )] = 3;
					break;
				}
			}
		}

		refreshMsg.JsonData = rootJson.ToJsonString();

		go._net.OnRefreshMessage( clientAndHost.Client, refreshMsg );

		Assert.AreEqual( 2, comp.FromHostInt, "FromHost property should not be overwritten by network refresh on the host" );
	}

	[TestMethod]
	public void RegularPropertyOverwrittenByRefresh()
	{
		var scene = new Scene();
		using var scope = scene.Push();

		using var clientAndHost = new ClientAndHost( TypeLibrary );

		clientAndHost.BecomeHost();

		var go = new GameObject();
		go.Parent = scene;

		var comp = go.Components.Create<RegularPropertyComponent>();
		go.NetworkSpawn( clientAndHost.Client );

		Assert.AreEqual( 1, comp.RegularInt );

		comp.RegularInt = 2;
		Assert.AreEqual( 2, comp.RegularInt );

		var refreshMsg = go._net.GetRefreshMessage();
		var rootJson = JsonNode.Parse( refreshMsg.JsonData ).AsObject();

		if ( rootJson[GameObject.JsonKeys.Components] is JsonArray components )
		{
			foreach ( var node in components )
			{
				if ( node is not JsonObject componentJson )
					continue;

				if ( componentJson.TryGetPropertyValue( Component.JsonKeys.Id, out var idNode )
					&& idNode.GetValue<Guid>() == comp.Id )
				{
					componentJson[nameof( RegularPropertyComponent.RegularInt )] = 3;
					break;
				}
			}
		}

		refreshMsg.JsonData = rootJson.ToJsonString();

		go._net.OnRefreshMessage( clientAndHost.Client, refreshMsg );

		Assert.AreEqual( 3, comp.RegularInt, "Regular property should be overwritten by network refresh on the host" );
	}

	[TestMethod]
	[DataRow( false, DisplayName = "Sent from Server" )]
	[DataRow( true, DisplayName = "Sent from Client" )]
	public void DontDeserializeScriptFromClient( bool sentFromClient )
	{
		using var clientAndHost = new ClientAndHost( TypeLibrary );

		var senderScene = new Scene();
		var receiverScene = new Scene();

		var sender = sentFromClient ? clientAndHost.Client : clientAndHost.Host;
		var receiver = sentFromClient ? clientAndHost.Host : clientAndHost.Client;

		//
		// SENDER
		//

		using ( senderScene.Push() )
		{
			clientAndHost.Become( sender );

			var go = new GameObject( "Button" );
			var btn = go.AddComponent<Button>();

			btn.OnTurnedOn = new Doo
			{
				Body =
				[
					new Doo.InvokeBlock
					{
						InvokeType = Doo.InvokeType.Static,
						Member = "Sandbox.Doo+Methods.LogInfo",
						Arguments =
						[
							new Doo.LiteralExpression { LiteralValue = "Hello, World!" }
						]
					}
				]
			};

			go.NetworkSpawn();
			btn.TurnOn();
		}

		//
		// RECEIVER
		//

		using ( receiverScene.Push() )
		{
			Assert.IsNull( receiverScene.GetComponentInChildren<Button>() );

			clientAndHost.Become( receiver );
			clientAndHost.ProcessMessages();

			var btn = receiverScene.GetComponentInChildren<Button>();

			Assert.IsNotNull( btn );
			Assert.AreEqual( sentFromClient, btn.OnTurnedOn is null );
		}
	}

	[TestMethod]
	public async Task NetworkedMeshSurvivesLateJoinSnapshot()
	{
		Assert.IsNotNull( TypeLibrary.GetType<MeshComponent>(), "TypeLibrary hasn't been given the game assembly" );

		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );

		clientAndHost.BecomeHost();

		var block = new GameObject();
		var meshComponent = block.Components.Create<MeshComponent>();

		var mesh = new PolygonMesh();
		var a = mesh.AddVertex( new Vector3( 0, 0, 0 ) );
		var b = mesh.AddVertex( new Vector3( 64, 0, 0 ) );
		var c = mesh.AddVertex( new Vector3( 64, 64, 0 ) );
		var d = mesh.AddVertex( new Vector3( 0, 64, 0 ) );
		mesh.AddFace( a, b, c, d );
		meshComponent.Mesh = mesh;

		var expectedFaces = mesh.FaceHandles.Count();
		Assert.AreNotEqual( 0, expectedFaces, "Test mesh should have geometry to begin with" );

		block.NetworkSpawn();

		// Build the snapshot a late-joining client would receive.
		var snapshot = new SnapshotMsg { GameObjectSystems = [], NetworkObjects = new List<object>() };
		SceneNetworkSystem.Instance.GetSnapshot( default, ref snapshot );

		// The mesh geometry rides in the object's own blob buffer - the exact data the client dropped.
		var createMsg = snapshot.NetworkObjects.OfType<ObjectCreateMsg>().Single( x => x.Guid == block.Id );
		Assert.IsNotNull( createMsg.BlobData, "Mesh blob data should be in the create message" );

		// Client applies the snapshot into a fresh scene, like a late-joiner.
		clientAndHost.BecomeClient();
		await SceneNetworkSystem.Instance.SetSnapshotAsync( snapshot );

		var clientMesh = Game.ActiveScene.GetAllComponents<MeshComponent>().FirstOrDefault();
		Assert.IsNotNull( clientMesh, "MeshComponent missing on client" );
		Assert.IsNotNull( clientMesh.Mesh, "Mesh data missing on client" );
		Assert.AreEqual( expectedFaces, clientMesh.Mesh.FaceHandles.Count(), "Mesh geometry did not survive the snapshot" );
	}

	[DataRow( false, false, false, DisplayName = "Cullable + hidden -> excluded" )]
	[DataRow( false, true, true, DisplayName = "Cullable + visible -> included" )]
	[DataRow( true, false, true, DisplayName = "AlwaysTransmit -> included when hidden" )]
	public void JoinSnapshotFiltersByVisibility( bool alwaysTransmit, bool visible, bool included )
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnNetworked( visible );
		go.Network.AlwaysTransmit = alwaysTransmit;

		var collection = new List<object>();
		Game.ActiveScene.SerializeNetworkObjects( clientAndHost.Client, collection );

		Assert.AreEqual( included, collection.OfType<ObjectCreateMsg>().Any( m => m.Guid == go.Id ) );
	}

	[TestMethod]
	public void JoinSnapshotIncludesNetworkedAncestorsOfIncludedObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		// A cullable parent the joining client can't see, with a child it can. The parent must still
		// ride along in the snapshot, otherwise the child resolves a null parent and gets reparented
		// to the scene root on the client.
		var parent = SpawnNetworked( visible: false );
		var child = SpawnNetworked( visible: true );
		child.SetParent( parent );

		var collection = new List<object>();
		Game.ActiveScene.SerializeNetworkObjects( clientAndHost.Client, collection );

		var guids = collection.OfType<ObjectCreateMsg>().Select( m => m.Guid ).ToList();

		Assert.IsTrue( guids.Contains( child.Id ), "Visible child should be included" );
		Assert.IsTrue( guids.Contains( parent.Id ), "Hidden parent must be pulled in so the child can resolve it" );
		// The parent's create message carries the child's real parent id.
		var childMsg = collection.OfType<ObjectCreateMsg>().Single( m => m.Guid == child.Id );
		Assert.AreEqual( parent.Id, childMsg.Parent, "Child create message should reference the real parent" );
	}

	[TestMethod]
	public void JoinSnapshotDoesNotDuplicateSharedAncestors()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		// One hidden parent, two visible children - the parent must appear exactly once.
		var parent = SpawnNetworked( visible: false );
		var childA = SpawnNetworked( visible: true );
		var childB = SpawnNetworked( visible: true );
		childA.SetParent( parent );
		childB.SetParent( parent );

		var collection = new List<object>();
		Game.ActiveScene.SerializeNetworkObjects( clientAndHost.Client, collection );

		var parentCount = collection.OfType<ObjectCreateMsg>().Count( m => m.Guid == parent.Id );
		Assert.AreEqual( 1, parentCount, "Shared ancestor should be emitted exactly once" );
	}

	[TestMethod]
	public void EnsureCreateMessageSentIsIdempotent()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		// Suppress the spawn broadcast so the client isn't already marked as having the create.
		NetworkObject net;
		using ( SceneNetworkSystem.SuppressSpawnMessages() )
			net = SpawnNetworked( visible: false )._net;

		net.EnsureCreateMessageSent( clientAndHost.Client );
		net.EnsureCreateMessageSent( clientAndHost.Client );

		Assert.AreEqual( 1, CreateMsgCount( clientAndHost.Client ) );
	}

	[TestMethod]
	public void EnsureCreateMessageSentSkipsDestroyedObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		NetworkObject net;
		GameObject go;
		using ( SceneNetworkSystem.SuppressSpawnMessages() )
		{
			go = SpawnNetworked( visible: false );
			net = go._net;
		}

		go.DestroyImmediate();
		net.EnsureCreateMessageSent( clientAndHost.Client );

		Assert.AreEqual( 0, CreateMsgCount( clientAndHost.Client ) );
	}

	[TestMethod]
	public void EnsureCreateMessageSentOnlyEmitsFromHost()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeClient();

		NetworkObject net;
		using ( SceneNetworkSystem.SuppressSpawnMessages() )
			net = SpawnNetworked( visible: false )._net;

		net.EnsureCreateMessageSent( clientAndHost.Host );

		Assert.AreEqual( 0, CreateMsgCount( clientAndHost.Host ),
			"A non-host machine must not emit an ObjectCreateMsg via EnsureCreateMessageSent" );
	}

	[TestMethod]
	public void SerializeNetworkObjectsMarksCreateMessageSent()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		GameObject go;
		using ( SceneNetworkSystem.SuppressSpawnMessages() )
			go = SpawnNetworked( visible: true );

		var collection = new List<object>();
		Game.ActiveScene.SerializeNetworkObjects( clientAndHost.Client, collection );

		Assert.IsTrue( collection.OfType<ObjectCreateMsg>().Any( m => m.Guid == go.Id ),
			"Object should have been serialized into the snapshot" );

		go._net.EnsureCreateMessageSent( clientAndHost.Client );

		Assert.AreEqual( 0, CreateMsgCount( clientAndHost.Client ) );
	}

	// Spawns a cullable (not AlwaysTransmit) networked object with controllable visibility.
	private static GameObject SpawnNetworked( bool visible )
	{
		var go = new GameObject();
		go.Components.Create<VisibilityController>().Visible = visible;
		go.NetworkSpawn();
		go.Network.AlwaysTransmit = false;
		return go;
	}

	private static int CreateMsgCount( TestConnection connection )
		=> connection.Messages.Count( m => m.Payload is ObjectCreateMsg );

	private class VisibilityController : Component, Component.INetworkVisible
	{
		public bool Visible;

		public bool IsVisibleToConnection( Connection connection, in BBox worldBounds ) => Visible;
	}

	private class NetworkTestComponent : Component
	{
		[Sync] public int SyncInt { get; set; }
	}

	private class FromHostPropertyComponent : Component
	{
		[Property, Sync( SyncFlags.FromHost )]
		public int FromHostInt { get; set; } = 1;
	}

	private class RegularPropertyComponent : Component
	{
		[Property, Sync]
		public int RegularInt { get; set; } = 1;
	}
}
