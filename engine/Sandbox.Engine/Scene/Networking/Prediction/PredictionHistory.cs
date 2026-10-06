namespace Sandbox;

/// <summary>
/// Bounded history of inputs and the states produced by them. States must be value snapshots,
/// not references to mutable simulation objects. Command numbers use unsigned wraparound.
/// </summary>
internal sealed class PredictionHistory<TInput, TState>
{
	internal readonly record struct Entry( uint CommandNumber, TInput Input, TState State );

	readonly Entry[] entries;
	int start;
	public int Count { get; private set; }
	uint lastCommand;
	bool hasLastCommand;

	public PredictionHistory( int capacity )
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( capacity );
		entries = new Entry[capacity];
	}

	/// <summary>
	/// Records the result of a new command, evicting the oldest entry when full.
	/// Duplicate and out-of-order commands are rejected without changing history.
	/// </summary>
	public bool Record( uint commandNumber, TInput input, TState state )
	{
		var delta = unchecked(commandNumber - lastCommand);
		if ( hasLastCommand && (delta == 0 || delta > 0x7FFFFFFF) )
			return false;

		if ( Count == entries.Length )
		{
			entries[start] = default;
			start = (start + 1) % entries.Length;
			Count--;
		}

		entries[(start + Count) % entries.Length] = new Entry( commandNumber, input, state );
		Count++;
		lastCommand = commandNumber;
		hasLastCommand = true;
		return true;
	}

	/// <summary>
	/// Completes the latest tick after native contact response. It cannot resurrect an
	/// acknowledged entry or replace an older tick with a newer physics result.
	/// </summary>
	public bool UpdateLatest( uint commandNumber, TInput input, TState state )
	{
		if ( Count == 0 ) return false;
		var index = (start + Count - 1) % entries.Length;
		if ( entries[index].CommandNumber != commandNumber ) return false;
		entries[index] = new Entry( commandNumber, input, state );
		return true;
	}

	/// <summary>
	/// Copies a bounded input batch containing the oldest pending commands for gap recovery
	/// and the newest commands for low-latency delivery. Repeated batches tolerate packet loss.
	/// </summary>
	public TInput[] GetInputBatch( int maximum )
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( maximum );
		var count = Math.Min( Count, maximum );
		var result = new TInput[count];
		var oldest = Count <= maximum ? count : (count + 1) / 2;
		for ( int i = 0; i < oldest; i++ )
			result[i] = entries[(start + i) % entries.Length].Input;
		for ( int i = oldest; i < count; i++ )
			result[i] = entries[(start + Count - count + i) % entries.Length].Input;
		return result;
	}

	/// <summary>
	/// Gets the predicted state at an acknowledged command for error comparison.
	/// </summary>
	public bool TryGetState( uint commandNumber, out TState state )
	{
		for ( int i = 0; i < Count; i++ )
		{
			var entry = entries[(start + i) % entries.Length];
			if ( entry.CommandNumber != commandNumber ) continue;
			state = entry.State;
			return true;
		}

		state = default;
		return false;
	}

	/// <summary>
	/// Restores an authoritative state and resimulates subsequent commands in order.
	/// Returns false without modifying history if the acknowledged command is absent;
	/// the caller must then perform a full resynchronization instead of a partial replay.
	/// The simulation callback must not mutate this history or emit replayed side effects.
	/// </summary>
	public bool Reconcile( uint acknowledgedCommand, TState authoritativeState,
		Func<TState, TInput, TState> simulate, out TState state )
	{
		ArgumentNullException.ThrowIfNull( simulate );
		state = authoritativeState;
		int acknowledgedIndex = -1;
		for ( int i = 0; i < Count; i++ )
		{
			if ( entries[(start + i) % entries.Length].CommandNumber != acknowledgedCommand )
				continue;

			acknowledgedIndex = i;
			break;
		}

		if ( acknowledgedIndex < 0 )
			return false;

		for ( int i = 0; i <= acknowledgedIndex; i++ )
			entries[(start + i) % entries.Length] = default;

		start = (start + acknowledgedIndex + 1) % entries.Length;
		Count -= acknowledgedIndex + 1;

		for ( int i = 0; i < Count; i++ )
		{
			var index = (start + i) % entries.Length;
			var entry = entries[index];
			state = simulate( state, entry.Input );
			entries[index] = entry with { State = state };
		}

		return true;
	}

	/// <summary>
	/// Clears history when changing scene, authority, or simulation timeline.
	/// </summary>
	public void Clear()
	{
		Array.Clear( entries );
		start = 0;
		Count = 0;
		lastCommand = 0;
		hasLastCommand = false;
	}
}
