namespace SceneTests.Core;

/// <summary>
/// Component that moves its object to a target position from inside a fixed update,
/// so tests can exercise the fixed-update transform path (where interpolation would
/// normally kick in).
/// </summary>
public class FixedStepMover : Component, Sandbox.Internal.IFixedUpdateSubscriber
{
	public Vector3 Target;
	public int Moves;

	protected override void OnFixedUpdate()
	{
		if ( Moves > 0 )
			return;

		Moves++;
		WorldPosition = Target;
	}
}

/// <summary>
/// Pins GameTransform behavior not covered by the transform math tests: the
/// OnTransformChanged callback and its propagation, the Absolute flag, the
/// default-transform coercion, interpolation state and the proxy escape hatch.
/// </summary>
[TestClass]
[DoNotParallelize]
public class TransformCoverageTest : SceneTest
{
	/// <summary>
	/// OnTransformChanged fires when the transform actually changes and is skipped
	/// when the transform is set to its current value.
	/// </summary>
	[TestMethod]
	public void TransformChangedFiresOnRealChanges()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();

		var changes = 0;
		go.Transform.OnTransformChanged += () => changes++;

		go.WorldPosition = new Vector3( 10, 0, 0 );
		Assert.AreEqual( 1, changes );

		// Setting the exact same position must not re-fire the callback
		go.WorldPosition = new Vector3( 10, 0, 0 );
		Assert.AreEqual( 1, changes );

		go.WorldRotation = Rotation.FromYaw( 90 );
		Assert.AreEqual( 2, changes );

		scene.Destroy();
	}

	/// <summary>
	/// Moving a parent notifies the children's OnTransformChanged too - their world
	/// transform changed even though their local transform didn't.
	/// </summary>
	[TestMethod]
	public void TransformChangedPropagatesToChildren()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var parent = scene.CreateObject();
		var child = new GameObject( parent );
		child.LocalPosition = new Vector3( 1, 0, 0 );

		var childChanges = 0;
		child.Transform.OnTransformChanged += () => childChanges++;

		parent.WorldPosition = new Vector3( 0, 0, 50 );

		Assert.AreEqual( 1, childChanges );
		Assert.IsTrue( child.WorldPosition.AlmostEqual( new Vector3( 1, 0, 50 ) ) );

		scene.Destroy();
	}

	/// <summary>
	/// An object flagged Absolute ignores its parent's transform completely: its world
	/// transform is its local transform, and parent moves neither move it nor fire its
	/// transform-changed callback.
	/// </summary>
	[TestMethod]
	public void AbsoluteFlagIgnoresParent()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var parent = scene.CreateObject();
		parent.WorldPosition = new Vector3( 100, 0, 0 );

		var child = new GameObject( parent );
		child.Flags |= GameObjectFlags.Absolute;
		child.WorldPosition = new Vector3( 5, 0, 0 );

		Assert.IsTrue( child.WorldPosition.AlmostEqual( new Vector3( 5, 0, 0 ) ) );
		Assert.IsTrue( child.LocalPosition.AlmostEqual( new Vector3( 5, 0, 0 ) ) );

		var childChanges = 0;
		child.Transform.OnTransformChanged += () => childChanges++;

		parent.WorldPosition = new Vector3( 200, 0, 0 );

		Assert.AreEqual( 0, childChanges );
		Assert.IsTrue( child.WorldPosition.AlmostEqual( new Vector3( 5, 0, 0 ) ) );

		scene.Destroy();
	}

	/// <summary>
	/// A world transform cached under an Absolute object is invalidated by ancestor moves once the
	/// object stops being Absolute, even though moves while it was Absolute went past it.
	/// </summary>
	[TestMethod]
	public void CacheUnderFormerlyAbsoluteObjectFollowsAncestorMoves()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var root = scene.CreateObject();
		var middle = new GameObject( root );
		var absolute = new GameObject( middle );
		absolute.Flags |= GameObjectFlags.Absolute;
		var leaf = new GameObject( absolute );
		leaf.LocalPosition = new Vector3( 0, 1, 0 );

		Assert.IsTrue( leaf.WorldPosition.AlmostEqual( new Vector3( 0, 1, 0 ) ) );

		root.WorldPosition = new Vector3( 10, 0, 0 );
		absolute.Flags &= ~GameObjectFlags.Absolute;
		root.WorldPosition = new Vector3( 20, 0, 0 );

		Assert.IsTrue( leaf.WorldPosition.AlmostEqual( new Vector3( 20, 1, 0 ) ) );

		scene.Destroy();
	}

	/// <summary>
	/// A listener that throws stops that change from reaching the children, but the next change still
	/// invalidates their cached world transforms.
	/// </summary>
	[TestMethod]
	public void ChangeAfterThrowingListenerStillReachesChildren()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var root = scene.CreateObject();
		var leaf = new GameObject( root );
		leaf.LocalPosition = new Vector3( 0, 1, 0 );
		_ = leaf.WorldPosition;

		System.Action listener = null;
		listener = () =>
		{
			root.Transform.OnTransformChanged -= listener;
			throw new System.InvalidOperationException( "listener failed" );
		};
		root.Transform.OnTransformChanged += listener;

		Assert.ThrowsException<System.InvalidOperationException>( () => root.WorldPosition = new Vector3( 10, 0, 0 ) );

		root.WorldPosition = new Vector3( 20, 0, 0 );
		Assert.IsTrue( leaf.WorldPosition.AlmostEqual( new Vector3( 20, 1, 0 ) ) );

		scene.Destroy();
	}

	/// <summary>
	/// A listener added deep in a hierarchy after changes have already gone through it - when nothing
	/// below the root was listening - is still told about the next one, and stops being told once removed.
	/// </summary>
	[TestMethod]
	public void ListenerAddedDeepInHierarchyIsNotified()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var root = scene.CreateObject();
		var middle = new GameObject( root );
		var leaf = new GameObject( middle );

		root.WorldPosition = new Vector3( 10, 0, 0 );

		var leafChanges = 0;
		System.Action listener = () => leafChanges++;
		leaf.Transform.OnTransformChanged += listener;

		root.WorldPosition = new Vector3( 20, 0, 0 );
		Assert.AreEqual( 1, leafChanges );

		leaf.Transform.OnTransformChanged -= listener;
		root.WorldPosition = new Vector3( 30, 0, 0 );
		Assert.AreEqual( 1, leafChanges );

		scene.Destroy();
	}

	/// <summary>
	/// A descendant's cached world transform is invalidated by every ancestor move, including moves
	/// after it was read, invalidated and read again - and moves while nothing had it cached.
	/// </summary>
	[TestMethod]
	public void CachedDescendantWorldFollowsEveryAncestorMove()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var root = scene.CreateObject();
		var middle = new GameObject( root );
		var leaf = new GameObject( middle );
		leaf.LocalPosition = new Vector3( 0, 1, 0 );

		Assert.IsTrue( leaf.WorldPosition.AlmostEqual( new Vector3( 0, 1, 0 ) ) );

		root.WorldPosition = new Vector3( 10, 0, 0 );
		Assert.IsTrue( leaf.WorldPosition.AlmostEqual( new Vector3( 10, 1, 0 ) ) );

		root.WorldPosition = new Vector3( 20, 0, 0 );
		Assert.IsTrue( leaf.WorldPosition.AlmostEqual( new Vector3( 20, 1, 0 ) ) );

		// Two moves with no read in between
		root.WorldPosition = new Vector3( 30, 0, 0 );
		root.WorldPosition = new Vector3( 40, 0, 0 );
		Assert.IsTrue( leaf.WorldPosition.AlmostEqual( new Vector3( 40, 1, 0 ) ) );

		// A move of the middle, then of the root
		middle.LocalPosition = new Vector3( 0, 0, 5 );
		Assert.IsTrue( leaf.WorldPosition.AlmostEqual( new Vector3( 40, 1, 5 ) ) );
		root.WorldPosition = new Vector3( 50, 0, 0 );
		Assert.IsTrue( leaf.WorldPosition.AlmostEqual( new Vector3( 50, 1, 5 ) ) );

		scene.Destroy();
	}

	/// <summary>
	/// A branch moved to a new parent follows the new parent, not the old one: its listeners are told
	/// about the new parent's moves only, and its cached world transforms are invalidated by them.
	/// </summary>
	[TestMethod]
	public void ReparentedBranchFollowsNewParent()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var oldParent = scene.CreateObject();
		var newParent = scene.CreateObject();
		var branch = new GameObject( oldParent );
		var listening = new GameObject( branch );
		var cached = new GameObject( branch );
		cached.LocalPosition = new Vector3( 0, 1, 0 );

		var changes = 0;
		listening.Transform.OnTransformChanged += () => changes++;
		_ = cached.WorldPosition;

		branch.SetParent( newParent, false );
		changes = 0;

		oldParent.WorldPosition = new Vector3( 10, 0, 0 );
		Assert.AreEqual( 0, changes );

		newParent.WorldPosition = new Vector3( 20, 0, 0 );
		Assert.AreEqual( 1, changes );
		Assert.IsTrue( cached.WorldPosition.AlmostEqual( new Vector3( 20, 1, 0 ) ) );

		// Read again, then move the new parent once more
		newParent.WorldPosition = new Vector3( 30, 0, 0 );
		Assert.IsTrue( cached.WorldPosition.AlmostEqual( new Vector3( 30, 1, 0 ) ) );

		scene.Destroy();
	}

	/// <summary>
	/// Destroying a listening child stops it counting towards its old parent, while the parent's other
	/// listening children keep being told about moves.
	/// </summary>
	[TestMethod]
	public void DestroyedListenerDoesNotHideSiblingListeners()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var root = scene.CreateObject();
		var doomed = new GameObject( root );
		var survivor = new GameObject( new GameObject( root ) );

		doomed.Transform.OnTransformChanged += () => { };

		var changes = 0;
		survivor.Transform.OnTransformChanged += () => changes++;

		doomed.DestroyImmediate();
		scene.ProcessDeletes();

		root.WorldPosition = new Vector3( 10, 0, 0 );
		Assert.AreEqual( 1, changes );

		scene.Destroy();
	}

	/// <summary>
	/// The animation path: local transforms set without notifying, then a single change from the
	/// root. Cached world transforms below are invalidated by it.
	/// </summary>
	[TestMethod]
	public void FastLocalSetThenRootChangeInvalidatesCachedChildren()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var root = scene.CreateObject();
		var bone = new GameObject( root );
		var childBone = new GameObject( bone );
		childBone.LocalPosition = new Vector3( 0, 1, 0 );

		Assert.IsTrue( childBone.WorldPosition.AlmostEqual( new Vector3( 0, 1, 0 ) ) );

		bone.Transform.SetLocalTransformFast( new Transform( new Vector3( 5, 0, 0 ) ) );
		root.Transform.TransformChanged();

		Assert.IsTrue( childBone.WorldPosition.AlmostEqual( new Vector3( 5, 1, 0 ) ) );

		scene.Destroy();
	}

	/// <summary>
	/// Assigning a default Transform (all zero, including scale) is coerced to
	/// Transform.Zero, which has identity rotation and scale one - objects can't end
	/// up invisible with a zero scale by accident.
	/// </summary>
	[TestMethod]
	public void DefaultTransformIsCoercedToZero()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		go.LocalPosition = new Vector3( 1, 2, 3 );

		go.LocalTransform = default;

		Assert.AreEqual( Transform.Zero, go.LocalTransform );
		Assert.IsTrue( go.LocalScale.AlmostEqual( new Vector3( 1, 1, 1 ) ) );

		scene.Destroy();
	}

	/// <summary>
	/// Outside of a fixed update a transform set is applied directly - the
	/// interpolated local transform immediately matches the target.
	/// </summary>
	[TestMethod]
	public void DirectSetUpdatesInterpolatedLocal()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		go.LocalPosition = new Vector3( 7, 8, 9 );

		Assert.IsTrue( go.Transform.InterpolatedLocal.Position.AlmostEqual( new Vector3( 7, 8, 9 ) ) );
		Assert.IsTrue( go.Transform.Local.Position.AlmostEqual( new Vector3( 7, 8, 9 ) ) );

		scene.Destroy();
	}

	/// <summary>
	/// A move made during a fixed update reaches its exact target once interpolation
	/// is cleared - ClearInterpolation snaps the transform to its final destination
	/// regardless of any interpolation that was in flight.
	/// </summary>
	[TestMethod]
	public void ClearInterpolationSnapsToTarget()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var mover = go.Components.Create<FixedStepMover>();
		mover.Target = new Vector3( 100, 50, 25 );

		scene.GameTick();

		Assert.AreEqual( 1, mover.Moves );

		go.Transform.ClearInterpolation();

		Assert.IsTrue( go.WorldPosition.AlmostEqual( mover.Target ), $"{go.WorldPosition}" );
		Assert.IsTrue( go.Transform.InterpolatedLocal.Position.AlmostEqual( mover.Target ) );

		scene.Destroy();
	}

	/// <summary>
	/// DisableProxy with no proxy installed returns nothing to dispose - there's no
	/// proxy to restore.
	/// </summary>
	[TestMethod]
	public void DisableProxyWithoutProxyReturnsNull()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();

		Assert.IsNull( go.Transform.Proxy );
		Assert.IsNull( go.Transform.DisableProxy() );

		scene.Destroy();
	}
}
