namespace SceneTests.Components;

[TestClass]
public class RigidbodyIdleTest
{
	static Rigidbody CreateBody( Scene scene, bool motionEnabled = true )
	{
		var go = scene.CreateObject();
		go.Components.Create<BoxCollider>().Scale = new Vector3( 16 );
		var rb = go.Components.Create<Rigidbody>( false );
		rb.Gravity = false;
		rb.GravityScale = 0.5f;
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
		Assert.IsFalse( rb.PhysicsBody.GravityEnabled, "Gravity configured before enabling must apply to both dynamic and keyframed bodies." );
		Assert.AreEqual( 0.5f, rb.PhysicsBody.GravityScale );
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
}
