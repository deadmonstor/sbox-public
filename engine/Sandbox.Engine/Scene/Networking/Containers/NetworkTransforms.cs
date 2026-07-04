using Sandbox.Network;

namespace Sandbox;

/// <summary>
/// A <see cref="NetworkTable{Transform}"/> containing <see cref="Transform">Transforms</see> but each component of the transform
/// is added to a <see cref="DeltaSnapshot"/>.
/// </summary>
internal class NetworkTransforms : NetworkTable<Transform>
{
	private Dictionary<int, (int Position, int Rotation, int Scale)> _componentHashes { get; set; } = new();
	private readonly Dictionary<int, QuantizedVector301> _vectorCache = new();
	private readonly Dictionary<int, QuantizedRotation32> _rotationCache = new();
	private readonly Dictionary<int, byte[]> _serializedCache = new();

	private void UpdateComponentHashes( int key )
	{
		if ( _componentHashes.ContainsKey( key ) )
			return;

		var subSlot = $"{_parentSlot}.{key}";

		_componentHashes[key] = (
			$"{subSlot}.position".FastHash(),
			$"{subSlot}.rotation".FastHash(),
			$"{subSlot}.scale".FastHash()
		);
	}

	private void AddQuantized( LocalSnapshotState state, int slot, QuantizedVector301 value )
	{
		if ( !_vectorCache.TryGetValue( slot, out var cached ) || !cached.Equals( value ) )
		{
			_vectorCache[slot] = value;
			_serializedCache[slot] = QuantizedVector301.ToRawBytes( value );
		}

		state.AddSerialized( slot, _serializedCache[slot], LocalSnapshotState.HashFlags.All );
	}

	private void AddQuantized( LocalSnapshotState state, int slot, QuantizedRotation32 value )
	{
		if ( !_rotationCache.TryGetValue( slot, out var cached ) || !cached.Equals( value ) )
		{
			_rotationCache[slot] = value;
			_serializedCache[slot] = QuantizedRotation32.ToRawBytes( value );
		}

		state.AddSerialized( slot, _serializedCache[slot], LocalSnapshotState.HashFlags.All );
	}

	protected override void WriteSnapshot( int slot, LocalSnapshotState state )
	{
		foreach ( var (key, transform) in Table )
		{
			UpdateComponentHashes( key );

			var hashes = _componentHashes[key];

			AddQuantized( state, hashes.Position, new QuantizedVector301( transform.Position ) );
			AddQuantized( state, hashes.Rotation, new QuantizedRotation32( transform.Rotation ) );
			AddQuantized( state, hashes.Scale, new QuantizedVector301( transform.Scale ) );
		}
	}

	protected override void ReadSnapshot( int slot, DeltaSnapshot snapshot )
	{
		foreach ( var key in Keys )
		{
			UpdateComponentHashes( key );

			var hashes = _componentHashes[key];
			var transform = Get( key );
			var didTransformChange = false;

			if ( snapshot.Lookup.TryGetValue( hashes.Position, out var positionData ) )
			{
				transform.Position = QuantizedVector301.FromRawBytes( positionData.Value ).ToVector3();
				didTransformChange = true;
			}

			if ( snapshot.Lookup.TryGetValue( hashes.Rotation, out var rotationData ) )
			{
				transform.Rotation = QuantizedRotation32.FromRawBytes( rotationData.Value ).ToRotation();
				didTransformChange = true;
			}

			if ( snapshot.Lookup.TryGetValue( hashes.Scale, out var scaleData ) )
			{
				transform.Scale = QuantizedVector301.FromRawBytes( scaleData.Value ).ToVector3();
				didTransformChange = true;
			}

			if ( !didTransformChange )
				continue;

			Table[key] = transform;
			Serialized[key] = Game.TypeLibrary.ToBytes( transform );
		}
	}

	protected override void OnValueChanged( int slot, Transform value )
	{
		UpdateComponentHashes( slot );
	}

	protected override void OnCleared()
	{
		_componentHashes.Clear();
		_vectorCache.Clear();
		_rotationCache.Clear();
		_serializedCache.Clear();
	}

	protected override void OnKeyRemoved( int key )
	{
		if ( _componentHashes.TryGetValue( key, out var hashes ) )
		{
			_vectorCache.Remove( hashes.Position );
			_vectorCache.Remove( hashes.Scale );
			_rotationCache.Remove( hashes.Rotation );
			_serializedCache.Remove( hashes.Position );
			_serializedCache.Remove( hashes.Rotation );
			_serializedCache.Remove( hashes.Scale );
		}

		_componentHashes.Remove( key );
	}

	protected override void OnInit( int slot )
	{
		// NetworkTable might be reinitialized under a new parent slot(?!) - invalidate _componentHashes so the new parent slot is used
		_componentHashes.Clear();
		_vectorCache.Clear();
		_rotationCache.Clear();
		_serializedCache.Clear();

		foreach ( var key in Keys )
		{
			UpdateComponentHashes( key );
		}
	}
}
