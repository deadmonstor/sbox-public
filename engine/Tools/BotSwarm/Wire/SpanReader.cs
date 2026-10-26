using System.Runtime.InteropServices;
using System.Text;

namespace BotSwarm.Wire;

/// <summary>
/// Little-endian reader over a received message, matching the engine's ByteStream layout.
/// </summary>
internal ref struct SpanReader
{
	readonly ReadOnlySpan<byte> data;

	public int Position;

	public SpanReader( ReadOnlySpan<byte> data )
	{
		this.data = data;
		Position = 0;
	}

	public readonly int Remaining => data.Length - Position;

	public T Read<T>() where T : unmanaged
	{
		var size = Unsafe.SizeOf<T>();
		if ( size > Remaining ) throw new InvalidDataException( $"Read {typeof( T ).Name} past end ({Remaining}b left)" );

		var value = MemoryMarshal.Read<T>( data.Slice( Position ) );
		Position += size;
		return value;
	}

	public ReadOnlySpan<byte> ReadBytes( int count )
	{
		if ( count < 0 || count > Remaining ) throw new InvalidDataException( $"Read {count}b past end ({Remaining}b left)" );

		var span = data.Slice( Position, count );
		Position += count;
		return span;
	}

	public void Skip( int count ) => ReadBytes( count );

	public string ReadString()
	{
		var count = Read<int>();
		if ( count < 0 ) return null;
		if ( count == 0 ) return string.Empty;
		return Encoding.UTF8.GetString( ReadBytes( count ) );
	}
}
