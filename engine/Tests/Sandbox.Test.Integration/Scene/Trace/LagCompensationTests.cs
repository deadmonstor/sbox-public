using System;
using Sandbox.Internal;
using static Sandbox.Internal.GlobalGameNamespace;

namespace SceneTests.Trace;

[TestClass]
public class LagCompensationTests
{
	sealed class Timeline : IPredictionTimeline
	{
		public Scene Scene { get; set; }

		public Guid Epoch { get; set; } = Guid.NewGuid();

		public uint AcknowledgedCommand { get; set; } = 10;

		public uint ExecutingCommand { get; set; }
	}

	sealed class Fixture : IDisposable
	{
		internal readonly Scene Scene = new();
		internal readonly ClientAndHost Peers = new( GlobalGameNamespace.TypeLibrary );
		internal readonly Timeline Timeline;

		internal readonly LagCompensationSystem System;
		readonly IDisposable scope;

		internal Fixture()
		{
			scope = Scene.Push();
			Peers.BecomeHost();
			Timeline = new Timeline { Scene = Scene };
			System = Scene.GetSystem<LagCompensationSystem>();
		}

		internal GameObject Box( Vector3 position, bool enroll = true )
		{
			var gameObject = Scene.CreateObject();
			gameObject.WorldPosition = position;
			gameObject.Components.Create<BoxCollider>().Scale = new Vector3( 10 );

			if ( enroll )
				gameObject.Components.Create<LagCompensation>();

			return gameObject;
		}

		internal void Capture( double time )
		{
			using ( Time.PushScope( time, 0.02 ) )
			{
				Scene.InternalFixedUpdate();
			}

			using ( Time.PushScope( time + 0.007, 0.007 ) )
			{
				Scene.Signal( GameObjectSystem.Stage.UpdateBones );
			}
		}

		internal PredictionCommandTime Command( double time = 0.9, float interpolation = 0 ) => new( Timeline, 10, time, interpolation );

		internal SceneTrace Ray => Scene.Trace.Ray( Vector3.Zero, new Vector3( 300, 0, 0 ) );

		internal LagCompensationResult Trace( SceneTrace trace, PredictionCommandTime? time = null ) => System.TraceAt( time ?? Command(), trace, 1 );

		public void Dispose()
		{
			Scene.Destroy();
			Scene.ProcessDeletes();
			scope.Dispose();
			Peers.Dispose();
		}
	}

	[TestMethod]
	public void MovingTargetUsesInterpolatedHistoricalHullWithoutMovingLiveObjects()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, -40, 0 ) );
		f.Capture( 0.88 );
		target.WorldPosition = new Vector3( 100, 40, 0 );
		f.Capture( 0.92 );

		var pose = target.WorldTransform;
		var collider = target.GetComponent<BoxCollider>();
		var bodyPose = collider.Shapes[0].Body.Transform;
		var messages = f.Peers.Client.Messages.Count + f.Peers.Host.Messages.Count;
		var result = f.Trace( f.Ray.UseHitPosition(), f.Command( 0.95, 0.05f ) );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.IsTrue( result.Trace.Hit );
		Assert.AreEqual( target, result.Trace.GameObject );
		Assert.AreEqual( 95, result.Trace.EndPosition.x, 0.1f );
		Assert.IsTrue( target.WorldTransform.AlmostEqual( pose ) );
		Assert.IsTrue( collider.Shapes[0].Body.Transform.AlmostEqual( bodyPose ) );
		Assert.AreEqual( messages, f.Peers.Client.Messages.Count + f.Peers.Host.Messages.Count );
		Assert.IsFalse( f.Ray.Run().Hit, "The live target is not on the historical ray" );
	}

	[TestMethod]
	public void StaticWorldObstructionWinsOverHistoricalTarget()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, 0, 0 ) );
		var wall = f.Box( new Vector3( 50, 0, 0 ), false );
		wall.GetComponent<BoxCollider>().Static = true;

		f.Capture( 0.88 );
		target.WorldPosition = new Vector3( 100, 40, 0 );
		f.Capture( 0.92 );

		var result = f.Trace( f.Ray );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.AreEqual( wall, result.Trace.GameObject );
	}

	[TestMethod]
	public void EnrolledMovingBlockerObstructsAtHistoricalPoseNotCurrentPose()
	{
		using var f = new Fixture();
		f.Box( new Vector3( 150, 0, 0 ) );
		var blocker = f.Box( new Vector3( 50, 0, 0 ) );
		f.Capture( 0.88 );
		f.Capture( 0.9 );
		blocker.WorldPosition = new Vector3( 50, 60, 0 );
		f.Capture( 0.92 );

		var result = f.Trace( f.Ray );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.AreEqual( blocker, result.Trace.GameObject );
		Assert.AreEqual( 45, result.Trace.EndPosition.x, 0.1f );
	}

	[TestMethod]
	public void CurrentEnrolledBlockerDoesNotOccludeItsClearHistoricalPose()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 150, 0, 0 ) );
		var blocker = f.Box( new Vector3( 50, 60, 0 ) );
		f.Capture( 0.88 );
		f.Capture( 0.9 );
		blocker.WorldPosition = new Vector3( 50, 0, 0 );
		f.Capture( 0.92 );

		var result = f.Trace( f.Ray );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.AreEqual( target, result.Trace.GameObject );
	}

	[TestMethod]
	public void TagsHierarchyAndSingleObjectExclusionsArePreserved()
	{
		using var f = new Fixture();
		var parent = f.Scene.CreateObject();
		parent.Components.Create<LagCompensation>();
		var target = f.Box( new Vector3( 100, 0, 0 ), false );
		target.Parent = parent;
		target.Tags.Add( "lag_target" );

		f.Capture( 0.88 );
		f.Capture( 0.92 );

		var hit = f.Trace( f.Ray.WithTag( "lag_target" ) );

		Assert.IsTrue( hit.Success, hit.Status.ToString() );
		Assert.IsTrue( hit.Trace.HasTag( "lag_target" ) );
		Assert.AreEqual( target, hit.Trace.GameObject );
		Assert.IsFalse( f.Trace( f.Ray.WithTag( "missing_tag" ) ).Trace.Hit );
		Assert.IsFalse( f.Trace( f.Ray.IgnoreGameObject( target ) ).Trace.Hit );
		Assert.IsFalse( f.Trace( f.Ray.IgnoreGameObjectHierarchy( parent ) ).Trace.Hit );
	}

	[TestMethod]
	public void ManualHitboxPreservesHitgroupAndDetachesReusableQueryBody()
	{
		using var f = new Fixture();
		var target = f.Scene.CreateObject();
		target.WorldPosition = new Vector3( 100, 0, 0 );
		target.Tags.Add( "hitbox_target" );
		var hitbox = target.Components.Create<ManualHitbox>();
		hitbox.HitboxTags.Add( "head" );
		hitbox.Rebuild();
		target.Components.Create<LagCompensation>();

		f.Capture( 0.88 );
		f.Capture( 0.9 );
		target.WorldPosition = new Vector3( 100, 60, 0 );
		f.Capture( 0.92 );

		var result = f.Trace( f.Ray.UseHitboxes().WithTag( "hitbox_target" ) );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.AreEqual( target, result.Trace.GameObject );
		Assert.IsNotNull( result.Trace.Hitbox );
		Assert.IsTrue( result.Trace.Hitbox.Tags.Has( "head" ) );
		Assert.IsNull( result.Trace.Hitbox.Body, "Historical metadata must not expose a reused query body" );
		Assert.IsNull( result.Trace.Body );
		Assert.IsNull( result.Trace.Shape );
		Assert.IsFalse( f.Trace( f.Ray.UseHitboxes().IgnoreGameObject( target ) ).Trace.Hit );
	}

	[TestMethod]
	[DataRow( 0 )]
	[DataRow( 1 )]
	[DataRow( 2 )]
	public void TeleportRespawnAndGeometryChangesRejectOldGeneration( int transition )
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, 0, 0 ) );
		f.Capture( 0.88 );
		f.Capture( 0.9 );

		if ( transition == 0 )
			target.WorldPosition = new Vector3( 100, 500, 0 );

		if ( transition == 1 )
			target.GetComponent<LagCompensation>().ResetHistory();

		if ( transition == 2 )
			target.GetComponent<BoxCollider>().Scale = new Vector3( 20 );

		f.Capture( 0.92 );
		Assert.AreEqual( LagCompensationStatus.MissingHistory, f.Trace( f.Ray ).Status );
	}

	[TestMethod]
	public void RemovedHitboxesAndDisabledCollidersLeaveNoPhantomGeometry()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, 0, 0 ) );
		var hitbox = target.Components.Create<ManualHitbox>();
		f.Capture( 0.88 );
		f.Capture( 0.9 );
		hitbox.Enabled = false;
		target.GetComponent<BoxCollider>().Enabled = false;
		f.Capture( 0.92 );

		var result = f.Trace( f.Ray.UseHitboxes() );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.IsFalse( result.Trace.Hit );

		target.Destroy();
		f.Scene.ProcessDeletes();

		Assert.IsFalse( f.Trace( f.Ray.UseHitboxes() ).Trace.Hit );
	}

	[TestMethod]
	public void HostIssuedTokenValidationRejectsOldFutureUnknownAndResetCommands()
	{
		using var f = new Fixture();
		f.Box( new Vector3( 100, 0, 0 ) );
		f.Capture( 0.88 );
		f.Capture( 0.92 );

		Assert.AreEqual( LagCompensationStatus.Expired, f.Trace( f.Ray, f.Command( 0.49 ) ).Status );
		Assert.AreEqual( LagCompensationStatus.Future, f.Trace( f.Ray, f.Command( 1.01 ) ).Status );
		Assert.AreEqual( LagCompensationStatus.InvalidCommand, f.Trace( f.Ray, default( PredictionCommandTime ) ).Status );
		Assert.AreEqual( LagCompensationStatus.InvalidCommand, f.Trace( f.Ray, new PredictionCommandTime( f.Timeline, 11, 0.9, 0 ) ).Status );

		var token = f.Command();
		f.Timeline.Epoch = Guid.NewGuid();

		Assert.AreEqual( LagCompensationStatus.InvalidCommand, f.Trace( f.Ray, token ).Status );

		f.Peers.BecomeClient();
		Assert.AreEqual( LagCompensationStatus.NotHost, f.Trace( f.Ray ).Status );

		f.Peers.BecomeHost();
		Assert.AreEqual( LagCompensationStatus.MissingHistory, f.Trace( f.Ray ).Status );
	}

	[TestMethod]
	public void MissingBracketRejectsRatherThanTracingCurrentPose()
	{
		using var f = new Fixture();
		f.Box( new Vector3( 100, 0, 0 ) );
		f.Capture( 0.88 );

		Assert.AreEqual( LagCompensationStatus.MissingHistory, f.Trace( f.Ray, f.Command( 0.87 ) ).Status );

		f.Capture( 0.92 );
		Assert.AreEqual( LagCompensationStatus.MissingHistory, f.Trace( f.Ray, f.Command( 0.7 ) ).Status );
	}

	[TestMethod]
	[DataRow( 0 )]
	[DataRow( 1 )]
	[DataRow( 2 )]
	public void RayBoxAndCapsuleSweepsPreserveGeometryAndRejectSphereSweeps( int geometry )
	{
		using var f = new Fixture();
		var target = f.Scene.CreateObject();
		target.WorldPosition = new Vector3( 100, 0, 0 );

		if ( geometry == 0 )
			target.Components.Create<SphereCollider>().Radius = 10;

		if ( geometry == 1 )
		{
			var capsule = target.Components.Create<CapsuleCollider>();
			capsule.Radius = 10;
			capsule.Start = Vector3.Down * 10;
			capsule.End = Vector3.Up * 10;
		}

		if ( geometry == 2 )
			target.Components.Create<BoxCollider>().Scale = new Vector3( 20 );

		target.Components.Create<LagCompensation>();
		f.Capture( 0.88 );
		f.Capture( 0.92 );

		double captureTime = 0.94;

		// Compare live native results at the captured pose, then move the live collider off the path.
		CheckSweep( f.Ray, 90 );
		Assert.AreEqual( LagCompensationStatus.InvalidTrace, f.Trace( f.Ray.Radius( 5 ) ).Status,
			"Native sphere/sphere casts cannot supply reliable compensated obstruction." );
		CheckSweep( f.Ray.Size( new Vector3( 10 ) ), 85 );
		CheckSweep( f.Ray.Capsule( new Capsule( Vector3.Down * 5, Vector3.Up * 5, 5 ) ), 85 );

		void CheckSweep( SceneTrace trace, float endX )
		{
			target.WorldPosition = new Vector3( 100, 0, 0 );
			f.Capture( captureTime );
			captureTime += 0.02;
			var native = trace.Run();
			var nativeContact = trace.UseHitPosition().Run();

			target.WorldPosition = new Vector3( 100, 40, 0 );
			f.Capture( captureTime );
			captureTime += 0.02;

			var live = trace.Run();
			Assert.IsFalse( live.Hit, $"The live collider must not substitute for historical geometry: geometry {geometry}, solid {live.StartedSolid}, fraction {live.Fraction}, end {live.EndPosition}" );

			for ( int option = 0; option < 2; option++ )
			{
				var useHitPosition = option != 0;
				var reference = useHitPosition ? nativeContact : native;
				var result = f.Trace( trace.UseHitPosition( useHitPosition ) );
				var hit = result.Trace;
				var detail = $"Geometry {geometry}, contact {useHitPosition}: status {result.Status}, hit {hit.Hit}, solid {hit.StartedSolid}, fraction {hit.Fraction}, start {hit.StartPosition}, end {hit.EndPosition}, contact {hit.HitPosition}; native hit {reference.Hit}, solid {reference.StartedSolid}, fraction {reference.Fraction}, end {reference.EndPosition}, contact {reference.HitPosition}";

				Assert.IsTrue( result.Success, detail );
				Assert.IsTrue( hit.Hit, detail );
				Assert.IsFalse( hit.StartedSolid, detail );
				Assert.AreEqual( target, hit.GameObject, detail );
				Assert.AreEqual( 0, hit.StartPosition.x, 0.25f, detail );
				Assert.AreEqual( endX, hit.EndPosition.x, 0.25f, detail );
				Assert.AreEqual( endX / 300, hit.Fraction, 0.001f, detail );

				if ( useHitPosition )
					Assert.AreEqual( 90, hit.HitPosition.x, 0.25f, detail );

				Assert.AreEqual( reference.Hit, hit.Hit, detail );
				Assert.AreEqual( reference.StartedSolid, hit.StartedSolid, detail );
				Assert.AreEqual( reference.Fraction, hit.Fraction, 0.001f, detail );
				Assert.AreEqual( reference.EndPosition.x, hit.EndPosition.x, 0.25f, detail );

				if ( useHitPosition )
					Assert.AreEqual( reference.HitPosition.x, hit.HitPosition.x, 0.25f, detail );
			}
		}
	}

	[TestMethod]
	public void FilteredFreshParticipantsDoNotRequireHistory()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, 0, 0 ) );
		target.Tags.Add( "target" );

		f.Capture( 0.88 );
		f.Capture( 0.92 );

		var fresh = f.Box( new Vector3( 50, 0, 0 ) );
		Assert.AreEqual( LagCompensationStatus.MissingHistory, f.Trace( f.Ray ).Status );

		var ignored = f.Trace( f.Ray.IgnoreGameObjectHierarchy( fresh ) );
		Assert.IsTrue( ignored.Success, ignored.Status.ToString() );
		Assert.AreEqual( target, ignored.Trace.GameObject );

		var filtered = f.Trace( f.Ray.WithTag( "target" ) );

		Assert.IsTrue( filtered.Success, filtered.Status.ToString() );
		Assert.AreEqual( target, filtered.Trace.GameObject );
	}

	[TestMethod]
	public void SphereGeometryDoesNotUseItsBoundingBox()
	{
		using var f = new Fixture();
		var target = f.Scene.CreateObject();
		target.WorldPosition = new Vector3( 100, 0, 0 );
		target.Components.Create<SphereCollider>().Radius = 10;
		target.Components.Create<LagCompensation>();

		f.Capture( 0.88 );
		f.Capture( 0.92 );

		var result = f.Trace( f.Scene.Trace.Ray( new Vector3( 0, 9, 9 ), new Vector3( 300, 9, 9 ) ) );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.IsFalse( result.Trace.Hit, "This passes through the sphere's bounding box but outside the sphere" );
	}

	[TestMethod]
	public void TriggerAndBodyTypeFiltersMatchNormalNativeTraces()
	{
		using var f = new Fixture();
		var solid = f.Box( new Vector3( 100, 0, 0 ) );
		var trigger = f.Box( new Vector3( 50, 0, 0 ) );
		trigger.GetComponent<BoxCollider>().IsTrigger = true;

		f.Capture( 0.88 );
		f.Capture( 0.92 );

		Assert.AreEqual( solid, f.Trace( f.Ray ).Trace.GameObject );
		Assert.AreEqual( trigger, f.Trace( f.Ray.HitTriggers() ).Trace.GameObject );
		Assert.AreEqual( trigger, f.Trace( f.Ray.HitTriggersOnly() ).Trace.GameObject );
		Assert.IsFalse( f.Trace( f.Ray.IgnoreKeyframed().HitTriggers() ).Trace.Hit );

		solid.GetComponent<BoxCollider>().Static = true;
		f.Capture( 1.02 );

		var staticHit = f.System.TraceAt( f.Command( 1.02 ), f.Ray.IgnoreKeyframed().HitTriggers(), 1.02 );

		Assert.IsTrue( staticHit.Success, staticHit.Status.ToString() );
		Assert.AreEqual( solid, staticHit.Trace.GameObject, "Copy body type must remain static, not keyframed" );
	}

	[TestMethod]
	public void AnimatedModelHitboxesKeepBoneIdentityAndInvalidateModelReplacement()
	{
		using var f = new Fixture();
		var target = f.Scene.CreateObject();
		target.WorldPosition = new Vector3( 100, 0, 0 );
		var renderer = target.Components.Create<SkinnedModelRenderer>();
		renderer.Model = Model.Load( "models/citizen/citizen.vmdl" );
		var modelHitboxes = target.Components.Create<ModelHitboxes>();
		modelHitboxes.Renderer = renderer;
		target.Components.Create<LagCompensation>();

		f.Capture( 0.88 );
		Assert.IsTrue( modelHitboxes.Hitboxes.Count > 0, "Native citizen model must supply hitboxes" );

		var firstBody = modelHitboxes.Hitboxes[0].Body;
		var center = firstBody.Position;

		foreach ( var shape in firstBody.Shapes )
		{
			var local = shape.IsSphereShape ? shape.Sphere.Center
				: shape.IsCapsuleShape ? (shape.Capsule.CenterA + shape.Capsule.CenterB) * 0.5f : shape.LocalBounds.Center;
			center = firstBody.Transform.PointToWorld( local );
			break;
		}

		f.Capture( 0.9 );
		target.WorldPosition += new Vector3( 0, 60, 0 );
		f.Capture( 0.92 );

		var ray = f.Scene.Trace.Ray( center - Vector3.Forward * 100, center + Vector3.Forward * 100 ).UseHitboxes();
		var result = f.Trace( ray );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.IsTrue( result.Trace.Hit );
		Assert.AreEqual( target, result.Trace.GameObject );
		Assert.IsNotNull( result.Trace.Hitbox.Bone );
		Assert.AreEqual( result.Trace.Hitbox.Bone.Index, result.Trace.Bone );

		var bodyPose = modelHitboxes.Hitboxes[0].Body.Transform;
		f.Trace( ray );
		Assert.IsTrue( modelHitboxes.Hitboxes[0].Body.Transform.AlmostEqual( bodyPose ) );

		var originalModel = renderer.Model;

		renderer.Model = null;
		f.Capture( 1.02 );

		var absent = f.System.TraceAt( f.Command(), ray, 1.02 );

		Assert.IsTrue( absent.Success, absent.Status.ToString() );
		Assert.IsFalse( absent.Trace.Hit, "Removed model hitboxes must not leave historical phantoms" );

		renderer.Model = originalModel;
		f.Capture( 1.04 );

		Assert.AreEqual( LagCompensationStatus.MissingHistory, f.System.TraceAt( f.Command(), ray, 1.04 ).Status );
	}

	[TestMethod]
	public void UnenrolledDynamicObstructionIsCurrentTime()
	{
		using var f = new Fixture();
		f.Box( new Vector3( 100, 0, 0 ) );
		var blocker = f.Box( new Vector3( 50, 60, 0 ), false );

		f.Capture( 0.88 );
		f.Capture( 0.9 );
		blocker.WorldPosition = new Vector3( 50, 0, 0 );
		f.Capture( 0.92 );
		Assert.AreEqual( blocker, f.Trace( f.Ray ).Trace.GameObject );
	}

	[TestMethod]
	public void UnsupportedMeshEnrollmentIsExplicitlyRejected()
	{
		using var f = new Fixture();
		var target = f.Scene.CreateObject();
		target.Components.Create<PlaneCollider>();
		target.Components.Create<LagCompensation>();
		f.Capture( 0.88 );
		f.Capture( 0.92 );

		Assert.AreEqual( LagCompensationStatus.UnsupportedGeometry, f.Trace( f.Ray ).Status );
	}

	[TestMethod]
	public void OwnershipTransitionInvalidatesHistoricalGeneration()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, 0, 0 ) );
		target.NetworkSpawn( f.Peers.Client );

		f.Capture( 0.88 );
		f.Capture( 0.9 );
		target.Network.AssignOwnership( f.Peers.Host );
		f.Capture( 0.92 );

		Assert.AreEqual( LagCompensationStatus.MissingHistory, f.Trace( f.Ray ).Status );
	}

	[TestMethod]
	public void OptOutGeometryRemainsInNormalWorld()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, 0, 0 ) );
		target.GetComponent<LagCompensation>().IncludeColliders = false;

		f.Capture( 0.88 );
		target.WorldPosition = new Vector3( 50, 0, 0 );
		f.Capture( 0.92 );

		var result = f.Trace( f.Ray );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.AreEqual( target, result.Trace.GameObject );
		Assert.AreEqual( 45, result.Trace.EndPosition.x, 0.1f );
	}

	[TestMethod]
	[DataRow( 0 )]
	[DataRow( 1 )]
	[DataRow( 2 )]
	public void SphereResizeCenterAndUniformScaleInvalidateHistory( int change )
	{
		using var f = new Fixture();
		var target = f.Scene.CreateObject();
		target.WorldPosition = new Vector3( 100, 0, 0 );
		var sphere = target.Components.Create<SphereCollider>();
		sphere.Radius = 10;
		target.Components.Create<LagCompensation>();

		f.Capture( 0.88 );
		f.Capture( 0.9 );

		if ( change == 0 )
			sphere.Radius = 20;

		if ( change == 1 )
			sphere.Center = new Vector3( 0, 5, 0 );

		if ( change == 2 )
			target.WorldScale = new Vector3( 2 );

		f.Capture( 0.92 );
		Assert.AreEqual( LagCompensationStatus.MissingHistory, f.Trace( f.Ray ).Status );

		var current = f.System.TraceAt( f.Command( 0.92 ), f.Ray, 1 );

		Assert.IsTrue( current.Success, current.Status.ToString() );
		Assert.AreEqual( target, current.Trace.GameObject );
		Assert.AreEqual( f.Ray.Run().Fraction, current.Trace.Fraction, 0.001f );
	}

	[TestMethod]
	public void RecordingStallCannotInterpolateAcrossMissingFixedSteps()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, -40, 0 ) );
		f.Capture( 0.8 );
		target.WorldPosition = new Vector3( 100, 40, 0 );
		f.Capture( 1 );

		Assert.AreEqual( LagCompensationStatus.MissingHistory, f.Trace( f.Ray ).Status );

		target.WorldPosition = new Vector3( 100, 0, 0 );
		f.Capture( 1.02 );

		var resumed = f.System.TraceAt( f.Command( 1.02 ), f.Ray, 1.02 );

		Assert.IsTrue( resumed.Success, resumed.Status.ToString() );
		Assert.AreEqual( target, resumed.Trace.GameObject );
	}

	[TestMethod]
	public void DistantFreshUnsupportedAndChangedParticipantsDoNotRejectHits()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, 0, 0 ) );
		var changed = f.Box( new Vector3( 100, 1000, 0 ) );
		var unsupported = f.Scene.CreateObject();
		unsupported.WorldPosition = new Vector3( 0, 0, -1000 );
		unsupported.Components.Create<PlaneCollider>();
		unsupported.Components.Create<LagCompensation>();

		f.Capture( 0.88 );
		f.Capture( 0.9 );
		changed.GetComponent<BoxCollider>().Scale = new Vector3( 20 );
		f.Capture( 0.92 );
		f.Box( new Vector3( 100, -1000, 0 ) );

		var result = f.Trace( f.Ray );
		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.AreEqual( target, result.Trace.GameObject );
	}

	[TestMethod]
	public void OverflowingDistantParticipantsDoNotRejectRecordedTarget()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, 0, 0 ) );

		for ( int i = 1; i <= LagCompensationSystem.MaximumParticipants; i++ )
		{
			f.Box( new Vector3( 100, 1000 + i * 20, 0 ) );
		}

		f.Capture( 0.88 );
		f.Capture( 0.92 );

		var result = f.Trace( f.Ray );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.AreEqual( target, result.Trace.GameObject );
	}

	[TestMethod]
	public void ZeroDelayCommandAtCurrentFixedTickUsesLiveTarget()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, 50, 0 ) );
		f.Capture( 0.88 );
		target.WorldPosition = new Vector3( 100, 0, 0 );
		f.Capture( 0.9 );

		using var scope = Time.PushScope( 0.92, 0.02 );
		var result = f.System.TraceAt( f.Command( 0.92 ), f.Ray, 0.92 );

		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.AreEqual( target, result.Trace.GameObject );
	}

	[TestMethod]
	public void TagExcludedUnsupportedParticipantDoesNotRequireHistory()
	{
		using var f = new Fixture();
		var target = f.Box( new Vector3( 100, 0, 0 ) );
		target.Tags.Add( "target" );

		var unsupported = f.Scene.CreateObject();
		unsupported.Components.Create<PlaneCollider>();
		unsupported.Components.Create<LagCompensation>();

		f.Capture( 0.88 );
		f.Capture( 0.92 );

		var result = f.Trace( f.Ray.WithTag( "target" ) );
		Assert.IsTrue( result.Success, result.Status.ToString() );
		Assert.AreEqual( target, result.Trace.GameObject );
	}
}
