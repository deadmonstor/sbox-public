namespace Sandbox;

/// <summary>
/// All input for one fixed movement tick. Duration and movement speeds are chosen by the host.
/// </summary>
[Expose]
internal record struct CharacterPredictionInput( uint Command, Vector3 Move, Angles Eyes, bool Run, bool Duck, bool Jump, Guid Support = default );

/// <summary>
/// A value snapshot of the walking motor. Timers advance with simulation ticks, including replay.
/// </summary>
[Expose]
internal record struct CharacterPredictionState
{
	public Vector3 Position { get; set; }
	public Vector3 Velocity { get; set; }
	public Vector3 WishVelocity { get; set; }
	public Vector3 PushVelocity { get; set; }
	public bool Grounded { get; set; }
	public bool Ducking { get; set; }
	public Guid Ground { get; set; }
	public Transform GroundTransform { get; set; }
	public float GroundedAgo { get; set; }
	public float JumpCooldown { get; set; }
	public float UngroundTime { get; set; }
	public float FallDistance { get; set; }
	public uint JumpCommand { get; set; }
}

[Expose]
internal record struct CharacterPredictionSnapshot( Guid Epoch, Guid Owner, uint Command, CharacterPredictionState State,
	CharacterPredictionSettings Settings = default, float Delta = 0 );

// Supporting-platform poses are sampled locally for first-pass movement. Replay uses
// the displayed world; the host never accepts platform poses from clients.
internal readonly record struct CharacterPredictionFrame( CharacterPredictionInput Input, Guid Ground, Transform? GroundTransform,
	Vector3 CollisionDisplacement = default, Vector3 CollisionVelocity = default, bool HasCollisionVelocity = false );

[Expose]
internal readonly record struct CharacterPredictionSettings(
	Vector3 Up, Vector3 Gravity, float WalkSpeed, float RunSpeed, float DuckSpeed,
	float JumpSpeed, float AccelerationTime, float DeaccelerationTime,
	float GroundAngle, float StepUp, float StepDown, float BodyRadius = 16, float Height = 72, float DuckHeight = 36 );

internal readonly record struct CharacterPredictionTrace(
	bool Hit, bool StartedSolid, float Fraction, Vector3 EndPosition, Vector3 Normal,
	Guid Ground, Transform GroundTransform );

/// <summary>
/// Trace-based walking simulation. It never steps native physics or emits events, so the same
/// function can simulate host commands and replay local commands after a correction.
/// </summary>
internal static class CharacterPrediction
{
	public static CharacterPredictionState ApplyCollisionResponse( CharacterPredictionState state, CharacterPredictionFrame frame )
	{
		state.Position += frame.CollisionDisplacement;
		if ( frame.HasCollisionVelocity ) state.PushVelocity = frame.CollisionVelocity;
		return state;
	}

	/// <summary>
	/// Express a grounded authoritative position in the client's displayed platform frame.
	/// Host and client platform transforms can represent different times due to interpolation.
	/// </summary>
	public static CharacterPredictionState RebaseGround( CharacterPredictionState state, Transform ground )
	{
		if ( !state.Grounded || state.Ground == Guid.Empty ) return state;
		return state with
		{
			Position = ground.PointToWorld( state.GroundTransform.PointToLocal( state.Position ) ),
			Velocity = ground.Rotation * state.GroundTransform.Rotation.Inverse * state.Velocity,
			WishVelocity = ground.Rotation * state.GroundTransform.Rotation.Inverse * state.WishVelocity,
			PushVelocity = ground.Rotation * state.GroundTransform.Rotation.Inverse * state.PushVelocity,
			GroundTransform = ground
		};
	}

	public static CharacterPredictionState Simulate( CharacterPredictionState state, CharacterPredictionInput input,
		CharacterPredictionSettings settings, float delta,
		Func<Vector3, Vector3, bool, CharacterPredictionTrace> trace,
		Func<Guid, Transform?> groundTransform,
		Func<Vector3, Vector3, bool, CharacterPredictionTrace> carryTrace = null )
	{
		var up = settings.Up;
		state.JumpCooldown = MathF.Max( 0, state.JumpCooldown - delta );
		state.UngroundTime = MathF.Max( 0, state.UngroundTime - delta );
		state.GroundedAgo = state.Grounded ? 0 : state.GroundedAgo + delta;

		if ( state.Grounded && state.Ground != Guid.Empty && groundTransform( state.Ground ) is { } ground )
		{
			// Carry the character through both translation and rotation of its supporting object.
			var target = ground.PointToWorld( state.GroundTransform.PointToLocal( state.Position ) );
			// The support may have moved into the old position. Carry sweeps ignore it
			// while still checking other geometry; normal movement and grounding do not.
			var carry = (carryTrace ?? trace)( state.Position, target, state.Ducking );
			if ( !carry.StartedSolid ) state.Position = carry.EndPosition;
			var rotation = ground.Rotation * state.GroundTransform.Rotation.Inverse;
			state.Velocity = rotation * state.Velocity;
			state.WishVelocity = rotation * state.WishVelocity;
			state.PushVelocity = rotation * state.PushVelocity;
			state.GroundTransform = ground;
		}

		if ( input.Duck ) state.Ducking = true;
		else if ( state.Ducking && !trace( state.Position, state.Position, false ).StartedSolid ) state.Ducking = false;

		var move = input.Move.ClampLength( 1 );
		var direction = input.Eyes.WithPitch( 0 ).WithRoll( 0 ).ToRotation() * move;
		if ( input.Support != Guid.Empty && groundTransform( input.Support ) is { } inputSupport )
			direction = inputSupport.Rotation * direction;
		direction -= up * direction.Dot( up );
		var speed = state.Ducking ? settings.DuckSpeed : input.Run ? settings.RunSpeed : settings.WalkSpeed;
		var targetWish = direction * speed;
		var smoothTime = targetWish.Length < state.WishVelocity.Length ? settings.DeaccelerationTime : settings.AccelerationTime;
		state.WishVelocity = smoothTime > 0
			? Vector3.Lerp( state.WishVelocity, targetWish, 1 - MathF.Exp( -delta / smoothTime ) )
			: targetWish;

		var vertical = up * state.Velocity.Dot( up );
		var horizontal = state.Velocity - vertical;
		if ( state.Grounded ) horizontal = state.WishVelocity;
		else horizontal += (state.WishVelocity - horizontal).ClampLength( settings.RunSpeed * delta );
		state.Velocity = horizontal + vertical;

		if ( input.Jump && state.GroundedAgo <= 0.33f && state.JumpCooldown <= 0 && settings.JumpSpeed > 0 )
		{
			state.Velocity = horizontal + up * settings.JumpSpeed;
			state.Grounded = false;
			state.Ground = Guid.Empty;
			state.UngroundTime = 0.2f;
			state.JumpCooldown = 0.5f;
			state.JumpCommand = input.Command;
		}

		if ( !state.Grounded ) state.Velocity += settings.Gravity * delta;
		else state.Velocity -= up * state.Velocity.Dot( up );

		state.PushVelocity *= MathF.Exp( -delta * 8 );
		var pushed = Slide( state.Position, state.PushVelocity, state.Ducking, delta, trace );
		state.Position = pushed.Position;
		state.PushVelocity = pushed.Velocity;

		var before = state.Position;
		var wasGrounded = state.Grounded;
		var moved = Slide( state.Position, state.Velocity, state.Ducking, delta, trace );
		if ( wasGrounded && settings.StepUp > 0 )
		{
			var lift = trace( before, before + up * settings.StepUp, state.Ducking );
			if ( !lift.StartedSolid && !lift.Hit )
			{
				var step = Slide( lift.EndPosition, state.Velocity, state.Ducking, delta, trace );
				var down = trace( step.Position, step.Position - up * (settings.StepUp + settings.StepDown), state.Ducking );
				if ( Standable( down, settings ) && HorizontalDistance( before, down.EndPosition, up ) > HorizontalDistance( before, moved.Position, up ) + 0.01f )
					moved = (down.EndPosition + up * 0.01f, step.Velocity);
			}
		}
		state.Position = moved.Position;
		state.Velocity = moved.Velocity;
		state.FallDistance += MathF.Max( 0, -(state.Position - before).Dot( up ) );

		state.Grounded = false;
		state.Ground = Guid.Empty;
		if ( state.UngroundTime <= 0 && (wasGrounded || state.Velocity.Dot( up ) <= 0) )
		{
			var distance = wasGrounded ? MathF.Max( 2, settings.StepDown ) : 2;
			var groundHit = trace( state.Position + up * 0.1f, state.Position - up * distance, state.Ducking );
			if ( Standable( groundHit, settings ) )
			{
				state.Position = groundHit.EndPosition + up * 0.01f;
				state.Grounded = true;
				state.Ground = groundHit.Ground;
				state.GroundTransform = groundHit.GroundTransform;
				state.GroundedAgo = 0;
				state.Velocity -= up * state.Velocity.Dot( up );
				state.FallDistance = 0;
			}
		}
		return state;
	}

	static bool Standable( CharacterPredictionTrace trace, CharacterPredictionSettings settings )
	{
		return trace.Hit && !trace.StartedSolid && trace.Normal.Dot( settings.Up ) >= MathF.Cos( settings.GroundAngle.DegreeToRadian() );
	}

	static float HorizontalDistance( Vector3 from, Vector3 to, Vector3 up )
	{
		var delta = to - from;
		return (delta - up * delta.Dot( up )).LengthSquared;
	}

	static (Vector3 Position, Vector3 Velocity) Slide( Vector3 position, Vector3 velocity, bool duck,
		float delta, Func<Vector3, Vector3, bool, CharacterPredictionTrace> trace )
	{
		var remaining = delta;
		Span<Vector3> planes = stackalloc Vector3[4];
		int planeCount = 0;
		for ( int bump = 0; bump < 4 && remaining > 0; bump++ )
		{
			var hit = trace( position, position + velocity * remaining, duck );
			if ( hit.StartedSolid ) return (position, Vector3.Zero);
			position = hit.EndPosition;
			if ( !hit.Hit ) break;
			remaining *= 1 - hit.Fraction.Clamp( 0, 1 );
			position += hit.Normal * 0.01f;
			planes[planeCount++] = hit.Normal;
			for ( int i = 0; i < planeCount; i++ )
			{
				var into = velocity.Dot( planes[i] );
				if ( into < 0 ) velocity -= planes[i] * into;
			}
			// Stop in a corner rather than reintroducing motion into an earlier plane.
			for ( int i = 0; i < planeCount; i++ )
				if ( velocity.Dot( planes[i] ) < -0.01f ) return (position, Vector3.Zero);
		}
		return (position, velocity);
	}
}
