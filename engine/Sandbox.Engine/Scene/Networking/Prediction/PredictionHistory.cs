namespace Sandbox;

/// <summary>
/// Bounded input history for replay, using wrap-safe command numbers.
/// </summary>
internal sealed class PredictionHistory<TInput>
{
	readonly record struct Entry( uint CommandNumber, TInput Input );

	readonly Entry[] entries;
	int start;
	uint lastCommand;
	bool hasLastCommand;

	public int Count { get; private set; }

	public PredictionHistory( int capacity )
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( capacity );
		entries = new Entry[capacity];
	}

	/// <summary>
	/// Record a new command, evicting the oldest input when full. Reject duplicate or old commands.
	/// </summary>
	public bool Record( uint commandNumber, TInput input )
	{
		var delta = unchecked(commandNumber - lastCommand);

		if ( hasLastCommand && (delta == 0 || delta > 0x7FFFFFFF) )
		{
			return false;
		}

		if ( Count == entries.Length )
		{
			entries[start] = default;
			start = (start + 1) % entries.Length;
			Count--;
		}

		entries[(start + Count) % entries.Length] = new Entry( commandNumber, input );
		Count++;
		lastCommand = commandNumber;
		hasLastCommand = true;

		return true;
	}

	int FindEntry( uint commandNumber )
	{
		// Dense command sequences permit a direct lookup; sparse histories use the scan.
		var behind = unchecked(lastCommand - commandNumber);

		if ( behind < Count )
		{
			var index = (start + Count - 1 - (int)behind) % entries.Length;

			if ( entries[index].CommandNumber == commandNumber )
			{
				return index;
			}
		}

		for ( var i = 0; i < Count; i++ )
		{
			var index = (start + i) % entries.Length;

			if ( entries[index].CommandNumber == commandNumber )
			{
				return index;
			}
		}

		return -1;
	}

	public bool TryGetInput( uint commandNumber, out TInput input )
	{
		var index = FindEntry( commandNumber );
		input = index >= 0 ? entries[index].Input : default;

		return index >= 0;
	}

	/// <summary>
	/// Discard inputs through a coherent root acknowledgement.
	/// </summary>
	public bool DiscardThrough( uint commandNumber )
	{
		var index = FindEntry( commandNumber );

		if ( index < 0 )
		{
			return false;
		}

		var count = (index - start + entries.Length) % entries.Length + 1;

		for ( var i = 0; i < count; i++ )
		{
			entries[(start + i) % entries.Length] = default;
		}

		start = (start + count) % entries.Length;
		Count -= count;

		return true;
	}

	/// <summary>
	/// Clear input history when changing scene, authority, or simulation timeline.
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
