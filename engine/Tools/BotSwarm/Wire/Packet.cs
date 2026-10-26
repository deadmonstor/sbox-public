using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace BotSwarm.Wire;

/// <summary>
/// Mirrors Sandbox.Network.InternalMessageType.
/// </summary>
internal enum MessageType : byte
{
	Unknown = 0,
	HeartbeatPing = 1,
	HeartbeatPong = 2,
	Packed = 3,
	ClientTick = 4,
	SetCullState = 5,
	DeltaSnapshot = 6,
	DeltaSnapshotCluster = 7,
	DeltaSnapshotAck = 8,
	DeltaSnapshotClusterAck = 9,
	TableSnapshot = 10,
	TableUpdated = 11,
	Request = 12,
	Response = 13,
	Multicast = 14,
	Rpc = 15,
}

/// <summary>
/// Transport packet decoding, mirroring Connection.Wire.cs and Connection.Receive.cs.
/// Each TCP frame is <c>[int32 length][flag][...]</c>.
/// </summary>
internal sealed class Packet
{
	public const byte FlagRaw = 0;
	public const byte FlagCompressed = 1;
	public const byte FlagChunk = 2;

	const int MaxChunkSize = 128 * 1024;
	const int MaxDecompressed = 256 * 1024 * 1024;

	const int KeepBufferSize = 256 * 1024;

	byte[] decodeBuffer = new byte[64 * 1024];
	byte[] chunkBuffer;
	int chunkLength = -1;
	uint chunkExpected;

	/// <summary>
	/// Drop buffers grown for a one-off big message (the join snapshot), so hundreds of bots
	/// don't each hold on to megabytes. Call after the decoded message has been handled.
	/// </summary>
	public void Trim()
	{
		if ( decodeBuffer.Length > KeepBufferSize ) decodeBuffer = new byte[64 * 1024];
		if ( chunkLength < 0 ) chunkBuffer = null;
	}

	/// <summary>
	/// Feed one transport packet. Returns true with the decoded message when one is complete.
	/// The returned span is only valid until the next call.
	/// </summary>
	public bool TryDecode( ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> message )
	{
		message = default;
		if ( packet.Length < 1 ) return false;

		if ( packet[0] != FlagChunk )
		{
			message = Decode( packet );
			return true;
		}

		if ( packet.Length < 9 ) throw new InvalidDataException( "Chunk packet too short" );

		var index = BinaryPrimitives.ReadUInt32LittleEndian( packet.Slice( 1 ) );
		var total = BinaryPrimitives.ReadUInt32LittleEndian( packet.Slice( 5 ) );
		var data = packet.Slice( 9 );

		if ( total <= 1 || total > 1024 || index >= total ) throw new InvalidDataException( $"Bad chunk {index}/{total}" );

		if ( index == 0 )
		{
			var required = (int)total * MaxChunkSize;
			if ( chunkBuffer is null || chunkBuffer.Length < required )
				chunkBuffer = new byte[required];

			chunkLength = 0;
			chunkExpected = 0;
		}

		if ( chunkLength < 0 || index != chunkExpected )
		{
			chunkLength = -1;
			throw new InvalidDataException( $"Out of order chunk {index}/{total}, expected {chunkExpected}" );
		}

		chunkExpected++;
		data.CopyTo( chunkBuffer.AsSpan( chunkLength ) );
		chunkLength += data.Length;

		if ( index + 1 < total ) return false;

		var assembled = chunkBuffer.AsSpan( 0, chunkLength );
		chunkLength = -1;
		message = Decode( assembled );
		return true;
	}

	ReadOnlySpan<byte> Decode( ReadOnlySpan<byte> data )
	{
		switch ( data[0] )
		{
			case FlagRaw:
				return data.Slice( 1 );

			case FlagCompressed:
				{
					var originalLength = BinaryPrimitives.ReadInt32LittleEndian( data.Slice( 1 ) );
					if ( originalLength <= 0 || originalLength > MaxDecompressed )
						throw new InvalidDataException( $"Compressed length {originalLength} out of range" );

					if ( decodeBuffer.Length < originalLength )
						decodeBuffer = new byte[originalLength];

					var written = LZ4Codec.Decode( data.Slice( 5 ), decodeBuffer.AsSpan( 0, originalLength ) );
					if ( written != originalLength )
						throw new InvalidDataException( $"LZ4 decoded {written}b, expected {originalLength}b" );

					return decodeBuffer.AsSpan( 0, originalLength );
				}

			default:
				throw new InvalidDataException( $"Unknown wire flag {data[0]}" );
		}
	}
}
