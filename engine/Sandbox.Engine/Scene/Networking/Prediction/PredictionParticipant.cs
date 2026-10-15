namespace Sandbox;

internal interface IPredictionParticipant
{
	void UpdatePredictionRegistration();
}

/// <summary>
/// Opt in to the root object's ordered owner/host prediction timeline. Inputs and states must be
/// complete value snapshots without shared mutable references. Native physics is not replayed.
/// </summary>
public abstract class PredictionParticipant<TInput, TState> : Component, IPredictionParticipant
	where TInput : struct where TState : struct
{
	ParticipantData data;

	/// <summary>
	/// The prediction timeline shared by this network root.
	/// </summary>
	public Prediction Prediction { get; private set; }

	/// <summary>
	/// The simulation order among participants whose dependencies are satisfied.
	/// </summary>
	public virtual int PredictionOrder => 0;

	/// <summary>
	/// Participants that must simulate before this participant.
	/// </summary>
	public virtual IEnumerable<Component> PredictionDependencies => Array.Empty<Component>();

	/// <summary>
	/// Sample owner input once per fixed command.
	/// </summary>
	protected abstract TInput CaptureInput();

	/// <summary>
	/// Capture all replayable state without references to mutable simulation objects.
	/// </summary>
	protected abstract TState CaptureState();

	/// <summary>
	/// Restore replayable state. Remote proxies must preserve their interpolated root transform.
	/// </summary>
	protected abstract void RestoreState( in TState state );

	/// <summary>
	/// Advance declared state only. Replay repeats this method; do not emit gameplay effects.
	/// </summary>
	protected abstract void Simulate( in TInput input, in PredictionContext context );

	/// <summary>
	/// Validate and sanitize owner input on the host before simulation. Returning false replaces
	/// this participant's input with its default value; the command still advances the timeline.
	/// </summary>
	protected virtual bool ValidateInput( ref TInput input ) => true;

	/// <summary>
	/// First-pass effects only; gate authoritative gameplay with context.IsAuthority. Never called during replay.
	/// </summary>
	protected virtual void AfterSimulate( in TInput input, in PredictionContext context )
	{
	}

	/// <summary>
	/// Update presentation after the entire root has reconciled; collision state is already corrected.
	/// </summary>
	protected virtual void OnCorrected( in TState previous, in TState corrected )
	{
	}

	/// <summary>
	/// Called when the prediction timeline resets.
	/// </summary>
	protected virtual void OnPredictionReset()
	{
	}

	/// <summary>
	/// Called when the authority confirms or rejects an event.
	/// </summary>
	protected virtual void OnEventConfirmed( PredictionEventId id, bool accepted )
	{
	}

	/// <summary>
	/// Reliable owner result for an action from the current authoritative first pass.
	/// </summary>
	protected void ConfirmEvent( uint action, bool accepted )
	{
		if ( data is null || !data.Context.IsAuthority || Prediction is null )
		{
			return;
		}

		Prediction.ConfirmEvent( this, data.Context.Event( action ), accepted );
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();

		if ( Scene.IsEditor )
		{
			return;
		}

		UpdatePredictionRegistration();
	}

	void IPredictionParticipant.UpdatePredictionRegistration() => UpdatePredictionRegistration();

	void UpdatePredictionRegistration()
	{
		data ??= new ParticipantData( this );
		var runner = global::Sandbox.Prediction.GetOrCreate( this );

		if ( Prediction == runner )
		{
			return;
		}

		Prediction?.Unregister( data );
		runner?.Register( data );
	}

	protected override void OnDisabled()
	{
		Prediction?.Unregister( data );
		base.OnDisabled();
	}

	sealed class ParticipantData( PredictionParticipant<TInput, TState> owner ) : PredictionParticipantData<TInput, TState>
	{
		internal override Component Component => owner;

		internal override int Order => owner.PredictionOrder;

		internal override IEnumerable<Component> Dependencies => owner.PredictionDependencies;

		internal override void RunnerChanged( Prediction runner ) => owner.Prediction = runner;

		protected override TInput CaptureInput() => owner.CaptureInput();

		protected override TState CaptureState() => owner.CaptureState();

		protected override void RestoreState( in TState state ) => owner.RestoreState( state );

		protected override void Simulate( in TInput input, in PredictionContext context ) => owner.Simulate( input, context );

		protected override bool ValidateInput( ref TInput input ) => owner.ValidateInput( ref input );

		protected override void AfterSimulate( in TInput input, in PredictionContext context ) => owner.AfterSimulate( input, context );

		protected override void OnCorrected( in TState previous, in TState corrected ) => owner.OnCorrected( previous, corrected );

		internal override void EventConfirmed( PredictionEventId id, bool accepted ) => owner.OnEventConfirmed( id, accepted );

		internal override void Reset()
		{
			base.Reset();
			owner.OnPredictionReset();
		}
	}
}

internal abstract class PredictionParticipantData
{
	internal PredictionContext Context { get; private set; }

	internal void SetContext( in PredictionContext context ) => Context = context;

	internal virtual void EventConfirmed( PredictionEventId id, bool accepted )
	{
	}

	internal virtual void RunnerChanged( Prediction runner )
	{
	}

	internal abstract Component Component { get; }

	internal abstract int Order { get; }

	internal virtual IEnumerable<Component> Dependencies => Array.Empty<Component>();

	internal abstract void Capture( uint command );

	internal abstract void WriteInput( uint command, ref ByteStream stream );

	internal abstract bool ReadInput( uint command, ref ByteStream stream );

	internal abstract void WriteState( ref ByteStream stream );

	internal abstract void ReadState( ref ByteStream stream );

	internal abstract void Restore();

	internal abstract void Execute( in PredictionContext context );

	internal virtual void AfterExecute( in PredictionContext context )
	{
	}

	internal abstract void Acknowledge( uint command );

	internal abstract void BeginCorrection();

	internal abstract void EndCorrection();

	internal virtual void FinalizeSimulation( in PredictionContext context )
	{
	}

	internal abstract void Reset();
}

internal abstract class PredictionParticipantData<TInput, TState> : PredictionParticipantData
	where TInput : struct where TState : struct
{
	readonly PredictionHistory<TInput> history = new( Prediction.Capacity );
	TInput[] authorityInputs;
	TState incoming;
	TState previous;

	protected abstract TInput CaptureInput();

	protected abstract TState CaptureState();

	protected abstract void RestoreState( in TState state );

	protected abstract void Simulate( in TInput input, in PredictionContext context );

	protected virtual bool ValidateInput( ref TInput input ) => true;

	protected virtual void AfterSimulate( in TInput input, in PredictionContext context )
	{
	}

	protected virtual void OnCorrected( in TState before, in TState corrected )
	{
	}

	internal override void Capture( uint command ) => history.Record( command, CaptureInput() );

	internal override void WriteInput( uint command, ref ByteStream stream )
	{
		if ( !history.TryGetInput( command, out var input ) )
		{
			throw new InvalidOperationException( "Prediction input is outside its history." );
		}

		Game.TypeLibrary.ToBytes( input, ref stream );
	}

	internal override bool ReadInput( uint command, ref ByteStream stream )
	{
		var input = Game.TypeLibrary.FromBytes<TInput>( ref stream );

		if ( !ValidateInput( ref input ) )
		{
			input = default;
		}

		authorityInputs ??= new TInput[Prediction.Capacity];
		authorityInputs[command % Prediction.Capacity] = input;

		return true;
	}

	internal override void WriteState( ref ByteStream stream ) => Game.TypeLibrary.ToBytes( CaptureState(), ref stream );

	internal override void ReadState( ref ByteStream stream ) => incoming = Game.TypeLibrary.FromBytes<TState>( ref stream );

	internal override void Restore() => RestoreState( incoming );

	internal override void Execute( in PredictionContext context )
	{
		var input = InputFor( context );
		Simulate( input, context );
	}

	internal override void AfterExecute( in PredictionContext context )
	{
		if ( context.IsReplay )
		{
			return;
		}

		var input = InputFor( context );
		AfterSimulate( input, context );
	}

	TInput InputFor( in PredictionContext context )
	{
		if ( history.TryGetInput( context.Command, out var input ) )
		{
			return input;
		}

		if ( context.IsAuthority && authorityInputs is not null )
		{
			return authorityInputs[context.Command % Prediction.Capacity];
		}

		throw new InvalidOperationException( "Prediction command is outside its participant history." );
	}

	internal override void Acknowledge( uint command ) => history.DiscardThrough( command );

	internal override void BeginCorrection() => previous = CaptureState();

	internal override void EndCorrection()
	{
		var corrected = CaptureState();
		OnCorrected( previous, corrected );
	}

	internal override void Reset()
	{
		history.Clear();

		if ( authorityInputs is not null )
		{
			Array.Clear( authorityInputs );
		}
	}
}
