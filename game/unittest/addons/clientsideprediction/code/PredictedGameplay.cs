using Sandbox;
using System;
using System.Collections.Generic;

/// <summary>
/// Owner input for the marking beam.
/// </summary>
public record struct ProbeInput( Angles Aim, bool Activate, bool Recharge );

/// <summary>
/// Replayable marking beam state.
/// </summary>
public record struct ProbeState
{
	/// <summary>
	/// Remaining beam charges.
	/// </summary>
	public int Charges { get; set; }

	/// <summary>
	/// Remaining time before the next activation.
	/// </summary>
	public float Cooldown { get; set; }

	/// <summary>
	/// Command that most recently activated the beam.
	/// </summary>
	public uint ActionCommand { get; set; }

	/// <summary>
	/// Whether the current activation was accepted.
	/// </summary>
	public bool Accepted { get; set; }

	/// <summary>
	/// Target selected by the authoritative trace.
	/// </summary>
	public Guid Target { get; set; }
}

/// <summary>
/// A resource-limited marking beam sharing movement's root timeline.
/// Only the host's historical trace can mark a target.
/// </summary>
public sealed class PredictionProbe : PredictionParticipant<ProbeInput, ProbeState>
{
	ProbeState state = new() { Charges = 5 };
	readonly PredictionEvents events = new();
	Guid presentedEpoch;
	PlayerController controller;
	TimeSince sinceFlash = 10;

	/// <summary>
	/// Remaining beam charges.
	/// </summary>
	public int Charges => state.Charges;

	/// <summary>
	/// Seconds until the beam can fire again.
	/// </summary>
	public float Cooldown => state.Cooldown;

	/// <summary>
	/// Number of beam flashes presented locally.
	/// </summary>
	public int Presented { get; private set; }

	/// <summary>
	/// Number of actions accepted by the host.
	/// </summary>
	public int Confirmed { get; private set; }

	/// <summary>
	/// Number of actions rejected by the host.
	/// </summary>
	public int Rejected { get; private set; }

	public override int PredictionOrder => 100;

	public override IEnumerable<Component> PredictionDependencies => new[] { Controller };

	PlayerController Controller => controller ??= GetComponent<PlayerController>();

	protected override ProbeInput CaptureInput() => new( Controller.EyeAngles,
		Controller.UseLookControls && Input.Pressed( "Attack1" ), Input.Pressed( "Reload" ) );

	protected override ProbeState CaptureState() => state;

	protected override void RestoreState( in ProbeState value ) => state = value;

	protected override bool ValidateInput( ref ProbeInput input )
	{
		if ( !float.IsFinite( input.Aim.pitch ) || !float.IsFinite( input.Aim.yaw ) || !float.IsFinite( input.Aim.roll ) )
			return false;

		input.Aim = input.Aim.WithPitch( input.Aim.pitch.Clamp( -90, 90 ) ).WithRoll( 0 );

		return true;
	}

	protected override void Simulate( in ProbeInput input, in PredictionContext context )
	{
		state.Cooldown = MathF.Max( 0, state.Cooldown - context.Delta );
		state.Accepted = false;
		state.Target = Guid.Empty;

		if ( input.Recharge && state.Cooldown == 0 )
			state.Charges = 5;

		if ( !input.Activate )
			return;

		state.ActionCommand = context.Command;

		if ( state.Charges == 0 || state.Cooldown > 0 )
			return;

		if ( context.IsAuthority )
		{
			// The origin comes from authoritative movement, never a client-supplied position.
			var from = Controller.WorldPosition + Vector3.Up * (Controller.CurrentHeight - Controller.EyeDistanceFromTop);
			var aim = input.Aim;
			var result = Scene.GetSystem<LagCompensationSystem>().Trace( context.CommandTime,
				Scene.Trace.Ray( from, from + aim.Forward * 2000 ).UseHitboxes().IgnoreGameObjectHierarchy( GameObject ) );
			if ( !result.Success )
				return;

			if ( result.Trace.Hit && result.Trace.GameObject.IsValid()
				&& result.Trace.GameObject.GetComponent<PredictionMarkTarget>().IsValid() )
			{
				state.Target = result.Trace.GameObject.Id;
			}
		}

		state.Charges--;
		state.Cooldown = 0.3f;
		state.Accepted = true;
	}

	protected override void AfterSimulate( in ProbeInput input, in PredictionContext context )
	{
		EnsureEvents( context.Epoch );

		if ( !input.Activate )
			return;

		if ( context.IsAuthority )
		{
			if ( state.Accepted && state.Target != Guid.Empty )
			{
				Scene.Directory.FindByGuid( state.Target )?.GetComponent<PredictionMarkTarget>()?.Mark( context.Event( 0 ) );
			}

			ConfirmEvent( 0, state.Accepted );
		}
		else if ( state.Accepted && events.Predict( context.Event( 0 ) ) )
		{
			Flash();
		}
	}

	protected override void OnEventConfirmed( PredictionEventId id, bool accepted )
	{
		EnsureEvents( id.Epoch );
		var result = events.Confirm( id, accepted );

		if ( result == PredictionEventConfirmation.Ignored )
			return;

		if ( accepted )
		{
			Confirmed++;
		}
		else
		{
			Rejected++;
		}

		if ( result == PredictionEventConfirmation.Accepted )
			Flash();
	}

	void EnsureEvents( Guid epoch )
	{
		if ( presentedEpoch == epoch )
			return;

		presentedEpoch = epoch;
		events.Reset( GameObject.Id, epoch );
	}

	void Flash()
	{
		Presented++;
		sinceFlash = 0;
	}

	protected override void OnUpdate()
	{
		if ( IsProxy || !Controller.IsValid() )
			return;

		Scene.DebugOverlay.ScreenText( new Vector2( 24, 100 ),
			$"Marking beam: {Charges}/5 | cooldown {Cooldown:F2}\nPresented {Presented} | confirmed {Confirmed} | rejected {Rejected}\nLMB mark / R recharge / RMB turn predicted panel", 18, TextFlag.LeftTop );
		Scene.DebugOverlay.ScreenText( new Vector2( Screen.Width / 2, Screen.Height / 2 ),
			sinceFlash < 0.12f ? "*" : "+", 24, TextFlag.Center );
	}
}

/// <summary>
/// A host-side action result, replicated independently of provisional owner feedback.
/// </summary>
public sealed class PredictionMarkTarget : Component
{
	/// <summary>
	/// Number of authoritative marks received.
	/// </summary>
	[Sync( SyncFlags.FromHost )] public int Marks { get; private set; }

	/// <summary>
	/// Identity of the most recent authoritative mark.
	/// </summary>
	[Sync( SyncFlags.FromHost )] public PredictionEventId LastMark { get; private set; }

	Vector3 origin;
	ModelRenderer renderer;

	protected override void OnStart()
	{
		origin = WorldPosition;
		renderer = GetComponentInChildren<ModelRenderer>();
	}

	/// <summary>
	/// Records a mark on the host.
	/// </summary>
	public void Mark( PredictionEventId id )
	{
		if ( Networking.IsActive && !Networking.IsHost )
			return;

		Marks++;
		LastMark = id;
	}

	protected override void OnFixedUpdate()
	{
		if ( Networking.IsActive && !Networking.IsHost )
			return;

		WorldPosition = origin + Vector3.Right * MathF.Sin( Time.Now * 1.4f ) * 110;
	}

	protected override void OnUpdate()
	{
		if ( renderer.IsValid() )
			renderer.Tint = Marks % 2 == 0 ? Color.Cyan : Color.Yellow;

		Scene.DebugOverlay.Text( WorldPosition + Vector3.Up * 50, $"Host marks: {Marks}", color: Color.White );
	}
}

/// <summary>
/// Owner input for the predicted panel.
/// </summary>
public record struct DialInput( bool Turn );

/// <summary>
/// Replayable panel angle.
/// </summary>
public record struct DialState( float Angle );

/// <summary>
/// A replayable scripted panel without native-world rollback.
/// </summary>
public sealed class PredictionDial : PredictionParticipant<DialInput, DialState>
{
	DialState state;
	float visualAngle;
	ModelRenderer renderer;

	/// <summary>
	/// The simulated panel angle in degrees.
	/// </summary>
	public float Angle => state.Angle;

	protected override void OnStart() => renderer = GetComponentInChildren<ModelRenderer>();

	protected override DialInput CaptureInput() => new( Input.Down( "Attack2" ) );

	protected override DialState CaptureState() => state;

	protected override void RestoreState( in DialState value )
	{
		state = value;

		if ( !Networking.IsActive || Networking.IsHost || !IsProxy )
			WorldRotation = Rotation.FromYaw( state.Angle );
	}

	protected override void Simulate( in DialInput input, in PredictionContext context )
	{
		state.Angle = (state.Angle + (input.Turn ? 90 : -90) * context.Delta).Clamp( 0, 90 );
		WorldRotation = Rotation.FromYaw( state.Angle );
	}

	protected override void OnCorrected( in DialState previous, in DialState corrected )
	{
		// Collision changes immediately; only the renderer is smoothed.
		if ( !IsProxy )
			visualAngle += previous.Angle - corrected.Angle;
	}

	protected override void OnPredictionReset() => visualAngle = 0;

	protected override void OnUpdate()
	{
		visualAngle = visualAngle.LerpTo( 0, Time.Delta * 15 );

		if ( renderer.IsValid() )
			renderer.GameObject.LocalRotation = Rotation.FromYaw( visualAngle );

		Scene.DebugOverlay.Text( WorldPosition + Vector3.Up * 50, $"Predicted panel: {Angle:F1}°", color: Color.White );
	}
}
