namespace Sandbox;

/// <summary>
/// Bounded wrap-safe gap recovery and host-time execution credit.
/// </summary>
internal sealed class PredictionCommandQueue<T>
{
	readonly T[] values;
	readonly uint[] commands;
	readonly bool[] occupied;
	uint acknowledged;
	double credit;

	public PredictionCommandQueue( int capacity )
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( capacity );

		if ( (capacity & (capacity - 1)) != 0 )
		{
			throw new ArgumentOutOfRangeException( nameof( capacity ), "Capacity must be a power of two for wrap-safe slots." );
		}

		values = new T[capacity];
		commands = new uint[capacity];
		occupied = new bool[capacity];
	}

	public void Reset( uint command = 0 )
	{
		Array.Clear( values );
		Array.Clear( occupied );
		acknowledged = command;
		credit = 0;
	}

	public bool CanAccept( uint command )
	{
		var ahead = unchecked(command - acknowledged);

		return ahead > 0 && ahead <= values.Length && !occupied[command % (uint)values.Length];
	}

	public bool Enqueue( uint command, T value )
	{
		if ( !CanAccept( command ) )
		{
			return false;
		}

		var index = command % (uint)values.Length;
		values[index] = value;
		commands[index] = command;
		occupied[index] = true;

		return true;
	}

	public void AddCredit( float delta ) => credit = Math.Min( credit + delta, delta * 8.0 );

	public bool TryTake( float delta, out uint command, out T value )
	{
		command = unchecked(acknowledged + 1);
		var index = command % (uint)values.Length;
		value = default;

		if ( credit < delta || !occupied[index] || commands[index] != command )
		{
			return false;
		}

		value = values[index];
		values[index] = default;
		occupied[index] = false;
		acknowledged = command;
		credit -= delta;

		return true;
	}
}
