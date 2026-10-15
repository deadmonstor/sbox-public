namespace Sandbox;

/// <summary>
/// How an authoritative event result relates to local provisional presentation.
/// </summary>
public enum PredictionEventConfirmation
{
	/// <summary>This accepted event was not presented locally. Present it once.</summary>
	Accepted,

	/// <summary>The accepted event was already presented provisionally. Do not present it again.</summary>
	AlreadyPredicted,

	/// <summary>The authority rejected the event. Cancel any provisional presentation.</summary>
	Rejected,

	/// <summary>This identity was already confirmed, expired, or belongs to another lifetime.</summary>
	Ignored
}

/// <summary>
/// Bounded per-object presentation ledger. Reset with the host's object/epoch before use.
/// Replay never calls presentation. When full, evicted commands become permanently expired
/// for this epoch, so arbitrarily delayed confirmations cannot replay old effects.
/// </summary>
public sealed class PredictionEvents
{
	struct Entry
	{
		public PredictionEventId Id;
		public bool Predicted;
		public bool Confirmed;
	}

	readonly Entry[] entries;
	Guid objectId;
	Guid epoch;
	int start;
	int count;
	uint expiredThrough;
	bool hasExpired;

	/// <summary>
	/// Creates a presentation ledger with the specified event capacity.
	/// </summary>
	public PredictionEvents( int capacity = 128 )
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( capacity );
		entries = new Entry[capacity];
	}

	/// <summary>
	/// Clears recorded events and starts the specified object lifetime.
	/// </summary>
	public void Reset( Guid objectId, Guid epoch )
	{
		this.objectId = objectId;
		this.epoch = epoch;
		Array.Clear( entries );
		start = count = 0;
		hasExpired = false;
	}

	/// <summary>
	/// Returns true only for the first provisional presentation of this event.
	/// </summary>
	public bool Predict( PredictionEventId id )
	{
		if ( !CanAccept( id ) || Find( id ) >= 0 )
		{
			return false;
		}

		Add( new Entry { Id = id, Predicted = true } );

		return true;
	}

	/// <summary>
	/// Records a host result without repeating a previously predicted effect.
	/// </summary>
	public PredictionEventConfirmation Confirm( PredictionEventId id, bool accepted )
	{
		if ( !CanAccept( id ) )
		{
			return PredictionEventConfirmation.Ignored;
		}

		var index = Find( id );

		if ( index < 0 )
		{
			Add( new Entry { Id = id, Confirmed = true } );
			return accepted ? PredictionEventConfirmation.Accepted : PredictionEventConfirmation.Rejected;
		}

		ref var entry = ref entries[index];

		if ( entry.Confirmed )
		{
			return PredictionEventConfirmation.Ignored;
		}

		entry.Confirmed = true;

		return !accepted ? PredictionEventConfirmation.Rejected
			: entry.Predicted ? PredictionEventConfirmation.AlreadyPredicted : PredictionEventConfirmation.Accepted;
	}

	bool CanAccept( PredictionEventId id ) => epoch != Guid.Empty && id.Object == objectId && id.Epoch == epoch
		&& (!hasExpired || (unchecked(id.Command - expiredThrough) is > 0 and <= 0x7FFFFFFF));

	int Find( PredictionEventId id )
	{
		for ( int i = 0; i < count; i++ )
		{
			var index = (start + i) % entries.Length;

			if ( entries[index].Id == id )
			{
				return index;
			}
		}

		return -1;
	}

	void Add( Entry entry )
	{
		if ( count == entries.Length )
		{
			var evicted = entries[start].Id.Command;

			if ( !hasExpired || unchecked(evicted - expiredThrough) is > 0 and <= 0x7FFFFFFF )
			{
				expiredThrough = evicted;
			}

			hasExpired = true;
			start = (start + 1) % entries.Length;
			count--;
		}

		entries[(start + count++) % entries.Length] = entry;
	}
}
