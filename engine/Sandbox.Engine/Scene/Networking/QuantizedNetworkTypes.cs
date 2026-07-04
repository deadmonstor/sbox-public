namespace Sandbox;

/// <summary>
/// Quantized float with 0.01 step size using signed 24-bit storage.
/// Range is approximately +/- 83,886.
/// </summary>
[Expose]
public readonly struct QuantizedFloat01 : BytePack.ISerializer, IEquatable<QuantizedFloat01>
{
	private const float Scale = 100f;
	private const int MinValue = -8_388_608;
	private const int MaxValue = 8_388_607;

	private readonly int _packed;

	public QuantizedFloat01( float value )
	{
		_packed = Quantize( value );
	}

	private QuantizedFloat01( int packed )
	{
		_packed = packed;
	}

	private static int Quantize( float value )
	{
		if ( !float.IsFinite( value ) )
			return 0;

		var scaled = (int)MathF.Round( value * Scale );
		return Math.Clamp( scaled, MinValue, MaxValue );
	}

	public float ToFloat() => _packed / Scale;

	public static implicit operator QuantizedFloat01( float value ) => new( value );
	public static implicit operator float( QuantizedFloat01 value ) => value.ToFloat();

	public static byte[] ToRawBytes( float value )
	{
		return ToRawBytes( new QuantizedFloat01( value ) );
	}

	public static byte[] ToRawBytes( QuantizedFloat01 value )
	{
		var bs = ByteStream.Create( 3 );
		value.WriteRaw( ref bs );
		var bytes = bs.ToArray();
		bs.Dispose();
		return bytes;
	}

	public static QuantizedFloat01 FromRawBytes( ReadOnlySpan<byte> data )
	{
		var bs = ByteStream.CreateReader( data );
		var value = ReadRaw( ref bs );
		bs.Dispose();
		return value;
	}

	public void WriteRaw( ref ByteStream bs )
	{
		WriteInt24( ref bs, _packed );
	}

	public static QuantizedFloat01 ReadRaw( ref ByteStream bs )
	{
		return new QuantizedFloat01( ReadInt24( ref bs ) );
	}

	static object BytePack.ISerializer.BytePackRead( ref ByteStream bs, Type targetType )
	{
		return ReadRaw( ref bs );
	}

	static void BytePack.ISerializer.BytePackWrite( object value, ref ByteStream bs )
	{
		((QuantizedFloat01)value).WriteRaw( ref bs );
	}

	private static void WriteInt24( ref ByteStream bs, int value )
	{
		var v = value & 0x00FF_FFFF;
		bs.Write( (byte)(v & 0xFF) );
		bs.Write( (byte)((v >> 8) & 0xFF) );
		bs.Write( (byte)((v >> 16) & 0xFF) );
	}

	private static int ReadInt24( ref ByteStream bs )
	{
		var v = bs.Read<byte>() | (bs.Read<byte>() << 8) | (bs.Read<byte>() << 16);
		if ( (v & 0x0080_0000) != 0 )
			v |= unchecked( (int)0xFF00_0000 );

		return v;
	}

	public bool Equals( QuantizedFloat01 other ) => _packed == other._packed;
	public override bool Equals( object obj ) => obj is QuantizedFloat01 other && Equals( other );
	public override int GetHashCode() => _packed;
}

/// <summary>
/// Quantized Vector3 with 0.01 step size per-axis using signed 24-bit storage.
/// Range is approximately +/- 83,886 units per axis.
/// </summary>
[Expose]
public readonly struct QuantizedVector301 : BytePack.ISerializer, IEquatable<QuantizedVector301>
{
	private const float Scale = 100f;
	private const int MinValue = -8_388_608;
	private const int MaxValue = 8_388_607;

	private readonly int _x;
	private readonly int _y;
	private readonly int _z;

	public QuantizedVector301( Vector3 value )
	{
		_x = Quantize( value.x );
		_y = Quantize( value.y );
		_z = Quantize( value.z );
	}

	private QuantizedVector301( int x, int y, int z )
	{
		_x = x;
		_y = y;
		_z = z;
	}

	private static int Quantize( float value )
	{
		if ( !float.IsFinite( value ) )
			return 0;

		var scaled = (int)MathF.Round( value * Scale );
		return Math.Clamp( scaled, MinValue, MaxValue );
	}

	public Vector3 ToVector3() => new( _x / Scale, _y / Scale, _z / Scale );

	public static implicit operator QuantizedVector301( Vector3 value ) => new( value );
	public static implicit operator Vector3( QuantizedVector301 value ) => value.ToVector3();

	public static byte[] ToRawBytes( Vector3 value )
	{
		return ToRawBytes( new QuantizedVector301( value ) );
	}

	public static byte[] ToRawBytes( QuantizedVector301 value )
	{
		var bs = ByteStream.Create( 9 );
		value.WriteRaw( ref bs );
		var bytes = bs.ToArray();
		bs.Dispose();
		return bytes;
	}

	public static QuantizedVector301 FromRawBytes( ReadOnlySpan<byte> data )
	{
		var bs = ByteStream.CreateReader( data );
		var value = ReadRaw( ref bs );
		bs.Dispose();
		return value;
	}

	public void WriteRaw( ref ByteStream bs )
	{
		WriteInt24( ref bs, _x );
		WriteInt24( ref bs, _y );
		WriteInt24( ref bs, _z );
	}

	public static QuantizedVector301 ReadRaw( ref ByteStream bs )
	{
		var x = ReadInt24( ref bs );
		var y = ReadInt24( ref bs );
		var z = ReadInt24( ref bs );
		return new QuantizedVector301( x, y, z );
	}

	static object BytePack.ISerializer.BytePackRead( ref ByteStream bs, Type targetType )
	{
		return ReadRaw( ref bs );
	}

	static void BytePack.ISerializer.BytePackWrite( object value, ref ByteStream bs )
	{
		((QuantizedVector301)value).WriteRaw( ref bs );
	}

	private static void WriteInt24( ref ByteStream bs, int value )
	{
		var v = value & 0x00FF_FFFF;
		bs.Write( (byte)(v & 0xFF) );
		bs.Write( (byte)((v >> 8) & 0xFF) );
		bs.Write( (byte)((v >> 16) & 0xFF) );
	}

	private static int ReadInt24( ref ByteStream bs )
	{
		var v = bs.Read<byte>() | (bs.Read<byte>() << 8) | (bs.Read<byte>() << 16);
		if ( (v & 0x0080_0000) != 0 )
			v |= unchecked( (int)0xFF00_0000 );

		return v;
	}

	public bool Equals( QuantizedVector301 other ) => _x == other._x && _y == other._y && _z == other._z;
	public override bool Equals( object obj ) => obj is QuantizedVector301 other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( _x, _y, _z );
}

/// <summary>
/// Quantized rotation in 32 bits by omitting the largest quaternion component and packing the remaining 3.
/// </summary>
[Expose]
public readonly struct QuantizedRotation32 : BytePack.ISerializer, IEquatable<QuantizedRotation32>
{
	private readonly uint _encoded;

	public QuantizedRotation32( Rotation rotation )
	{
		rotation = rotation.Normal;

		if ( !float.IsFinite( rotation.x ) || !float.IsFinite( rotation.y ) || !float.IsFinite( rotation.z ) || !float.IsFinite( rotation.w ) )
		{
			_encoded = 0;
			return;
		}

		var absX = MathF.Abs( rotation.x );
		var absY = MathF.Abs( rotation.y );
		var absZ = MathF.Abs( rotation.z );
		var absW = MathF.Abs( rotation.w );

		ComponentIndex omitted;
		float a;
		float b;
		float c;

		if ( absX >= absY && absX >= absZ && absX >= absW )
		{
			omitted = ComponentIndex.X;
			var sign = MathF.Sign( rotation.x );
			a = rotation.y * sign;
			b = rotation.z * sign;
			c = rotation.w * sign;
		}
		else if ( absY >= absZ && absY >= absW )
		{
			omitted = ComponentIndex.Y;
			var sign = MathF.Sign( rotation.y );
			a = rotation.x * sign;
			b = rotation.z * sign;
			c = rotation.w * sign;
		}
		else if ( absZ >= absW )
		{
			omitted = ComponentIndex.Z;
			var sign = MathF.Sign( rotation.z );
			a = rotation.x * sign;
			b = rotation.y * sign;
			c = rotation.w * sign;
		}
		else
		{
			omitted = ComponentIndex.W;
			var sign = MathF.Sign( rotation.w );
			a = rotation.x * sign;
			b = rotation.y * sign;
			c = rotation.z * sign;
		}

		_encoded = (((uint)omitted) << 30)
			| (PackComponent( a ) << 20)
			| (PackComponent( b ) << 10)
			| PackComponent( c );
	}

	private QuantizedRotation32( uint encoded )
	{
		_encoded = encoded;
	}

	private enum ComponentIndex : byte
	{
		X,
		Y,
		Z,
		W
	}

	private const float SqrtTwo = 1.41421356237f;
	private const float SqrtHalf = SqrtTwo / 2f;

	private static uint PackComponent( float value )
	{
		return (uint)(((value + SqrtHalf) / SqrtTwo).Clamp( 0f, 1f ) * 0x3ff);
	}

	private static float UnpackComponent( uint value )
	{
		return value * SqrtTwo / 0x3ff - SqrtHalf;
	}

	public Rotation ToRotation()
	{
		var omitted = (ComponentIndex)((_encoded >> 30) & 0x3);
		var a = UnpackComponent( (_encoded >> 20) & 0x3ff );
		var b = UnpackComponent( (_encoded >> 10) & 0x3ff );
		var c = UnpackComponent( _encoded & 0x3ff );
		var d = MathF.Sqrt( (1f - a * a - b * b - c * c).Clamp( 0f, 1f ) );

		return omitted switch
		{
			ComponentIndex.X => new Rotation( d, a, b, c ).Normal,
			ComponentIndex.Y => new Rotation( a, d, b, c ).Normal,
			ComponentIndex.Z => new Rotation( a, b, d, c ).Normal,
			ComponentIndex.W => new Rotation( a, b, c, d ).Normal,
			_ => Rotation.Identity
		};
	}

	public static implicit operator QuantizedRotation32( Rotation value ) => new( value );
	public static implicit operator Rotation( QuantizedRotation32 value ) => value.ToRotation();

	public static byte[] ToRawBytes( Rotation value )
	{
		return ToRawBytes( new QuantizedRotation32( value ) );
	}

	public static byte[] ToRawBytes( QuantizedRotation32 value )
	{
		var bs = ByteStream.Create( 4 );
		value.WriteRaw( ref bs );
		var bytes = bs.ToArray();
		bs.Dispose();
		return bytes;
	}

	public static QuantizedRotation32 FromRawBytes( ReadOnlySpan<byte> data )
	{
		var bs = ByteStream.CreateReader( data );
		var value = ReadRaw( ref bs );
		bs.Dispose();
		return value;
	}

	public void WriteRaw( ref ByteStream bs )
	{
		bs.Write( _encoded );
	}

	public static QuantizedRotation32 ReadRaw( ref ByteStream bs )
	{
		return new QuantizedRotation32( bs.Read<uint>() );
	}

	static object BytePack.ISerializer.BytePackRead( ref ByteStream bs, Type targetType )
	{
		return ReadRaw( ref bs );
	}

	static void BytePack.ISerializer.BytePackWrite( object value, ref ByteStream bs )
	{
		((QuantizedRotation32)value).WriteRaw( ref bs );
	}

	public bool Equals( QuantizedRotation32 other ) => _encoded == other._encoded;
	public override bool Equals( object obj ) => obj is QuantizedRotation32 other && Equals( other );
	public override int GetHashCode() => (int)_encoded;
}
