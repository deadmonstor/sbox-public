using System.Threading;

namespace Borealis.Source2.Models;

/// <summary>
/// A model's decoded bytes and, when they were uploaded unchanged, how to get them back. Workers take their own reference
/// to the array before reading it. A buffer rewritten on the GPU must keep its original, not read back the new bytes.
/// </summary>
internal sealed class GeometryBytes( byte[] bytes, Func<byte[]> reload = null )
{
	byte[] resident = bytes;
	WeakReference<byte[]> decoded;
	Func<byte[]> readback = reload;

	public byte[] Cached
	{
		get
		{
			if ( Volatile.Read( ref resident ) is { } data ) return data;
			return Volatile.Read( ref decoded ) is { } weak && weak.TryGetTarget( out var known ) ? known : null;
		}
	}

	public byte[] Read()
	{
		if ( Cached is { } data ) return data;
		lock ( this )
		{
			if ( Cached is { } known ) return known;
			if ( readback is null ) return null;
			data = readback();
			decoded.SetTarget( data );
			return data;
		}
	}

	public void Evict()
	{
		lock ( this )
		{
			if ( readback is null || resident is not { Length: > 0 } data ) return;
			decoded = new WeakReference<byte[]>( data );
			Volatile.Write( ref resident, null );
		}
	}

	public void Keep( byte[] original )
	{
		lock ( this )
		{
			readback = null;
			Volatile.Write( ref resident, original );
			decoded = null;
		}
	}
}
