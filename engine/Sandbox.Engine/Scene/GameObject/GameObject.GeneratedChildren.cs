using Sandbox.Hashing;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Sandbox;

public partial class GameObject
{
	/// <summary>
	/// Model renderers create a GameObject for every bone and attachment. Unless something was added to one,
	/// they're all alike: no components, default settings, and a transform that's serialized as identity anyway.
	/// Networked serialization writes them into a compact table on their parent instead of a JSON object each,
	/// and deserialization turns the table back into the same objects.
	/// </summary>
	internal bool IsPlainGeneratedObject
	{
		get
		{
			if ( Flags != GameObjectFlags.Bone && Flags != GameObjectFlags.Attachment ) return false;
			if ( GetType() != typeof( GameObject ) ) return false;
			if ( !Enabled ) return false;
			if ( Components.Count > 0 ) return false;
			if ( _prefabInstanceData is not null ) return false;
			if ( NetworkMode != NetworkMode.Snapshot ) return false;
			if ( NetworkFlags != NetworkFlags.None ) return false;
			if ( NetworkOrphaned != NetworkOrphaned.Destroy ) return false;
			if ( !AlwaysTransmit ) return false;
			if ( OwnerTransfer != OwnerTransfer.Fixed ) return false;
			if ( Tags.TryGetAll( false ).Any() ) return false;

			for ( int i = 0; i < Children.Count; i++ )
			{
				var child = Children[i];
				if ( child is null || child.IsDestroyed ) continue;
				if ( !child.IsPlainGeneratedObject ) return false;
			}

			return true;
		}
	}

	/// <summary>
	/// The id a model renderer gives a bone or attachment GameObject it creates. Derived from the parent, so
	/// the table can leave it out, and a client rebuilding the object arrives at the same id.
	/// </summary>
	internal static Guid DeriveGeneratedChildId( Guid parentId, bool attachment, string name )
	{
		var nameBytes = MemoryMarshal.AsBytes( name.AsSpan() );
		var length = 17 + nameBytes.Length;
		var rented = length > 512 ? ArrayPool<byte>.Shared.Rent( length ) : null;
		Span<byte> input = rented is null ? stackalloc byte[length] : rented.AsSpan( 0, length );

		parentId.TryWriteBytes( input[..16] );
		input[16] = attachment ? (byte)1 : (byte)0;
		nameBytes.CopyTo( input[17..] );

		Span<byte> derived = stackalloc byte[16];
		BinaryPrimitives.WriteUInt64LittleEndian( derived[..8], XxHash3.HashToUInt64( input ) );
		BinaryPrimitives.WriteUInt64LittleEndian( derived[8..], XxHash3.HashToUInt64( input, seed: 0x2545f4914f6cdd1d ) );

		if ( rented is not null ) ArrayPool<byte>.Shared.Return( rented );

		// RFC 4122 version 8 (custom), same as prefab-derived ids
		derived[7] = (byte)((derived[7] & 0x0F) | 0x80);
		derived[8] = (byte)((derived[8] & 0x3F) | 0x80);

		return new Guid( derived );
	}

	/// <summary>
	/// Builds the <see cref="JsonKeys.GeneratedChildren"/> table while serializing a parent's children.
	/// <code>
	/// [byte version][varint count] then per object, parents before their children:
	/// [byte flags][varint parent entry, 0 = the object holding the table][varint index among its siblings]
	/// [varint name length][utf8 name][16 byte id, only if it isn't the derived one]
	/// </code>
	/// </summary>
	private struct GeneratedChildTable
	{
		const byte FormatVersion = 1;
		const byte AttachmentBit = 1;
		const byte ExplicitIdBit = 2;

		ArrayBufferWriter<byte> _data;
		int _count;

		/// <summary>
		/// How many of the parent's direct children went into the table.
		/// </summary>
		public int TopLevelCount { get; private set; }

		/// <summary>
		/// Add a direct child and everything below it. <paramref name="index"/> is where it would have sat in
		/// the parent's "Children" array.
		/// </summary>
		public void Add( GameObject child, int index )
		{
			TopLevelCount++;
			Write( child, 0, index );
		}

		void Write( GameObject go, int parentEntry, int index )
		{
			_data ??= new ArrayBufferWriter<byte>( 2048 );

			var attachment = go.Flags == GameObjectFlags.Attachment;
			var explicitId = go.Id != DeriveGeneratedChildId( go.Parent.Id, attachment, go.Name );
			var nameLength = Encoding.UTF8.GetByteCount( go.Name );

			var span = _data.GetSpan( 1 + 5 * 3 + nameLength + 16 );
			var written = 0;

			span[written++] = (byte)((attachment ? AttachmentBit : 0) | (explicitId ? ExplicitIdBit : 0));
			written += WriteVarUInt( span[written..], (uint)parentEntry );
			written += WriteVarUInt( span[written..], (uint)index );
			written += WriteVarUInt( span[written..], (uint)nameLength );
			written += Encoding.UTF8.GetBytes( go.Name, span[written..] );

			if ( explicitId )
			{
				go.Id.TryWriteBytes( span[written..] );
				written += 16;
			}

			_data.Advance( written );

			var entry = ++_count;
			var childIndex = 0;

			for ( int i = 0; i < go.Children.Count; i++ )
			{
				var child = go.Children[i];
				if ( child is null || child.IsDestroyed ) continue;

				Write( child, entry, childIndex++ );
			}
		}

		public readonly string ToBase64()
		{
			Span<byte> header = stackalloc byte[6];
			header[0] = FormatVersion;
			var headerLength = 1 + WriteVarUInt( header[1..], (uint)_count );

			var bytes = new byte[headerLength + _data.WrittenCount];
			header[..headerLength].CopyTo( bytes );
			_data.WrittenSpan.CopyTo( bytes.AsSpan( headerLength ) );

			return Convert.ToBase64String( bytes );
		}

		/// <summary>
		/// Add the objects in <paramref name="table"/> to the JSON of the object that held it, so they
		/// deserialize exactly as their full JSON would have.
		/// </summary>
		public static void Expand( JsonObject node, Guid parentId, string table )
		{
			var data = Convert.FromBase64String( table );
			var reader = new SpanReader( data );

			if ( reader.ReadByte() != FormatVersion )
				throw new InvalidDataException( "Unknown generated children table version" );

			var count = (int)reader.ReadVarUInt();
			if ( count > data.Length )
				throw new InvalidDataException( "Generated children table is truncated" );

			var ids = new Guid[count];
			var nodes = new JsonObject[count];

			if ( node[JsonKeys.Children] is not JsonArray children )
			{
				children = new JsonArray();
				node[JsonKeys.Children] = children;
			}

			for ( int entry = 0; entry < count; entry++ )
			{
				var flags = reader.ReadByte();
				var parentEntry = (int)reader.ReadVarUInt();
				var index = (int)reader.ReadVarUInt();
				var name = reader.ReadString( (int)reader.ReadVarUInt() );

				if ( parentEntry > entry )
					throw new InvalidDataException( "Generated child listed before its parent" );

				var attachment = (flags & AttachmentBit) != 0;
				var objectParentId = parentEntry == 0 ? parentId : ids[parentEntry - 1];
				var id = (flags & ExplicitIdBit) != 0 ? reader.ReadGuid() : DeriveGeneratedChildId( objectParentId, attachment, name );

				var child = new JsonObject
				{
					[JsonKeys.Id] = id,
					[JsonKeys.Version] = GameObjectVersion,
					[JsonKeys.Flags] = (long)(attachment ? GameObjectFlags.Attachment : GameObjectFlags.Bone),
					[JsonKeys.Name] = name,
					[JsonKeys.Tags] = "",
					[JsonKeys.Enabled] = true,
					[JsonKeys.NetworkMode] = (int)NetworkMode.Snapshot,
					[JsonKeys.NetworkFlags] = (int)NetworkFlags.None,
					[JsonKeys.NetworkOrphaned] = (int)NetworkOrphaned.Destroy,
					[JsonKeys.AlwaysTransmit] = true,
					[JsonKeys.OwnerTransfer] = (int)OwnerTransfer.Fixed,
					[JsonKeys.Components] = new JsonArray(),
					[JsonKeys.Children] = new JsonArray()
				};

				ids[entry] = id;
				nodes[entry] = child;

				var siblings = parentEntry == 0 ? children : (JsonArray)nodes[parentEntry - 1][JsonKeys.Children];
				siblings.Insert( Math.Min( index, siblings.Count ), child );
			}
		}

		static int WriteVarUInt( Span<byte> span, uint value )
		{
			var i = 0;
			while ( value >= 0x80 )
			{
				span[i++] = (byte)(value | 0x80);
				value >>= 7;
			}

			span[i++] = (byte)value;
			return i;
		}

		ref struct SpanReader( ReadOnlySpan<byte> data )
		{
			readonly ReadOnlySpan<byte> _data = data;
			int _position;

			public byte ReadByte()
			{
				if ( _position >= _data.Length ) throw new InvalidDataException( "Generated children table is truncated" );
				return _data[_position++];
			}

			public uint ReadVarUInt()
			{
				uint value = 0;

				for ( var shift = 0; shift < 35; shift += 7 )
				{
					var b = ReadByte();
					value |= (uint)(b & 0x7F) << shift;
					if ( (b & 0x80) == 0 ) return value;
				}

				throw new InvalidDataException( "Bad varint in generated children table" );
			}

			public string ReadString( int length )
			{
				if ( length < 0 || length > _data.Length - _position ) throw new InvalidDataException( "Generated children table is truncated" );

				var value = Encoding.UTF8.GetString( _data.Slice( _position, length ) );
				_position += length;
				return value;
			}

			public Guid ReadGuid()
			{
				if ( _data.Length - _position < 16 ) throw new InvalidDataException( "Generated children table is truncated" );

				var value = new Guid( _data.Slice( _position, 16 ) );
				_position += 16;
				return value;
			}
		}
	}
}
