using Sandbox.Movement;

namespace Sandbox;

public sealed partial class PlayerController
{
	/// <summary>
	/// Use host-authoritative, trace-based walking with prediction on the owning client.
	/// Requires fixed updates, a 3D scene, and the built-in walking mode. Configure on the host.
	/// </summary>
	[Property, Sync( SyncFlags.FromHost ), Group( "Networking" )]
	public bool UseClientPrediction { get; set; }

	/// <summary>
	/// Log prediction snapshots, corrections, and native contact responses for diagnosis.
	/// </summary>
	[Property, Sync( SyncFlags.FromHost ), Group( "Networking" )]
	public bool PredictionDebugLogging { get; set; }

	/// <summary>
	/// Number of authoritative corrections applied by this client.
	/// </summary>
	public int PredictionCorrections { get; private set; }

	[Sync( SyncFlags.FromHost )]
	private CharacterPredictionSnapshot PredictionSnapshot { get; set; }

	[Sync( SyncFlags.FromHost )]
	private bool PredictionOwnsTransformAuthority { get; set; }

	const int PredictionCapacity = 128;
	const int PredictionBatchSize = 16;
	readonly PredictionHistory<CharacterPredictionFrame, CharacterPredictionState> _predictionHistory = new( PredictionCapacity );
	readonly Dictionary<uint, CharacterPredictionInput> _predictionCommands = new();
	CharacterPredictionState _predictionState;
	CharacterPredictionFrame _predictionPendingFrame;
	CharacterPredictionSettings _predictionSettings;
	Guid _predictionEpoch;
	Guid _predictionResetEpoch;
	Guid _predictionOwner;
	bool _predictionHost;
	bool _predictionActive;
	bool _predictionHasState;
	bool _predictionSavedMotion;
	uint _predictionPresentedJump;
	bool _predictionHasPresentedJump;
	uint _predictionNextCommand;
	uint _predictionAcknowledgedCommand;
	float _predictionCredit;
	float _predictionDelta;
	float _predictionLocalDelta;
	Vector3 _predictionVisualOffset;

	internal bool IsPredictingLocally => _predictionActive && !IsProxy;
	internal bool HasPredictionPhysicsAuthority => _predictionActive && (IsPredictionAuthority || !IsProxy);
	bool IsPredictionAuthority => !Networking.IsActive || Networking.IsHost;

	void LogPrediction( string message )
	{
		if ( !PredictionDebugLogging ) return;
		Log.Info( $"[prediction-diag-v1] role={(IsPredictionAuthority ? "host" : "client")} player={GameObject.Id} owner={_predictionOwner} time={Time.Now:F3} epoch={_predictionEpoch} {message}" );
	}

	bool UpdatePredictionMode()
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
			_predictionSavedMotion = Body.MotionEnabled;
			_predictionActive = true;
			Body.MotionEnabled = HasPredictionPhysicsAuthority;
			ResetPredictionTimeline();
		}

		if ( !UseClientPrediction && _predictionActive ) StopPrediction();
		if ( !_predictionActive ) return false;

		if ( _predictionOwner != Network.OwnerId || _predictionHost != IsPredictionAuthority
			|| (_predictionLocalDelta > 0 && MathF.Abs( _predictionLocalDelta - Time.Delta ) > 0.0001f) )
			ResetPredictionTimeline();

		Body.MotionEnabled = HasPredictionPhysicsAuthority;
		_predictionLocalDelta = Time.Delta;
		if ( IsPredictionAuthority )
		{
			_predictionDelta = Time.Delta;
			var walk = (MoveModeWalk)Mode;
			_predictionSettings = new CharacterPredictionSettings( UpDirection, Scene.PhysicsWorld.Gravity,
				WalkSpeed, RunSpeed, DuckedSpeed, JumpSpeed, AccelerationTime, DeaccelerationTime,
				walk.GroundAngle, walk.StepUpHeight, walk.StepDownHeight, BodyRadius, BodyHeight, DuckedHeight );
		}
		if ( IsPredictionAuthority && (Network.Flags & NetworkFlags.HostTransformAuthority) == 0 )
		{
			PredictionOwnsTransformAuthority = true;
			Network.Flags |= NetworkFlags.HostTransformAuthority;
		}

		return true;
	}

	void ResetPredictionTimeline( bool preserveState = true )
	{
		_predictionOwner = Network.OwnerId;
		_predictionHost = IsPredictionAuthority;
		_predictionEpoch = IsPredictionAuthority ? Guid.NewGuid() : Guid.Empty;
		_predictionResetEpoch = Guid.Empty;
		_predictionState = preserveState && _predictionHasState
			? _predictionState with { Position = WorldPosition }
			: new CharacterPredictionState { Position = WorldPosition, GroundedAgo = 1 };
		_predictionHasState = true;
		_predictionHistory.Clear();
		_predictionCommands.Clear();
		_predictionNextCommand = 0;
		_predictionAcknowledgedCommand = 0;
		_predictionCredit = 0;
		_predictionVisualOffset = 0;
		_predictionPresentedJump = 0;
		_predictionHasPresentedJump = false;
		LogPrediction( $"reset preserve={preserveState} position={_predictionState.Position} world={WorldPosition} delta={Time.Delta:F6}" );
		if ( IsPredictionAuthority )
		{
			Transform.ClearInterpolation();
			PublishPredictionState();
		}
	}

	void StopPrediction()
	{
		if ( !_predictionActive ) return;
		_predictionActive = false;
		_predictionHasState = false;
		_predictionHistory.Clear();
		_predictionCommands.Clear();
		_predictionEpoch = Guid.Empty;
		_predictionVisualOffset = 0;
		if ( Body.IsValid() ) Body.MotionEnabled = _predictionSavedMotion;
		if ( IsPredictionAuthority && PredictionOwnsTransformAuthority )
		{
			Network.Flags &= ~NetworkFlags.HostTransformAuthority;
			PredictionOwnsTransformAuthority = false;
		}
	}

	void PredictionPrePhysicsStep()
	{
		if ( !HasPredictionPhysicsAuthority ) return;
		if ( IsPredictionAuthority && (_predictionState.Position - WorldPosition).Length > 0.1f ) ResetPredictionTimeline( false );
		// The trace motor already integrated input and gravity. Native physics runs only
		// contact response here, so movement is not integrated a second time.
		Body.Gravity = false;
		Body.LinearDamping = 0;
		// Platform carry is already integrated by the motor; native friction would add it again.
		FeetCollider.Friction = 0;
		Body.Velocity = Vector3.Zero;
		Body.AngularVelocity = Vector3.Zero;
	}

	void PredictionPostPhysicsStep()
	{
		if ( !HasPredictionPhysicsAuthority || !Body.PhysicsBody.IsValid() ) return;
		var velocity = Body.PhysicsBody.Velocity;
		var frame = _predictionPendingFrame with
		{
			CollisionDisplacement = Body.PhysicsBody.Transform.Position - _predictionState.Position,
			CollisionVelocity = velocity,
			HasCollisionVelocity = velocity.IsFinite && velocity.Length > 0.1f
		};
		_predictionState = CharacterPrediction.ApplyCollisionResponse( _predictionState, frame );
		if ( PredictionDebugLogging && (frame.CollisionDisplacement.Length > 0.01f || frame.HasCollisionVelocity) )
			LogPrediction( $"contact command={frame.Input.Command} ack={_predictionAcknowledgedCommand} displacement={frame.CollisionDisplacement} nativeVelocity={velocity} push={_predictionState.PushVelocity} position={_predictionState.Position} grounded={_predictionState.Grounded} ground={_predictionState.Ground} pending={_predictionHistory.Count}" );
		if ( !IsPredictionAuthority )
			_predictionHistory.UpdateLatest( frame.Input.Command, frame, _predictionState );
		ApplyPredictionState( true );
		if ( IsPredictionAuthority ) PublishPredictionState();
	}
	void PredictionFixedUpdate()
	{
		if ( IsPredictionAuthority && (_predictionState.Position - WorldPosition).Length > 0.1f )
			ResetPredictionTimeline( false ); // Host-side teleports invalidate pending commands.

		if ( !IsPredictionAuthority ) ConsumePredictionSnapshot();
		if ( _predictionEpoch == Guid.Empty ) return; // Wait for the host's initial state.

		if ( !IsProxy )
		{
			if ( !IsPredictionAuthority && _predictionHistory.Count == PredictionCapacity )
			{
				_predictionResetEpoch = _predictionEpoch;
				_predictionEpoch = Guid.Empty;
				_predictionHistory.Clear();
				RequestPredictionReset( _predictionResetEpoch );
				return;
			}
			var command = CapturePredictionInput();
			var frame = CapturePredictionFrame( command );
			_predictionPendingFrame = frame;
			var before = _predictionState;
			_predictionState = SimulatePredictionFrame( before, frame );
			ApplyPredictionState( true );
			if ( IsPredictionAuthority )
			{
				_predictionAcknowledgedCommand = command.Command;
				PublishPredictionState();
			}
			else
			{
				_predictionHistory.Record( command.Command, frame, _predictionState );
				SubmitPredictionInput( _predictionEpoch, _predictionHistory.GetInputBatch( PredictionBatchSize ).Select( x => x.Input ).ToArray() );
			}
			EmitPredictionEvents( before, command );
		}
		else if ( IsPredictionAuthority )
		{
			// Credit comes from host ticks, never from a client-supplied delta. A short burst
			// can catch up after jitter, but the client cannot advance faster than host time.
			_predictionCredit = MathF.Min( _predictionCredit + Time.Delta, Time.Delta * 8 );
			int steps = 0;
			while ( _predictionCredit >= Time.Delta && steps++ < 8
				&& _predictionCommands.Remove( unchecked(_predictionAcknowledgedCommand + 1), out var command ) )
			{
				var before = _predictionState;
				_predictionState = SimulatePredictionFrame( before, CapturePredictionFrame( command ) );
				_predictionCredit -= Time.Delta;
				_predictionAcknowledgedCommand = command.Command;
				ApplyPredictionState( true );
				EmitPredictionEvents( before, command );
			}
			PublishPredictionState();
		}
		else ApplyPredictionState( false );

		UpdateHeadroom();
		UpdateBody();
		UpdateEyeTransform();
	}

	CharacterPredictionInput CapturePredictionInput()
	{
		var eyes = EyeAngles;
		if ( _predictionState.Grounded && PredictionGroundTransform( _predictionState.Ground ) is { } support )
			eyes = (support.Rotation.Inverse * eyes.WithPitch( 0 ).WithRoll( 0 ).ToRotation()).Angles();
		return new CharacterPredictionInput( unchecked(++_predictionNextCommand), Input.AnalogMove, eyes,
			Input.Down( AltMoveButton ) != RunByDefault, Input.Down( "duck" ), Input.Pressed( "Jump" ), _predictionState.Grounded ? _predictionState.Ground : Guid.Empty );
	}

	[Expose, Rpc.Host( NetFlags.OwnerOnly | NetFlags.Unreliable )]
	private void SubmitPredictionInput( Guid epoch, CharacterPredictionInput[] inputs )
	{
		if ( !_predictionActive || epoch != _predictionEpoch || Rpc.Caller?.Id != _predictionOwner ) return;
		if ( inputs is null || inputs.Length > PredictionBatchSize ) return;
		foreach ( var received in inputs )
		{
			var input = received;
			var ahead = unchecked(input.Command - _predictionAcknowledgedCommand);
			if ( ahead == 0 || ahead > PredictionCapacity ) continue;
			if ( !input.Move.IsFinite || !float.IsFinite( input.Eyes.pitch ) || !float.IsFinite( input.Eyes.yaw ) || !float.IsFinite( input.Eyes.roll ) ) continue;
			input.Move = input.Move.ClampLength( 1 );
			input.Eyes = input.Eyes.WithPitch( input.Eyes.pitch.Clamp( -90, 90 ) ).WithRoll( 0 );
			_predictionCommands.TryAdd( input.Command, input );
		}
	}

	void PublishPredictionState()
	{
		PredictionSnapshot = new CharacterPredictionSnapshot( _predictionEpoch, _predictionOwner, _predictionAcknowledgedCommand,
			_predictionState, _predictionSettings, _predictionDelta );
		if ( PredictionDebugLogging )
			LogPrediction( $"publish ack={_predictionAcknowledgedCommand} position={_predictionState.Position} velocity={_predictionState.Velocity} push={_predictionState.PushVelocity} grounded={_predictionState.Grounded} ground={_predictionState.Ground} queued={_predictionCommands.Count} credit={_predictionCredit:F6} delta={_predictionDelta:F6}" );
	}

	void ConsumePredictionSnapshot()
	{
		var snapshot = PredictionSnapshot;
		if ( snapshot.Epoch == Guid.Empty || snapshot.Owner != _predictionOwner ) return;
		if ( snapshot.Epoch == _predictionResetEpoch ) return;
		if ( snapshot.Delta <= 0 ) return;
		_predictionSettings = snapshot.Settings;
		_predictionDelta = snapshot.Delta;
		if ( snapshot.Epoch != _predictionEpoch )
		{
			LogPrediction( $"snapshot-reset incomingEpoch={snapshot.Epoch} ack={snapshot.Command} position={snapshot.State.Position} delta={snapshot.Delta:F6}" );
			_predictionEpoch = snapshot.Epoch;
			_predictionResetEpoch = Guid.Empty;
			_predictionState = snapshot.State;
			_predictionAcknowledgedCommand = snapshot.Command;
			_predictionNextCommand = snapshot.Command;
			_predictionHistory.Clear();
			_predictionVisualOffset = 0;
			ApplyPredictionState( !IsProxy );
			return;
		}

		var delta = unchecked(snapshot.Command - _predictionAcknowledgedCommand);
		if ( delta == 0 || delta > 0x7FFFFFFF ) return;
		_predictionAcknowledgedCommand = snapshot.Command;
		if ( IsProxy )
		{
			_predictionState = snapshot.State;
			return;
		}

		// Normal platform carry is expected movement, not an authoritative correction.
		// Compare both states in the same displayed frame before counting or smoothing errors.
		var rawPrevious = _predictionState;
		var previous = rawPrevious;
		if ( previous.Grounded && PredictionGroundTransform( previous.Ground ) is { } previousGround )
			previous = CharacterPrediction.RebaseGround( previous, previousGround );
		var previousPosition = previous.Position;
		var authoritative = snapshot.State;
		var pendingBefore = _predictionHistory.Count;
		var replayed = 0;
		var replayContact = Vector3.Zero;
		if ( authoritative.Grounded && PredictionGroundTransform( authoritative.Ground ) is { } displayedGround )
			authoritative = CharacterPrediction.RebaseGround( authoritative, displayedGround );
		// Replay against the displayed collision world. Historical absolute platform poses
		// belong to a different timeline and must not carry a restored host state backwards.
		CharacterPredictionState Replay( CharacterPredictionState state, CharacterPredictionFrame frame )
		{
			replayed++;
			replayContact += frame.CollisionDisplacement;
			if ( frame.GroundTransform is { } recordedGround && PredictionGroundTransform( frame.Ground ) is { } displayedSupport )
			{
				var rotation = displayedSupport.Rotation * recordedGround.Rotation.Inverse;
				frame = frame with
				{
					CollisionDisplacement = rotation * frame.CollisionDisplacement,
					CollisionVelocity = rotation * frame.CollisionVelocity
				};
			}
			return CharacterPrediction.ApplyCollisionResponse(
				SimulatePredictionFrame( state, frame with { Ground = Guid.Empty, GroundTransform = null } ), frame );
		}
		if ( !_predictionHistory.Reconcile( snapshot.Command, authoritative, Replay, out _predictionState ) )
		{
			// History overflow: ask the host for a new epoch so commands still in flight
			LogPrediction( $"history-miss ack={snapshot.Command} next={_predictionNextCommand} pending={pendingBefore}" );
			// cannot be applied to a locally restarted timeline.
			_predictionHistory.Clear();
			_predictionEpoch = Guid.Empty;
			_predictionResetEpoch = snapshot.Epoch;
			RequestPredictionReset( snapshot.Epoch );
		}
		var correction = previousPosition - _predictionState.Position;
		if ( PredictionDebugLogging )
			LogPrediction( $"reconcile ack={snapshot.Command} ackAdvance={delta} next={_predictionNextCommand} pending={pendingBefore}->{_predictionHistory.Count} replayed={replayed} error={correction} magnitude={correction.Length:F4} carry={previousPosition - rawPrevious.Position} rawPrevious={rawPrevious.Position} hostPosition={snapshot.State.Position} rebasedHost={authoritative.Position} result={_predictionState.Position} previousGround={previous.Ground} hostGround={snapshot.State.Ground} resultGround={_predictionState.Ground} grounded={previous.Grounded}/{snapshot.State.Grounded}/{_predictionState.Grounded} input={_predictionPendingFrame.Input.Move} previousVelocity={rawPrevious.Velocity} hostVelocity={snapshot.State.Velocity} resultVelocity={_predictionState.Velocity} previousPush={rawPrevious.PushVelocity} hostPush={snapshot.State.PushVelocity} resultPush={_predictionState.PushVelocity} replayContact={replayContact} delta={_predictionDelta:F6}" );
		if ( correction.Length > 0.01f )
		{
			PredictionCorrections++;
			_predictionVisualOffset = correction.Length < 64 ? _predictionVisualOffset + correction : Vector3.Zero;
		}
		ApplyPredictionState( true );
		Transform.ClearInterpolation();
	}

	[Expose, Rpc.Host( NetFlags.OwnerOnly )]
	private void RequestPredictionReset( Guid epoch )
	{
		if ( _predictionActive && epoch == _predictionEpoch && Rpc.Caller?.Id == _predictionOwner ) ResetPredictionTimeline();
	}

	CharacterPredictionFrame CapturePredictionFrame( CharacterPredictionInput input )
	{
		return new CharacterPredictionFrame( input, _predictionState.Ground, PredictionGroundTransform( _predictionState.Ground ) );
	}

	CharacterPredictionState SimulatePredictionFrame( CharacterPredictionState state, CharacterPredictionFrame frame )
	{
		CharacterPredictionTrace Trace( Vector3 from, Vector3 to, bool duck )
		{
			var hit = TracePredictionBody( from, to, duck );
			if ( hit.Ground == frame.Ground && frame.GroundTransform is { } ground ) hit = hit with { GroundTransform = ground };
			return hit;
		}
		Transform? Ground( Guid id ) => id == frame.Ground ? frame.GroundTransform : PredictionGroundTransform( id );
		CharacterPredictionTrace Carry( Vector3 from, Vector3 to, bool duck )
			=> TracePredictionBody( from, to, duck, Scene.Directory.FindByGuid( state.Ground ) );
		return CharacterPrediction.Simulate( state, frame.Input, _predictionSettings, _predictionDelta, Trace, Ground, Carry );
	}

	CharacterPredictionTrace TracePredictionBody( Vector3 from, Vector3 to, bool duck, GameObject ignore = null )
	{
		var height = duck ? _predictionSettings.DuckHeight : _predictionSettings.Height;
		var radius = _predictionSettings.BodyRadius * 0.5f;
		var center = UpDirection * height * 0.5f;
		var extents = new Vector3( radius ) + UpDirection.Abs() * (height * 0.5f - radius);
		var query = Scene.Trace.Box( new BBox( center - extents, center + extents ), from, to )
			.IgnoreGameObjectHierarchy( GameObject ).WithCollisionRules( Tags );
		if ( ignore.IsValid() ) query = query.IgnoreGameObjectHierarchy( ignore );
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
		if ( position ) WorldPosition = _predictionState.Position;
		Velocity = _predictionState.Velocity + _predictionState.PushVelocity;
		GroundObject = _predictionState.Grounded ? Scene.Directory.FindByGuid( _predictionState.Ground ) : null;
		GroundComponent = GroundObject?.GetComponent<Collider>();
		GroundVelocity = 0;
		TimeSinceGrounded = _predictionState.GroundedAgo;
		if ( GroundObject.IsValid() )
		{
			// Surface information is presentation state and is refreshed after replay.
			var tr = TraceBody( WorldPosition + UpDirection * 0.1f, WorldPosition - UpDirection * 2 );
			GroundSurface = tr.Surface;
			GroundIsDynamic = GroundComponent is Collider collider && collider.IsDynamic;
		}
	}

	void EmitPredictionEvents( CharacterPredictionState before, CharacterPredictionInput input )
	{
		if ( _predictionState.JumpCommand == input.Command && _predictionState.JumpCooldown > before.JumpCooldown )
		{
			_predictionPresentedJump = input.Command;
			_predictionHasPresentedJump = true;
			if ( UseAnimatorControls && Renderer.IsValid() ) Renderer.Set( "b_jump", true );
			if ( IsPredictionAuthority ) IEvents.PostToGameObject( GameObject, x => x.OnJumped() );
		}
		if ( !before.Grounded && _predictionState.Grounded && before.FallDistance > 1 )
		{
			if ( IsPredictionAuthority ) IEvents.PostToGameObject( GameObject, x => x.OnLanded( before.FallDistance, before.Velocity ) );
			if ( !IsProxy && EnableFootstepSounds )
			{
				PlayFootstepSound( WorldPosition, 1, 0 );
				PlayFootstepSound( WorldPosition, 1, 1 );
			}
		}
	}

	void PresentPredictionJump()
	{
		if ( !_predictionActive || !IsProxy || _predictionState.JumpCooldown <= 0
			|| (_predictionHasPresentedJump && _predictionPresentedJump == _predictionState.JumpCommand) ) return;
		_predictionPresentedJump = _predictionState.JumpCommand;
		_predictionHasPresentedJump = true;
		if ( UseAnimatorControls && Renderer.IsValid() ) Renderer.Set( "b_jump", true );
	}
}
