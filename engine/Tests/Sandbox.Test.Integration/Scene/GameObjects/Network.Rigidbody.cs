using Sandbox.Internal;

namespace SceneTests.GameObjects;

using static GlobalGameNamespace;

public partial class NetworkTest
{
	[TestMethod]
	[DataRow( ScenePhysicsMode.Physics3D )]
	[DataRow( ScenePhysicsMode.Physics2D )]
	public void IdleRigidbodyProxyTracksTargetsAndCorrectsBodyEdits( ScenePhysicsMode mode )
	{
		var scene = new Scene { PhysicsMode = mode };
		using var scope = scene.Push();
		using var peers = new ClientAndHost( TypeLibrary );
		peers.BecomeClient();
		var go = new GameObject();
		go.NetworkSpawn( peers.Host );
		go.Components.Create<BoxCollider>().Scale = new Vector3( 16 );
		var rb = go.Components.Create<Rigidbody>( false );
		rb.Gravity = false;
		rb.GravityScale = 0.5f;
		rb.Enabled = true;
		Assert.IsTrue( rb.IsProxy );
		Assert.IsFalse( rb.PhysicsBody.GravityEnabled, "A proxy body must apply gravity settings configured before enabling." );
		Assert.AreEqual( 0.5f, rb.PhysicsBody.GravityScale );
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
