using Sandbox.Internal;

namespace SceneTests.Components;

using static GlobalGameNamespace;

[TestClass]
public class RigidbodyGravityTest
{
	[TestMethod]
	[DataRow( true )]
	[DataRow( false )]
	public void GravitySetWhileDisabledAppliesOnCreation( bool motionEnabled )
	{
		var scene = new Scene();
		using var scope = scene.Push();
		var go = scene.CreateObject();
		go.Components.Create<BoxCollider>().Scale = new Vector3( 16 );
		var rb = go.Components.Create<Rigidbody>( false );
		rb.Gravity = false;
		rb.GravityScale = 0.5f;
		rb.MotionEnabled = motionEnabled;
		rb.Enabled = true;

		Assert.IsFalse( rb.PhysicsBody.GravityEnabled, "Gravity configured before enabling must apply to both dynamic and keyframed bodies." );
		Assert.AreEqual( 0.5f, rb.PhysicsBody.GravityScale );
	}

	[TestMethod]
	public void ProxyAppliesGravitySetWhileDisabled()
	{
		var scene = new Scene();
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
	}
}
