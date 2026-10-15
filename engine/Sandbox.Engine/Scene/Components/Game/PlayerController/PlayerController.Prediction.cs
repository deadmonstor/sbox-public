using Sandbox.Movement;

namespace Sandbox;

public sealed partial class PlayerController : IPredictionParticipant
{
	/// <summary>
	/// Uses host-authoritative, trace-based walking on the root's shared prediction timeline.
	/// Requires fixed updates, a 3D scene, and the built-in walking mode; configure on the host.
	/// </summary>
	[Property, Sync( SyncFlags.FromHost ), Group( "Networking" )]
	public bool UseClientPrediction
	{
		get;
		set
		{
			field = value;

			// Include the shared timeline in the initial network spawn.
			if ( value && IsValid && !Scene.IsEditor && IsPredictionAuthority )
			{
				Prediction.GetOrCreate( this );
			}
		}
	}

	/// <summary>
	/// Number of position corrections received from the host.
	/// </summary>
	public int PredictionCorrections { get; private set; }

	CharacterPredictionState _predictionState;
	CharacterPredictionFrame _predictionPendingFrame;
	CharacterPredictionSettings _predictionSettings;
	Prediction _prediction;
	ControllerPredictionData _predictionData;
	bool _predictionActive;
	bool _predictionSavedMotion;
	uint? _predictionPresentedJump;
	Vector3 _predictionVisualOffset;
	CharacterPredictionFrame _predictionTraceFrame;
	Guid _predictionCarryGround;
	Func<Vector3, Vector3, bool, CharacterPredictionTrace> _predictionTrace;
	Func<Guid, Transform?> _predictionGround;
	Func<Vector3, Vector3, bool, CharacterPredictionTrace> _predictionCarry;

	internal bool HasPredictionPhysicsAuthority => _predictionActive && (IsPredictionAuthority || !IsProxy);

	bool IsPredictionAuthority => !Networking.IsActive || Networking.IsHost;

	void IPredictionParticipant.UpdatePredictionRegistration() => UpdatePredictionMode();

	internal bool UpdatePredictionMode()
	{
		if ( UseClientPrediction && !Scene.IsEditor )
		{
			if ( !ProjectSettings.Physics.UseFixedUpdate || Scene.Is2D || !UseInputControls || Mode?.GetType() != typeof( MoveModeWalk )
				|| (Networking.IsActive && Network.RootGameObject != GameObject) )
			{
				if ( IsPredictionAuthority )
				{
					Log.Warning( "Player prediction requires fixed updates, the built-in 3D walking mode, default input controls, and a network root object." );
					UseClientPrediction = false;
				}

				StopPrediction();
				return false;
			}
		}

		if ( UseClientPrediction && !Scene.IsEditor && !_predictionActive )
		{
			var runner = Prediction.GetOrCreate( this );

			if ( runner is null )
			{
				return false;
			}

			_predictionSavedMotion = Body.MotionEnabled;
			_predictionActive = true;
			_predictionState = new CharacterPredictionState { Position = WorldPosition, GroundedAgo = 1 };
			_prediction = runner;
			_predictionData ??= new ControllerPredictionData( this );
			_prediction.Register( _predictionData );
		}

		if ( !UseClientPrediction && _predictionActive )
		{
			StopPrediction();
		}

		if ( !_predictionActive )
		{
			return false;
		}

		Body.HasPredictionPhysicsAuthority = HasPredictionPhysicsAuthority;
		Body.MotionEnabled = HasPredictionPhysicsAuthority;

		if ( IsPredictionAuthority )
		{
			var walk = (MoveModeWalk)Mode;
			_predictionSettings = new CharacterPredictionSettings( UpDirection, Scene.PhysicsWorld.Gravity,
				WalkSpeed, RunSpeed, DuckedSpeed, JumpSpeed, AccelerationTime, DeaccelerationTime,
				walk.GroundAngle, walk.StepUpHeight, walk.StepDownHeight, BodyRadius, BodyHeight, DuckedHeight );
		}

		return true;
	}

	void ResetPredictionTimeline( bool preserveState = true )
	{
		_predictionState = preserveState
			? _predictionState with { Position = WorldPosition }
			: new CharacterPredictionState { Position = WorldPosition, GroundedAgo = 1 };
		_prediction?.ResetTimeline();
	}

	void StopPrediction()
	{
		if ( !_predictionActive )
		{
			return;
		}

		_predictionActive = false;
		_prediction?.Unregister( _predictionData );
		_prediction = null;
		_predictionVisualOffset = 0;

		if ( Body.IsValid() )
		{
			Body.HasPredictionPhysicsAuthority = false;
			Body.MotionEnabled = _predictionSavedMotion;
		}
	}

	void DetectPredictionTeleport()
	{
		if ( IsPredictionAuthority && (_predictionState.Position - WorldPosition).Length > 0.1f )
		{
			ResetPredictionTimeline( false );
		}
	}

	void PredictionPrePhysicsStep()
	{
		if ( !HasPredictionPhysicsAuthority )
		{
			return;
		}

		DetectPredictionTeleport();

		// The motor integrates movement; native physics supplies contact response only.
		Body.Gravity = false;
		Body.LinearDamping = 0;
		Body.Velocity = Vector3.Zero;
		Body.AngularVelocity = Vector3.Zero;
	}

	void CompletePredictionPhysics( in PredictionContext context )
	{
		if ( !HasPredictionPhysicsAuthority || !Body.PhysicsBody.IsValid() )
		{
			return;
		}

		var velocity = Body.PhysicsBody.Velocity;
		var frame = _predictionPendingFrame with
		{
			CollisionDisplacement = Body.PhysicsBody.Transform.Position - _predictionState.Position,
			CollisionVelocity = velocity,
			HasCollisionVelocity = velocity.IsFinite && velocity.Length > 0.1f
		};

		_predictionState = CharacterPrediction.ApplyCollisionResponse( _predictionState, frame );
		_predictionData.UpdateFrame( context.Command, frame );
		ApplyPredictionState( true );
		RefreshPredictionGroundSurface();
	}

	void PredictionFixedUpdate()
	{
		DetectPredictionTeleport();
		_prediction.Step();

		if ( IsProxy && !IsPredictionAuthority )
		{
			ApplyPredictionState( false );
		}

		UpdateHeadroom();
		UpdateBody();
		UpdateEyeTransform();
	}

	CharacterPredictionInput CapturePredictionInput()
	{
		var eyes = EyeAngles;

		if ( _predictionState.Grounded && PredictionGroundTransform( _predictionState.Ground ) is { } support )
		{
			eyes = (support.Rotation.Inverse * eyes.WithPitch( 0 ).WithRoll( 0 ).ToRotation()).Angles();
		}

		return new CharacterPredictionInput( Input.AnalogMove, eyes,
			Input.Down( AltMoveButton ) != RunByDefault, Input.Down( "duck" ), Input.Pressed( "Jump" ), _predictionState.Grounded ? _predictionState.Ground : Guid.Empty );
	}

	CharacterPredictionFrame CapturePredictionFrame( CharacterPredictionInput input )
		=> new( input, _predictionState.Ground, PredictionGroundTransform( _predictionState.Ground ) );

	CharacterPredictionState SimulatePredictionFrame( CharacterPredictionState state, CharacterPredictionFrame frame, uint command, float delta )
	{
		_predictionTraceFrame = frame;
		_predictionCarryGround = state.Ground;
		_predictionTrace ??= TracePredictionFrame;
		_predictionGround ??= PredictionFrameGroundTransform;
		_predictionCarry ??= TracePredictionCarry;

		return CharacterPrediction.Simulate( state, frame.Input, command, _predictionSettings, delta, _predictionTrace, _predictionGround, _predictionCarry );
	}

	CharacterPredictionTrace TracePredictionFrame( Vector3 from, Vector3 to, bool duck )
	{
		var hit = TracePredictionBody( from, to, duck );

		if ( hit.Ground == _predictionTraceFrame.Ground && _predictionTraceFrame.GroundTransform is { } ground )
		{
			hit = hit with { GroundTransform = ground };
		}

		return hit;
	}

	Transform? PredictionFrameGroundTransform( Guid id )
		=> id == _predictionTraceFrame.Ground ? _predictionTraceFrame.GroundTransform : PredictionGroundTransform( id );

	CharacterPredictionTrace TracePredictionCarry( Vector3 from, Vector3 to, bool duck )
		=> TracePredictionBody( from, to, duck, Scene.Directory.FindByGuid( _predictionCarryGround ) );

	CharacterPredictionTrace TracePredictionBody( Vector3 from, Vector3 to, bool duck, GameObject ignore = null )
	{
		var height = duck ? _predictionSettings.DuckHeight : _predictionSettings.Height;
		var radius = _predictionSettings.BodyRadius * 0.5f;
		var center = UpDirection * height * 0.5f;
		var extents = new Vector3( radius ) + UpDirection.Abs() * (height * 0.5f - radius);
		var query = Scene.Trace.Box( new BBox( center - extents, center + extents ), from, to )
			.IgnoreGameObjectHierarchy( GameObject ).WithCollisionRules( Tags );

		if ( ignore.IsValid() )
		{
			query = query.IgnoreGameObjectHierarchy( ignore );
		}

		var tr = query.Run();

		return new CharacterPredictionTrace( tr.Hit, tr.StartedSolid, tr.Fraction, tr.EndPosition, tr.Normal,
			tr.GameObject?.Id ?? Guid.Empty, tr.GameObject?.WorldTransform ?? global::Transform.Zero );
	}

	Transform? PredictionGroundTransform( Guid id )
	{
		var ground = Scene.Directory.FindByGuid( id );

		return ground.IsValid() ? ground.WorldTransform : null;
	}

	void ApplyPredictionState( bool position )
	{
		if ( position )
		{
			WorldPosition = _predictionState.Position;
		}

		Velocity = _predictionState.Velocity + _predictionState.PushVelocity;
		GroundVelocity = 0;
		TimeSinceGrounded = _predictionState.GroundedAgo;
	}

	void RefreshPredictionGroundSurface()
	{
		GroundObject = _predictionState.Grounded ? Scene.Directory.FindByGuid( _predictionState.Ground ) : null;
		GroundComponent = GroundObject?.GetComponent<Collider>();
		GroundIsDynamic = GroundComponent is Collider collider && collider.IsDynamic;

		if ( GroundObject.IsValid() )
		{
			var tr = TraceBody( WorldPosition + UpDirection * 0.1f, WorldPosition - UpDirection * 2 );
			GroundSurface = tr.Surface;
		}
	}

	void EmitPredictionEvents( CharacterPredictionState before, uint command )
	{
		var epoch = _prediction.Epoch;

		if ( _predictionState.JumpCommand == command && _predictionState.JumpCooldown > before.JumpCooldown )
		{
			_predictionPresentedJump = command;

			if ( UseAnimatorControls && Renderer.IsValid() )
			{
				Renderer.Set( "b_jump", true );
			}

			if ( IsPredictionAuthority )
			{
				IEvents.PostToGameObject( GameObject, x => x.OnJumped() );
			}

			if ( _prediction?.Epoch != epoch )
			{
				return;
			}
		}

		if ( !before.Grounded && _predictionState.Grounded && before.FallDistance > 1 )
		{
			if ( IsPredictionAuthority )
			{
				IEvents.PostToGameObject( GameObject, x => x.OnLanded( before.FallDistance, before.Velocity ) );
			}

			if ( _prediction?.Epoch != epoch )
			{
				return;
			}

			if ( !IsProxy && EnableFootstepSounds )
			{
				PlayFootstepSound( WorldPosition, 1, 0 );
				PlayFootstepSound( WorldPosition, 1, 1 );
			}
		}
	}

	void PresentPredictionJump()
	{
		if ( !IsProxy || _predictionState.JumpCooldown <= 0 || _predictionPresentedJump == _predictionState.JumpCommand )
		{
			return;
		}

		_predictionPresentedJump = _predictionState.JumpCommand;

		if ( UseAnimatorControls && Renderer.IsValid() )
		{
			Renderer.Set( "b_jump", true );
		}
	}

	sealed class ControllerPredictionData( PlayerController owner ) : PredictionParticipantData<CharacterPredictionInput, CharacterPredictionValueState>
	{
		readonly CharacterPredictionFrame[] frames = new CharacterPredictionFrame[Prediction.Capacity];
		CharacterPredictionState before;

		internal override Component Component => owner;

		internal override int Order => -100;

		protected override CharacterPredictionInput CaptureInput() => owner.CapturePredictionInput();

		protected override CharacterPredictionValueState CaptureState() => new( owner._predictionState, owner._predictionSettings );

		protected override void RestoreState( in CharacterPredictionValueState value )
		{
			owner._predictionState = value.State;
			owner._predictionSettings = value.Settings;

			if ( !owner.IsProxy && owner._predictionState.Grounded && owner.PredictionGroundTransform( owner._predictionState.Ground ) is { } ground )
			{
				owner._predictionState = CharacterPrediction.RebaseGround( owner._predictionState, ground );
			}

			owner.ApplyPredictionState( !owner.IsProxy );
			owner.RefreshPredictionGroundSurface();
		}

		protected override bool ValidateInput( ref CharacterPredictionInput input )
		{
			if ( !input.Move.IsFinite || !float.IsFinite( input.Eyes.pitch ) || !float.IsFinite( input.Eyes.yaw ) || !float.IsFinite( input.Eyes.roll ) )
			{
				return false;
			}

			input.Move = input.Move.ClampLength( 1 );
			input.Eyes = input.Eyes.WithPitch( input.Eyes.pitch.Clamp( -90, 90 ) ).WithRoll( 0 );

			return true;
		}

		protected override void Simulate( in CharacterPredictionInput input, in PredictionContext context )
		{
			before = owner._predictionState;
			var frame = context.IsReplay ? frames[context.Command % Prediction.Capacity] : owner.CapturePredictionFrame( input );

			if ( context.IsReplay )
			{
				if ( frame.GroundTransform is { } recordedGround && owner.PredictionGroundTransform( frame.Ground ) is { } displayedSupport )
				{
					var rotation = displayedSupport.Rotation * recordedGround.Rotation.Inverse;
					frame = frame with
					{
						CollisionDisplacement = rotation * frame.CollisionDisplacement,
						CollisionVelocity = rotation * frame.CollisionVelocity
					};
				}

				owner._predictionState = CharacterPrediction.ApplyCollisionResponse(
					owner.SimulatePredictionFrame( before, frame with { Ground = Guid.Empty, GroundTransform = null }, context.Command, context.Delta ), frame );
			}
			else
			{
				frames[context.Command % Prediction.Capacity] = frame;
				owner._predictionPendingFrame = frame;
				owner._predictionState = owner.SimulatePredictionFrame( before, frame, context.Command, context.Delta );
			}

			owner.ApplyPredictionState( true );

			if ( !context.IsReplay )
			{
				owner.RefreshPredictionGroundSurface();
			}
		}

		protected override void AfterSimulate( in CharacterPredictionInput input, in PredictionContext context )
			=> owner.EmitPredictionEvents( before, context.Command );

		internal void UpdateFrame( uint command, CharacterPredictionFrame frame ) => frames[command % Prediction.Capacity] = frame;

		internal override void FinalizeSimulation( in PredictionContext context ) => owner.CompletePredictionPhysics( context );

		protected override void OnCorrected( in CharacterPredictionValueState previous, in CharacterPredictionValueState corrected )
		{
			var displayedPrevious = previous.State;

			if ( displayedPrevious.Grounded && owner.PredictionGroundTransform( displayedPrevious.Ground ) is { } ground )
			{
				displayedPrevious = CharacterPrediction.RebaseGround( displayedPrevious, ground );
			}

			var correction = displayedPrevious.Position - corrected.State.Position;

			if ( correction.Length > 0.01f )
			{
				owner.PredictionCorrections++;
				var offset = owner._predictionVisualOffset + correction;
				owner._predictionVisualOffset = offset.Length < 64 ? offset : Vector3.Zero;
			}
		}

		internal override void Reset()
		{
			base.Reset();
			Array.Clear( frames );

			if ( (owner._predictionState.Position - owner.WorldPosition).Length > 0.1f )
			{
				owner._predictionState = new CharacterPredictionState { Position = owner.WorldPosition, GroundedAgo = 1 };
			}

			owner._predictionVisualOffset = 0;
			owner._predictionPresentedJump = null;
		}
	}
}
