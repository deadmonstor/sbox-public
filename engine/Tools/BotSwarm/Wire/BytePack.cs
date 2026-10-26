using Sandbox.Hashing;
using System.Runtime.InteropServices;

namespace BotSwarm.Wire;

/// <summary>
/// Mirrors Sandbox.BytePack.Identifier. Every BytePack value starts with one of these.
/// </summary>
internal enum PackId : byte
{
	Null,
	Object,
	String,
	Bool,
	Byte,
	SByte,
	Int,
	UInt,
	Float,
	Double,
	Decimal,
	Short,
	UShort,
	Guid,
	TimeSpan,
	DateTime,
	DateTimeOffset,
	Long,
	ULong,
	Char,
	ArrayValue,
	Array,
	List,
	Dictionary,
	Runtime,
	Vector2,
	Vector2Int,
	Vector3,
	Vector3Int,
	Vector4,
	Rotation,
	Angles,
	Transform,
	Color,
	Color32,
	BBox,
	Sphere,
	Plane,
	Rect,
	Ray,
	Line,
	Matrix,
	SteamId
}

/// <summary>
/// A runtime (TypeLibrary) type the bot knows how to read or write.
/// The engine identifies it by <c>FastHash("FullName,AssemblyName")</c>. Unmanaged structs
/// are written as raw memory, anything else as each field in descending field-name order.
/// </summary>
internal sealed class PackSchema
{
	public string Name { get; }
	public int Ident { get; }

	/// <summary>
	/// Raw byte size for unmanaged structs (ValuePacker), -1 for field-by-field structs (TypePacker).
	/// </summary>
	public int PodSize { get; }

	/// <summary>
	/// Field names in wire order.
	/// </summary>
	public string[] Fields { get; }

	PackSchema( string fullName, string assembly, int podSize, string[] properties )
	{
		Name = fullName;
		Ident = TypeIdent( fullName, assembly );
		PodSize = podSize;

		// TypePacker orders struct fields with OrderByDescending( FieldInfo.Name ) using the default
		// (culture) comparer. Auto-properties are stored in "<Name>k__BackingField".
		Fields = properties
			.OrderByDescending( x => $"<{x}>k__BackingField" )
			.ToArray();
	}

	public static PackSchema Pod( string fullName, int size, string assembly = "Sandbox.Engine" ) => new( fullName, assembly, size, [] );
	public static PackSchema Struct( string fullName, params string[] properties ) => new( fullName, "Sandbox.Engine", -1, properties );

	public int IndexOf( string field ) => Array.IndexOf( Fields, field );

	public static int TypeIdent( string fullName, string assembly ) => FastHash( $"{fullName},{assembly}" );

	/// <summary>
	/// Matches Sandbox StringExtensions.FastHash: the engine's XxHash3 over the UTF-16 bytes, truncated.
	/// </summary>
	public static int FastHash( string value ) => (int)XxHash3.HashToUInt64( MemoryMarshal.AsBytes( value.AsSpan() ) );
}

/// <summary>
/// A decoded TypePacker struct, values in <see cref="PackSchema.Fields"/> order.
/// </summary>
internal sealed record PackedObject( PackSchema Schema, object[] Values )
{
	public object this[string field] => Values[Schema.IndexOf( field )];
}

/// <summary>
/// Sandbox.Transform memory layout.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
internal struct PackTransform
{
	public System.Numerics.Vector3 Position;
	public System.Numerics.Vector3 Scale;
	public System.Numerics.Quaternion Rotation;
}

/// <summary>
/// A decoded ValuePacker struct: the raw unmanaged bytes.
/// </summary>
internal sealed record PackedPod( PackSchema Schema, byte[] Bytes )
{
	public T As<T>() where T : unmanaged => MemoryMarshal.Read<T>( Bytes );
}

internal sealed class UnknownPackTypeException( int ident ) : Exception( $"Unknown runtime type {ident}" )
{
	public int Ident { get; } = ident;
}

internal static class BytePack
{
	static readonly Dictionary<int, PackSchema> schemas = new();

	public static void Register( PackSchema schema ) => schemas[schema.Ident] = schema;

	public static bool TryGetSchema( int ident, out PackSchema schema ) => schemas.TryGetValue( ident, out schema );

	//
	// Writing
	//

	public static void WriteNull( this PacketWriter w ) => w.Write( (byte)PackId.Null );

	public static void WriteString( this PacketWriter w, string value )
	{
		if ( value is null )
		{
			w.WriteNull();
			return;
		}

		w.Write( (byte)PackId.String );
		w.WriteRawString( value );
	}

	public static void WriteBool( this PacketWriter w, bool value )
	{
		w.Write( (byte)PackId.Bool );
		w.Write( value );
	}

	public static void WriteInt( this PacketWriter w, int value )
	{
		w.Write( (byte)PackId.Int );
		w.Write( value );
	}

	public static void WriteGuid( this PacketWriter w, Guid value )
	{
		w.Write( (byte)PackId.Guid );
		w.Write( value );
	}

	public static void WriteSteamId( this PacketWriter w, ulong value )
	{
		w.Write( (byte)PackId.SteamId );
		w.Write( value );
	}

	public static void WriteDateTimeOffset( this PacketWriter w, DateTimeOffset value )
	{
		w.Write( (byte)PackId.DateTimeOffset );
		w.Write( value );
	}

	public static void WriteStringDictionary( this PacketWriter w, IReadOnlyDictionary<string, string> values )
	{
		if ( values is null )
		{
			w.WriteNull();
			return;
		}

		// [Dictionary][count][key header][value header] then raw key/value bodies
		w.Write( (byte)PackId.Dictionary );
		w.Write( values.Count );
		w.Write( (byte)PackId.String );
		w.Write( (byte)PackId.String );

		foreach ( var (key, value) in values )
		{
			w.WriteRawString( key );
			w.WriteRawString( value );
		}
	}

	public static void WriteRuntimeHeader( this PacketWriter w, PackSchema schema )
	{
		w.Write( (byte)PackId.Runtime );
		w.Write( schema.Ident );
	}

	/// <summary>
	/// Write a TypePacker struct. Field writers are supplied by name and emitted in wire order;
	/// every field must be present.
	/// </summary>
	public static void WriteStruct( this PacketWriter w, PackSchema schema, IReadOnlyDictionary<string, Action<PacketWriter>> fields )
	{
		w.WriteRuntimeHeader( schema );

		foreach ( var name in schema.Fields )
		{
			if ( !fields.TryGetValue( name, out var write ) )
				throw new InvalidOperationException( $"{schema.Name}.{name} not supplied" );

			write( w );
		}
	}

	/// <summary>
	/// Write a ValuePacker struct (raw unmanaged bytes).
	/// </summary>
	public static void WritePod<T>( this PacketWriter w, PackSchema schema, T value ) where T : unmanaged
	{
		if ( Unsafe.SizeOf<T>() != schema.PodSize )
			throw new InvalidOperationException( $"{schema.Name} is {schema.PodSize}b, got {Unsafe.SizeOf<T>()}b" );

		w.WriteRuntimeHeader( schema );
		w.Write( value );
	}

	//
	// Reading
	//

	/// <summary>
	/// Read one complete BytePack value (header + body).
	/// Primitives come back boxed, <c>byte[]</c> for byte value arrays, <see cref="PackedObject"/>
	/// / <see cref="PackedPod"/> for registered runtime types.
	/// </summary>
	public static object ReadValue( ref SpanReader r, int depth = 0 )
	{
		var id = (PackId)r.Read<byte>();
		var ident = id == PackId.Runtime ? r.Read<int>() : 0;
		return ReadBody( ref r, id, ident, depth );
	}

	/// <summary>
	/// Read just the header of a value, returning the runtime ident when it is a runtime type.
	/// </summary>
	public static PackId ReadHeader( ref SpanReader r, out int ident )
	{
		var id = (PackId)r.Read<byte>();
		ident = id == PackId.Runtime ? r.Read<int>() : 0;
		return id;
	}

	static object ReadBody( ref SpanReader r, PackId id, int ident, int depth )
	{
		if ( depth > 32 ) throw new InvalidDataException( "BytePack nesting too deep" );

		switch ( id )
		{
			case PackId.Null: return null;
			case PackId.Object: return ReadValue( ref r, depth + 1 );
			case PackId.String: return r.ReadString();
			case PackId.Bool: return r.Read<bool>();
			case PackId.Byte: return r.Read<byte>();
			case PackId.Short: return r.Read<short>();
			case PackId.UShort: return r.Read<ushort>();
			case PackId.Int: return r.Read<int>();
			case PackId.UInt: return r.Read<uint>();
			case PackId.Float: return r.Read<float>();
			case PackId.Double: return r.Read<double>();
			case PackId.Long: return r.Read<long>();
			case PackId.Transform: return r.Read<PackTransform>();
			case PackId.ULong: return r.Read<ulong>();
			case PackId.Guid: return r.Read<Guid>();
			case PackId.SteamId: return r.Read<ulong>();

			case PackId.ArrayValue:
				{
					var count = r.Read<int>();
					var elementId = ReadHeader( ref r, out var elementIdent );
					var size = PodSize( elementId, elementIdent );
					var bytes = r.ReadBytes( checked(count * size) );
					return elementId == PackId.Byte ? bytes.ToArray() : null;
				}

			case PackId.Array:
			case PackId.List:
				{
					var count = r.Read<int>();
					var mode = r.Read<byte>();
					var list = new List<object>( Math.Min( count, 4096 ) );

					if ( mode == 0 )
					{
						// Unmanaged elements, one header then raw memory
						var elementId = ReadHeader( ref r, out var elementIdent );
						r.Skip( checked(count * PodSize( elementId, elementIdent )) );
						return list;
					}

					if ( mode == 2 )
					{
						// Element type header once, then each element as a full value
						ReadHeader( ref r, out _ );
					}
					else if ( mode != 1 )
					{
						throw new InvalidDataException( $"Unknown list mode {mode}" );
					}

					for ( var i = 0; i < count; i++ )
						list.Add( ReadValue( ref r, depth + 1 ) );

					return list;
				}

			case PackId.Dictionary:
				{
					var count = r.Read<int>();
					var keyId = ReadHeader( ref r, out var keyIdent );
					var valueId = ReadHeader( ref r, out var valueIdent );
					var dict = new Dictionary<object, object>();

					for ( var i = 0; i < count; i++ )
					{
						var key = ReadBody( ref r, keyId, keyIdent, depth + 1 );
						var value = ReadBody( ref r, valueId, valueIdent, depth + 1 );
						if ( key is not null ) dict[key] = value;
					}

					return dict;
				}

			case PackId.Runtime:
				{
					if ( !schemas.TryGetValue( ident, out var schema ) )
						throw new UnknownPackTypeException( ident );

					if ( schema.PodSize >= 0 )
						return new PackedPod( schema, r.ReadBytes( schema.PodSize ).ToArray() );

					var values = new object[schema.Fields.Length];
					for ( var i = 0; i < values.Length; i++ )
						values[i] = ReadValue( ref r, depth + 1 );

					return new PackedObject( schema, values );
				}

			default:
				r.Skip( PodSize( id, ident ) );
				return null;
		}
	}

	/// <summary>
	/// Size of an unmanaged value body, for skipping.
	/// </summary>
	static int PodSize( PackId id, int ident ) => id switch
	{
		PackId.Bool or PackId.Byte or PackId.SByte => 1,
		PackId.Short or PackId.UShort or PackId.Char => 2,
		PackId.Int or PackId.UInt or PackId.Float or PackId.Color32 => 4,
		PackId.Double or PackId.Long or PackId.ULong or PackId.TimeSpan or PackId.DateTime or PackId.SteamId or PackId.Vector2 or PackId.Vector2Int => 8,
		PackId.Vector3 or PackId.Vector3Int or PackId.Angles => 12,
		PackId.Decimal or PackId.Guid or PackId.DateTimeOffset or PackId.Vector4 or PackId.Rotation or PackId.Color or PackId.Sphere or PackId.Plane or PackId.Rect => 16,
		PackId.BBox or PackId.Ray or PackId.Line => 24,
		PackId.Transform => 40,
		PackId.Matrix => 64,
		PackId.Runtime when schemas.TryGetValue( ident, out var s ) && s.PodSize >= 0 => s.PodSize,
		PackId.Runtime => throw new UnknownPackTypeException( ident ),
		_ => throw new InvalidDataException( $"Not an unmanaged BytePack id: {id}" )
	};
}
