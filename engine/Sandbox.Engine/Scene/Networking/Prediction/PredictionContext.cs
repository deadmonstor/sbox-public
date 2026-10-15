namespace Sandbox;

/// <summary>
/// The phase of explicitly replayable value-state simulation.
/// </summary>
[Expose]
public enum PredictionPhase
{
	/// <summary>
	/// First-pass owner simulation.
	/// </summary>
	Predict,

	/// <summary>
	/// Re-simulation of unacknowledged commands after correction.
	/// </summary>
	Replay,

	/// <summary>
	/// Host-authoritative simulation.
	/// </summary>
	Authority
}

/// <summary>
/// A stable identity for an action or presentation event, including its object lifetime.
/// </summary>
[Expose]
public readonly record struct PredictionEventId( Guid Object, Guid Epoch, uint Command, uint Action );

internal interface IPredictionTimeline
{
	Scene Scene { get; }

	Guid Epoch { get; }

	uint AcknowledgedCommand { get; }

	uint ExecutingCommand { get; }
}

/// <summary>
/// Host-issued command time. This is not a client-selected rewind timestamp. Invalid after a
/// timeline reset; historical queries additionally reject times outside the host's rewind window.
/// </summary>
public readonly struct PredictionCommandTime
{
	internal readonly IPredictionTimeline Timeline;

	/// <summary>
	/// The authority epoch that issued this time.
	/// </summary>
	public Guid Epoch { get; }

	/// <summary>
	/// The executed command number.
	/// </summary>
	public uint Command { get; }

	/// <summary>
	/// The host-established simulation time.
	/// </summary>
	public double SimulationTime { get; }

	/// <summary>
	/// The interpolation delay applied to historical queries.
	/// </summary>
	public float InterpolationDelay { get; }

	/// <summary>
	/// The displayed world time for this command.
	/// </summary>
	public double DisplayTime => SimulationTime - InterpolationDelay;

	internal PredictionCommandTime( IPredictionTimeline timeline, uint command, double time, float interpolationDelay )
	{
		Timeline = timeline;
		Epoch = timeline.Epoch;
		Command = command;
		SimulationTime = time;
		InterpolationDelay = interpolationDelay;
	}

	internal bool IsValidFor( Scene scene ) => Timeline is not null && Timeline.Scene == scene
		&& Epoch != Guid.Empty && Timeline.Epoch == Epoch
		&& (Command == Timeline.ExecutingCommand || unchecked(Timeline.AcknowledgedCommand - Command) <= 0x7FFFFFFF);
}

/// <summary>
/// Explicit simulation context. Simulate only declared value state; perform gameplay and
/// presentation effects after simulation, never during replay. Random samples are stateless.
/// </summary>
public readonly struct PredictionContext
{
	/// <summary>
	/// The network root being simulated.
	/// </summary>
	public Guid ObjectId { get; }

	/// <summary>
	/// The current authority epoch.
	/// </summary>
	public Guid Epoch { get; }

	/// <summary>
	/// The command being simulated.
	/// </summary>
	public uint Command { get; }

	/// <summary>
	/// The fixed simulation step in seconds.
	/// </summary>
	public float Delta { get; }

	/// <summary>
	/// The current simulation phase.
	/// </summary>
	public PredictionPhase Phase { get; }

	/// <summary>
	/// The host-established random seed.
	/// </summary>
	public uint Seed { get; }

	/// <summary>
	/// The host-issued time for an authoritative command.
	/// </summary>
	public PredictionCommandTime CommandTime { get; }

	/// <summary>
	/// Whether this is authoritative simulation.
	/// </summary>
	public bool IsAuthority => Phase == PredictionPhase.Authority;

	/// <summary>
	/// Whether this command is being replayed.
	/// </summary>
	public bool IsReplay => Phase == PredictionPhase.Replay;

	internal PredictionContext( Guid objectId, Guid epoch, uint command, float delta, PredictionPhase phase, uint seed,
		PredictionCommandTime commandTime = default )
	{
		ObjectId = objectId;
		Epoch = epoch;
		Command = command;
		Delta = delta;
		Phase = phase;
		Seed = seed;
		CommandTime = commandTime;
	}

	/// <summary>
	/// Creates an event identity for an action in this command.
	/// </summary>
	public PredictionEventId Event( uint action ) => new( ObjectId, Epoch, Command, action );

	/// <summary>
	/// Deterministic uniform sample in [0,1), keyed by the host-established seed and command.
	/// </summary>
	public float Random( uint action, uint sample = 0 )
	{
		uint x = Seed ^ (Command * 0x9E3779B9u) ^ (action * 0x85EBCA6Bu) ^ (sample * 0xC2B2AE35u);
		x ^= x >> 16;
		x *= 0x7FEB352Du;
		x ^= x >> 15;
		x *= 0x846CA68Bu;
		x ^= x >> 16;

		return (x >> 8) * (1.0f / 16777216.0f);
	}
}

/// <summary>
/// Host-established, fixed-frequency sequence-to-time mapping for an authority epoch.
/// </summary>
internal sealed class PredictionClock
{
	double lastTime;
	uint lastCommand;
	float delta;
	bool initialized;

	public void Reset( float step )
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( step );

		if ( !float.IsFinite( step ) )
		{
			throw new ArgumentOutOfRangeException( nameof( step ) );
		}

		delta = step;
		initialized = false;
	}

	/// <summary>
	/// Anchors ordered commands to host-observed one-way delay.
	/// </summary>
	public double Execute( uint command, double hostNow, float oneWayDelay, float maximumRewind )
	{
		var observed = hostNow - Math.Clamp( float.IsFinite( oneWayDelay ) ? oneWayDelay : 0, 0, maximumRewind );

		if ( !initialized )
		{
			lastCommand = command;
			lastTime = observed;
			initialized = true;
		}

		var elapsed = unchecked(command - lastCommand);
		var time = lastTime + elapsed * (double)delta;
		var tolerance = Math.Min( 0.2, maximumRewind * 0.25 );

		if ( time < observed - tolerance )
		{
			time = observed;
		}

		lastCommand = command;
		lastTime = Math.Min( time, hostNow );

		return lastTime;
	}
}
