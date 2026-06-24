using System.Runtime.InteropServices;
using Sandbox.Engine;

namespace Sandbox.Network;

internal class SnapshotValueCache
{
	private readonly Dictionary<int, CacheEntry> _cache = new();

	private struct CacheEntry
	{
		public int Hash;
		public byte[] Bytes;
	}

	/// <summary>
	/// Get cached bytes from the specified value if they exist. If the value is different,
	/// then re-serialize and cache again.
	/// </summary>
	public byte[] GetCached<T>( int slot, in T value, out bool isEqual )
	{
		var hash = value is null ? 0 : value.GetHashCode();

		ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault( _cache, slot, out bool exists );

		if ( exists && entry.Hash == hash )
		{
			isEqual = true;
			return entry.Bytes;
		}

		var bytes = GlobalContext.Current.TypeLibrary.ToBytes( value );

		entry.Hash = hash;
		entry.Bytes = bytes;
		isEqual = false;

		return bytes;
	}

	public void Remove( int slot ) => _cache.Remove( slot );
	public void Clear() => _cache.Clear();
}
