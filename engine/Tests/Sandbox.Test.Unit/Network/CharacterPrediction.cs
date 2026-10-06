using System;
using Sandbox;

namespace NetworkTests;

[TestClass]
public class CharacterPredictionTest
{
	static readonly Guid FloorId = Guid.NewGuid();
	static readonly CharacterPredictionSettings Settings = new( Vector3.Up, Vector3.Down * 800,
		100, 200, 50, 300, 0, 0, 45, 18, 18 );
	const float Delta = 0.02f;

	static CharacterPredictionState Standing => new()
	{
		Position = Vector3.Up * 0.01f,
		Grounded = true,
		Ground = FloorId,
		GroundTransform = Transform.Zero
	};

	static CharacterPredictionTrace Floor( Vector3 from, Vector3 to, bool duck )
	{
		if ( from.z < -0.001f ) return new( true, true, 0, from, Vector3.Up, FloorId, Transform.Zero );
		if ( to.z < 0 && from.z >= 0 )
		{
			var fraction = from.z / (from.z - to.z);
			return new( true, false, fraction, Vector3.Lerp( from, to, fraction ), Vector3.Up, FloorId, Transform.Zero );
		}
		return new( false, false, 1, to, default, default, default );
	}

	static CharacterPredictionState Step( CharacterPredictionState state, CharacterPredictionInput input,
		Func<Vector3, Vector3, bool, CharacterPredictionTrace> trace = null )
	{
		return CharacterPrediction.Simulate( state, input, Settings, Delta, trace ?? Floor, _ => null );
	}

	[TestMethod]
	public void SupportedMovementMatchesAcrossDifferentDisplayedRotations()
	{
		CharacterPredictionState Run( float yaw )
		{
			var support = new Transform( Vector3.Zero, Rotation.FromYaw( yaw ) );
			var state = Standing with { GroundTransform = support };
			CharacterPredictionTrace Trace( Vector3 from, Vector3 to, bool duck )
				=> Floor( from, to, duck ) with { GroundTransform = support };
			for ( uint command = 1; command <= 20; command++ )
				state = CharacterPrediction.Simulate( state,
					new( command, command <= 10 ? Vector3.Forward : Vector3.Zero, default, false, false, false, FloorId ),
					Settings, Delta, Trace, _ => support );
			return state with { Position = support.PointToLocal( state.Position ) };
		}
		Assert.IsTrue( Run( 40 ).Position.AlmostEqual( Run( 25 ).Position, 0.001f ) );
	}

	[TestMethod]
	public void WalkingRunningAndDuckingUseHostSpeeds()
	{
		var input = new CharacterPredictionInput( 1, Vector3.Forward * 10, default, false, false, false );
		var walking = Step( Standing, input );
		Assert.AreEqual( 2f, walking.Position.x, 0.001f );
		Assert.IsTrue( walking.Grounded );
		Assert.AreEqual( 4f, Step( Standing, input with { Run = true } ).Position.x, 0.001f );
		Assert.AreEqual( 1f, Step( Standing, input with { Run = true, Duck = true } ).Position.x, 0.001f );
	}

	[TestMethod]
	public void JumpCooldownAndLandingArePartOfReplayState()
	{
		var state = Step( Standing, new( 1, default, default, false, false, true ) );
		Assert.IsFalse( state.Grounded );
		Assert.AreEqual( 284f, state.Velocity.z, 0.001f );
		Assert.AreEqual( 1u, state.JumpCommand );
		state = Step( state, new( 2, default, default, false, false, true ) );
		Assert.AreEqual( 1u, state.JumpCommand, "A second jump cannot bypass the cooldown" );
		for ( uint command = 3; command < 100 && !state.Grounded; command++ )
			state = Step( state, new( command, default, default, false, false, false ) );
		Assert.IsTrue( state.Grounded );
		Assert.AreEqual( 0f, state.Velocity.z, 0.001f );
		Assert.AreEqual( 0f, state.FallDistance );
	}

	[TestMethod]
	public void CrouchCannotExpandIntoACeiling()
	{
		static CharacterPredictionTrace Ceiling( Vector3 from, Vector3 to, bool duck )
		{
			if ( !duck ) return new( true, true, 0, from, Vector3.Down, default, default );
			return Floor( from, to, duck );
		}
		var state = Step( Standing, new( 1, default, default, false, true, false ) );
		state = Step( state, new( 2, default, default, false, false, false ), Ceiling );
		Assert.IsTrue( state.Ducking );
		state = Step( state, new( 3, default, default, false, false, false ) );
		Assert.IsFalse( state.Ducking );
	}

	[TestMethod]
	public void WallCollisionStopsMotionWithoutPassingThrough()
	{
		static CharacterPredictionTrace Wall( Vector3 from, Vector3 to, bool duck )
		{
			if ( to.x > 1 && from.x <= 1 )
			{
				var fraction = (1 - from.x) / (to.x - from.x);
				return new( true, false, fraction, Vector3.Lerp( from, to, fraction ), Vector3.Backward, default, default );
			}
			return Floor( from, to, duck );
		}
		var state = Step( Standing, new( 1, Vector3.Forward, default, false, false, false ), Wall );
		Assert.IsTrue( state.Position.x <= 1 );
		Assert.AreEqual( 0f, state.Velocity.x );
	}

	[TestMethod]
	public void StepUpChoosesTheRouteWithMoreForwardProgress()
	{
		static CharacterPredictionTrace Stairs( Vector3 from, Vector3 to, bool duck )
		{
			if ( to.x > 1 && from.x <= 1 && from.z < 12 )
			{
				var fraction = (1 - from.x) / (to.x - from.x);
				return new( true, false, fraction, Vector3.Lerp( from, to, fraction ), Vector3.Backward, FloorId, Transform.Zero );
			}
			var height = from.x > 1 ? 12 : 0;
			if ( to.z < height && from.z >= height )
			{
				var fraction = (from.z - height) / (from.z - to.z);
				return new( true, false, fraction, Vector3.Lerp( from, to, fraction ), Vector3.Up, FloorId, Transform.Zero );
			}
			return new( false, false, 1, to, default, default, default );
		}
		var state = Step( Standing, new( 1, Vector3.Forward, default, false, false, false ), Stairs );
		Assert.AreEqual( 2f, state.Position.x, 0.001f );
		Assert.AreEqual( 12.01f, state.Position.z, 0.001f );
		Assert.IsTrue( state.Grounded );
	}

	[TestMethod]
	public void PlatformTranslationCarriesThePlayer()
	{
		var platform = Transform.Zero.WithPosition( Vector3.Forward * 10 );
		var state = CharacterPrediction.Simulate( Standing, new( 1, default, default, false, false, false ),
			Settings, Delta, Floor, _ => platform );
		Assert.AreEqual( 10f, state.Position.x, 0.001f );
	}

	[TestMethod]
	public void CorrectionAndReplayMatchAuthoritativeSimulation()
	{
		var history = new PredictionHistory<CharacterPredictionInput, CharacterPredictionState>( 128 );
		var predicted = Standing;
		var authoritative = Standing with { Position = Standing.Position + Vector3.Right * 5 };
		CharacterPredictionState acknowledged = default;
		for ( uint command = 1; command <= 40; command++ )
		{
			var input = new CharacterPredictionInput( command, Vector3.Forward, default, false, command > 20, command == 5 );
			predicted = Step( predicted, input );
			authoritative = Step( authoritative, input );
			history.Record( command, input, predicted );
			if ( command == 10 ) acknowledged = authoritative;
		}
		Assert.IsTrue( history.Reconcile( 10, acknowledged, ( state, input ) => Step( state, input ), out var reconciled ) );
		Assert.AreEqual( authoritative, reconciled );
	}
	[TestMethod]
	public void PlatformCorrectionPreservesRelativePositionAcrossInterpolationDelay()
	{
		var hostPlatform = new Transform( new Vector3( 100, 20, 0 ), Rotation.FromYaw( 40 ) );
		var displayedPlatform = new Transform( new Vector3( 80, 10, 0 ), Rotation.FromYaw( 25 ) );
		var localPosition = new Vector3( 12, 8, 0.01f );
		var authoritative = Standing with { Position = hostPlatform.PointToWorld( localPosition ), GroundTransform = hostPlatform };
		var rebased = CharacterPrediction.RebaseGround( authoritative, displayedPlatform );
		Assert.IsTrue( rebased.Position.AlmostEqual( displayedPlatform.PointToWorld( localPosition ), 0.001f ) );
		Assert.AreEqual( displayedPlatform, rebased.GroundTransform );

		CharacterPredictionTrace DisplayedFloor( Vector3 from, Vector3 to, bool duck )
			=> Floor( from, to, duck ) with { GroundTransform = displayedPlatform };
		for ( uint command = 1; command <= 12; command++ )
			rebased = CharacterPrediction.Simulate( rebased, new( command, default, default, false, false, false ),
				Settings, Delta, DisplayedFloor, _ => displayedPlatform );
		Assert.IsTrue( rebased.Position.AlmostEqual( displayedPlatform.PointToWorld( localPosition ), 0.001f ),
			"Replaying pending commands must not reapply historical platform movement" );
	}

	[TestMethod]
	public void AirborneCorrectionDoesNotAttachToAPlatform()
	{
		var airborne = Standing with { Grounded = false, Position = Vector3.Up * 80 };
		Assert.AreEqual( airborne, CharacterPrediction.RebaseGround( airborne, Transform.Zero.WithPosition( Vector3.Forward * 100 ) ) );
	}
	[TestMethod]
	public void SupportingPlatformOverlapDoesNotBlockCarry()
	{
		var platform = Transform.Zero.WithPosition( Vector3.Forward * 10 );
		CharacterPredictionTrace Overlap( Vector3 from, Vector3 to, bool duck )
		{
			if ( from.x < 5 ) return new( true, true, 0, from, Vector3.Up, FloorId, platform );
			return Floor( from, to, duck ) with { GroundTransform = platform };
		}
		var carried = CharacterPrediction.Simulate( Standing, new( 1, default, default, false, false, false ),
			Settings, Delta, Overlap, _ => platform, Floor );
		Assert.AreEqual( 10f, carried.Position.x, 0.001f );
		Assert.IsTrue( carried.Grounded );
		var blocked = CharacterPrediction.Simulate( Standing, new( 1, default, default, false, false, false ),
			Settings, Delta, Overlap, _ => platform, ( from, to, duck ) => new( true, true, 0, from, Vector3.Backward, default, default ) );
		Assert.AreEqual( 0f, blocked.Position.x, 0.001f, "Other blocking geometry must still prevent carry" );
	}
	[TestMethod]
	public void CollisionMomentumSurvivesIdleInputAndReplay()
	{
		var pushed = Standing with { PushVelocity = Vector3.Forward * 100 };
		var next = Step( pushed, new( 1, default, default, false, false, false ) );
		Assert.IsTrue( next.Position.x > 1, "Idle input must not erase a native collision push" );
		Assert.IsTrue( next.PushVelocity.x > 0 && next.PushVelocity.x < 100 );
		Assert.AreEqual( next, Step( pushed, new( 1, default, default, false, false, false ) ) );
	}
}
