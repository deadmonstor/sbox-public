using System.Runtime.InteropServices;
using System.Text;

namespace BotSwarm.Wire;

/// <summary>
/// Growable little-endian writer matching the engine's ByteStream layout. Reserves room for the
/// TCP length prefix and the wire flag so a finished message can be sent without copying.
/// </summary>
internal sealed class PacketWriter
{
	// [int32 length][flag byte]
	public const int HeaderSize = 5;

	byte[] buffer;
	int length;

	public PacketWriter( int capacity = 256 )
	{
		buffer = new byte[Math.Max( capacity, HeaderSize + 16 )];
		Reset();
	}

	public int PayloadLength => length - HeaderSize;

	/// <summary>
	/// Starts a new message.
	/// </summary>
	public PacketWriter Reset()
	{
		length = HeaderSize;
		return this;
	}

	/// <summary>
	/// Writes the TCP frame header for a raw (uncompressed) packet and returns the whole frame.
	/// </summary>
	public ReadOnlySpan<byte> Frame()
	{
		// The frame length covers the flag byte plus the payload
		MemoryMarshal.Write( buffer.AsSpan( 0 ), length - sizeof( int ) );
		buffer[4] = Packet.FlagRaw;
		return buffer.AsSpan( 0, length );
	}

	public Span<byte> Payload => buffer.AsSpan( HeaderSize, length - HeaderSize );

	void Ensure( int count )
	{
		if ( length + count <= buffer.Length ) return;
		Array.Resize( ref buffer, Math.Max( buffer.Length * 2, length + count ) );
	}

	public void Write<T>( T value ) where T : unmanaged
	{
		var size = Unsafe.SizeOf<T>();
		Ensure( size );
		MemoryMarshal.Write( buffer.AsSpan( length ), in value );
		length += size;
	}

	public void Write( ReadOnlySpan<byte> bytes )
	{
		Ensure( bytes.Length );
		bytes.CopyTo( buffer.AsSpan( length ) );
		length += bytes.Length;
	}

	/// <summary>
	/// ByteStream string: int byte count (-1 null, 0 empty) then UTF-8.
	/// </summary>
	public void WriteRawString( string value )
	{
		if ( value is null )
		{
			Write( -1 );
			return;
		}

		var count = Encoding.UTF8.GetByteCount( value );
		Write( count );
		Ensure( count );
		Encoding.UTF8.GetBytes( value, buffer.AsSpan( length ) );
		length += count;
	}

	/// <summary>
	/// Reserve an int to be patched later with <see cref="PatchLength"/>.
	/// </summary>
	public int ReserveInt()
	{
		var at = length;
		Write( 0 );
		return at;
	}

	/// <summary>
	/// Patch a reserved int with the number of bytes written after it.
	/// </summary>
	public void PatchLength( int at )
	{
		MemoryMarshal.Write( buffer.AsSpan( at ), length - at - sizeof( int ) );
	}
}
