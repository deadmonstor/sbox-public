using Sandbox.Internal;

namespace SceneTests.Components;

using static GlobalGameNamespace;

[TestClass]
public class RigidbodyIdleTest
{
	static Rigidbody CreateBody( Scene scene, bool motionEnabled = true )
	{
		var go = scene.CreateObject();
		go.Components.Create<BoxCollider>().Scale = new Vector3( 16 );
		var rb = go.Components.Create<Rigidbody>( false );
		rb.Gravity = false;
		rb.MotionEnabled = motionEnabled;
		rb.StartAsleep = true;
		rb.Enabled = true;
		return rb;
	}

	[TestMethod]
	[DataRow( true )]
	[DataRow( false )]
	public void IdleBodyReflectsDirectEditsAndRecreation( bool motionEnabled )
	{
		var scene = new Scene();
		using var scope = scene.Push();
		var rb = CreateBody( scene, motionEnabled );
		rb.UpdateTransformFromBody();
		rb.UpdateTransformFromBody();
		rb.PhysicsBody.Position = new Vector3( 20, 30, 40 );
		rb.UpdateTransformFromBody();
		Assert.IsTrue( rb.WorldPosition.AlmostEqual( new Vector3( 20, 30, 40 ) ) );
		rb.PhysicsBody.Rotation = Rotation.FromYaw( 45 );
		rb.UpdateTransformFromBody();
		Assert.IsTrue( rb.WorldRotation.AlmostEqual( Rotation.FromYaw( 45 ) ) );
		rb.PhysicsBody.Transform = new Transform( new Vector3( 60, 70, 80 ), Rotation.FromYaw( 90 ) );
		rb.UpdateTransformFromBody();
		Assert.IsTrue( rb.WorldPosition.AlmostEqual( new Vector3( 60, 70, 80 ) ) );
		Assert.IsTrue( rb.WorldRotation.AlmostEqual( Rotation.FromYaw( 90 ) ) );

		rb.Enabled = false;
		rb.GameObject.WorldPosition = new Vector3( 100, 200, 300 );
		rb.Enabled = true;
		rb.PhysicsBody.Position = new Vector3( 200, 300, 400 );
		rb.UpdateTransformFromBody();
		Assert.IsTrue( rb.WorldPosition.AlmostEqual( new Vector3( 200, 300, 400 ) ) );
		rb.GameObject.WorldPosition += new Vector3( 0.00001f, 0, 0 );
		rb.UpdateTransformFromBody();
		Assert.AreEqual( rb.PhysicsBody.Position, rb.WorldPosition, "Transform edits must invalidate the readback cache even if the body setter ignores a tiny change." );
		rb.GameObject.Destroy();
		scene.ProcessDeletes();
	}

	[TestMethod]
	[DataRow( ScenePhysicsMode.Physics3D )]
	[DataRow( ScenePhysicsMode.Physics2D )]
	public void SleepingBodyWakesAndTracksSimulation( ScenePhysicsMode mode )
	{
		var scene = new Scene { PhysicsMode = mode };
		using var scope = scene.Push();
		var rb = CreateBody( scene );
		rb.UpdateTransformFromBody();
		rb.PhysicsBody.Sleeping = false;
		rb.PhysicsBody.Velocity = new Vector3( 100, 0, 0 );
		for ( var i = 0; i < 10; i++ )
		{
			scene.PhysicsWorld.Step( (i + 1) * 0.02, 0.02f, 1 );
			rb.UpdateTransformFromBody();
		}
		Assert.IsTrue( rb.WorldPosition.x > 1, "A previously idle body must follow the native simulation." );
		Assert.IsTrue( rb.WorldPosition.WithZ( 0 ).AlmostEqual( rb.PhysicsBody.Position.WithZ( 0 ) ) );
		rb.GameObject.Destroy();
		scene.ProcessDeletes();
	}

	[TestMethod]
	public void BodyEditDuringReadbackIsAppliedNextTime()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		var rb = CreateBody( scene );
		rb.UpdateTransformFromBody();
		var edited = false;
		rb.Transform.OnTransformChanged += () =>
		{
			if ( edited ) return;
			edited = true;
			rb.PhysicsBody.Position = new Vector3( 200, 0, 0 );
		};
		rb.PhysicsBody.Position = new Vector3( 100, 0, 0 );
		rb.UpdateTransformFromBody();
		Assert.IsTrue( edited );
		rb.UpdateTransformFromBody();
		Assert.IsTrue( rb.WorldPosition.AlmostEqual( new Vector3( 200, 0, 0 ) ) );
		rb.GameObject.Destroy();
		scene.ProcessDeletes();
	}

	[TestMethod]
	[DataRow( ScenePhysicsMode.Physics3D )]
	[DataRow( ScenePhysicsMode.Physics2D )]
	public void IdleRigidbodyProxyTracksTargetsAndCorrectsBodyEdits( ScenePhysicsMode mode )
	{
		var scene = new Scene { PhysicsMode = mode };
		using var scope = scene.Push();
		// Proxy controllers use keyframed bodies; isolate target following from world gravity.
		scene.PhysicsWorld.Gravity = Vector3.Zero;
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeClient();
		var go = new GameObject();
		go.NetworkSpawn( peers.Host );
		go.Components.Create<BoxCollider>().Scale = new Vector3( 16 );
		var rb = go.Components.Create<Rigidbody>( false );
		rb.Gravity = false;
		rb.Enabled = true;
		Assert.IsTrue( rb.IsProxy );
		var now = Time.NowDouble;

		void Step()
		{
			now += 0.02;
			using var time = Time.Scope( now, 0.02 );
			((IScenePhysicsEvents)rb).PrePhysicsStep();
			scene.PhysicsWorld.Step( now, 0.02f, 1 );
		}

		for ( var i = 0; i < 3; i++ ) Step();
		var target = new Transform( new Vector3( 100, 20, 0 ), Rotation.FromYaw( 30 ) );
		go.Transform.FromNetwork( target, false );
		go.Transform.Update( now + Networking.InterpolationTime * 2f, now );
		for ( var i = 0; i < 200; i++ ) Step();
		Assert.IsTrue( rb.PhysicsBody.Position.AlmostEqual( target.Position, 0.1f ), $"The proxy must move to a new network target: actual {rb.PhysicsBody.Position}, target {target.Position}." );
		Assert.IsTrue( rb.PhysicsBody.Rotation.AlmostEqual( target.Rotation, 0.1f ) );

		for ( var i = 0; i < 200; i++ ) Step();
		Assert.IsTrue( rb.PhysicsBody.Position.AlmostEqual( target.Position, 0.1f ), "A stationary proxy must not drift after a target change." );
		rb.PhysicsBody.Position += new Vector3( 50, 0, 0 );
		for ( var i = 0; i < 200; i++ ) Step();
		Assert.IsTrue( rb.PhysicsBody.Position.AlmostEqual( target.Position, 0.1f ), "An external body edit must still be corrected to the network target." );
		go.Destroy();
		scene.ProcessDeletes();
	}
}
