using System.Numerics;

namespace Sandbox.Network;

/// <summary>
/// Connection-index membership without allocations for the first 64 connections.
/// Mutable masks own their overflow words; do not mutate a shallow copy of an overflow mask.
/// </summary>
internal struct ConnectionMask
{
	private ulong _bits;
	private ulong[] _overflow;

	internal readonly ulong Word( int word ) => word == 0 ? _bits
		: _overflow is not null && word <= _overflow.Length ? _overflow[word - 1] : 0;

	internal readonly bool Contains( int index ) => index >= 0 && (Word( index >> 6 ) & (1UL << (index & 63))) != 0;

	internal void Add( int index )
	{
		ArgumentOutOfRangeException.ThrowIfNegative( index );
		var bit = 1UL << (index & 63);
		if ( index < 64 ) { _bits |= bit; return; }
		var word = (index >> 6) - 1;
		if ( _overflow is null || word >= _overflow.Length ) Array.Resize( ref _overflow, word + 1 );
		_overflow[word] |= bit;
	}

	internal void Remove( int index )
	{
		if ( index < 0 ) return;
		var bit = ~(1UL << (index & 63));
		if ( index < 64 ) { _bits &= bit; return; }
		var word = (index >> 6) - 1;
		if ( _overflow is not null && word < _overflow.Length ) _overflow[word] &= bit;
	}

	internal void Clear()
	{
		_bits = 0;
		if ( _overflow is not null ) Array.Clear( _overflow );
	}

	internal readonly bool ContainsAll( in ConnectionMask active )
	{
		if ( (_bits & active._bits) != active._bits ) return false;
		if ( active._overflow is null ) return true;
		for ( var i = 0; i < active._overflow.Length; i++ )
			if ( (Word( i + 1 ) & active._overflow[i]) != active._overflow[i] ) return false;
		return true;
	}

	internal readonly int Count => BitOperations.PopCount( _bits )
		+ (_overflow?.Sum( word => BitOperations.PopCount( word ) ) ?? 0);
}

/// <summary>
/// Indices are scoped to one delta snapshot system. Callers clear receipts before recycling.
/// </summary>
internal sealed class ConnectionIndices
{
	private readonly Dictionary<Guid, int> _indices = new();
	private readonly Stack<int> _free = new();
	private int _next;

	internal int GetOrAdd( Guid id )
	{
		if ( _indices.TryGetValue( id, out var index ) ) return index;
		index = _free.TryPop( out var recycled ) ? recycled : _next++;
		_indices.Add( id, index );
		return index;
	}

	internal bool TryGet( Guid id, out int index ) => _indices.TryGetValue( id, out index );

	internal void Remove( Guid id )
	{
		if ( _indices.Remove( id, out var index ) ) _free.Push( index );
	}
}
