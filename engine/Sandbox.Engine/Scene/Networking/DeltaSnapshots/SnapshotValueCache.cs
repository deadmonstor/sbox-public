using Sandbox.Engine;

namespace Sandbox.Network;

internal class SnapshotValueCache
{
	private readonly Dictionary<int, (int Hash, byte[] Bytes)> _cache = new();

	/// <summary>
	/// Get cached bytes from the specified value if they exist. If the value is different,
	/// then re-serialize and cache again.
	/// </summary>
	public byte[] GetCached<T>( int slot, in T value, out bool isEqual )
	{
		var hash = value?.GetHashCode() ?? 0;

		if ( _cache.TryGetValue( slot, out var cached ) && cached.Hash == hash )
		{
			isEqual = true;
			return cached.Bytes;
		}

		var bytes = GlobalContext.Current.TypeLibrary.ToBytes( value );
		_cache[slot] = (hash, bytes);

		isEqual = false;

		return bytes;
	}

	public void Remove( int slot )
	{
		_cache.Remove( slot );
	}

	public void Clear()
	{
		_cache.Clear();
	}
}
