using Facepunch.ActionGraphs;
using Sandbox.Hashing;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sandbox;

/// <summary>
/// What a fresh instance of a prefab serializes to for the network. A networked prefab instance is sent as a
/// patch on this instead of its full JSON, and both ends build it from their own copy of the prefab.
/// <para>
/// Ids in the patch are the prefab's. Cloning a prefab derives each id from the new root (see
/// <see cref="CloneContext.DerivePrefabCloneId"/>, and <see cref="GameObject.DeriveGeneratedChildId"/> for bones
/// and attachments), so the receiver works the instance ids out from the root id and only ids that were
/// changed since need sending.
/// </para>
/// <para>
/// The patch has an entry for each GameObject or component of the template that's still where the template
/// has it but differs, keyed by its prefab id:
/// <code>
/// { "set": { property: value }, "unset": [ property ],
///   "Components" / "Children": { "remove": [ id ], "add": [ full JSON ], "order": [ id ] } }
/// </code>
/// Anything the template doesn't have in that spot comes whole in "add", and "order" is only there when the
/// template order, without the removed ones and with the added ones after, isn't right.
/// </para>
/// </summary>
internal sealed class NetworkPrefabTemplate
{
	// Matches NetworkObject's create options, apart from the bone table ids, see AssumeDerivedGeneratedIds
	static readonly GameObject.SerializeOptions SerializeOptions = new() { SingleNetworkObject = true, SkipNulls = true, AssumeDerivedGeneratedIds = true };

	const string Set = "set";
	const string Unset = "unset";
	const string Add = "add";
	const string Remove = "remove";
	const string Order = "order";

	enum Kind : byte
	{
		Other,
		Bone,
		Attachment
	}

	/// <summary>
	/// A GameObject or component in the template. <see cref="Parent"/> indexes the GameObject holding it.
	/// </summary>
	readonly record struct Entry( Guid Id, int Parent, Kind Kind, string Name );

	readonly string _path;
	readonly JsonObject _json;
	readonly ulong _hash;

	// Parents come before their children, the root is first
	readonly List<Entry> _entries = new();
	readonly Dictionary<Guid, int> _index = new();
	readonly Dictionary<Guid, JsonObject> _nodes = new();

	// Each property of each GameObject and component as JSON, to compare instances against
	readonly Dictionary<Guid, Dictionary<string, byte[]>> _values = new();

	// The "Children" or "Components" array each GameObject or component is in
	readonly Dictionary<Guid, JsonArray> _containers = new();

	bool _warnedMismatch;

	NetworkPrefabTemplate( string path, JsonObject json )
	{
		_path = path;
		_json = json;
		_hash = XxHash3.HashToUInt64( Encoding.UTF8.GetBytes( json.ToJsonString() ) );
	}

	internal static NetworkPrefabTemplate Create( PrefabCacheScene prefab )
	{
		if ( prefab.Source is not { } source ) return null;

		JsonObject json;
		using ( ActionGraph.PushSerializationOptions( source.SerializationOptions with { ForceUpdateCached = prefab.IsEditor } ) )
		using ( BlobDataSerializer.Suppress() )
		{
			json = prefab.SerializeStandard( SerializeOptions );
		}

		if ( json is null ) return null;

		var template = new NetworkPrefabTemplate( source.ResourcePath, json );
		return template.AddGameObject( json, -1, null ) ? template : null;
	}

	bool AddGameObject( JsonObject node, int parent, JsonArray container )
	{
		var kind = Kind.Other;
		if ( parent >= 0 && node[GameObject.JsonKeys.Flags] is JsonValue flagsValue )
		{
			var flags = (GameObjectFlags)flagsValue.GetValue<long>();
			if ( flags == GameObjectFlags.Bone ) kind = Kind.Bone;
			if ( flags == GameObjectFlags.Attachment ) kind = Kind.Attachment;
		}

		var self = _entries.Count;
		if ( !AddEntry( node, parent, kind, node[GameObject.JsonKeys.Name]?.GetValue<string>() ?? "", container, true ) ) return false;

		if ( node[GameObject.JsonKeys.Components] is JsonArray components )
		{
			foreach ( var component in components )
			{
				if ( component is JsonObject componentNode && !AddEntry( componentNode, self, Kind.Other, null, components, false ) ) return false;
			}
		}

		if ( node[GameObject.JsonKeys.Children] is JsonArray children )
		{
			foreach ( var child in children )
			{
				if ( child is JsonObject childNode && !AddGameObject( childNode, self, children ) ) return false;
			}
		}

		return true;
	}

	bool AddEntry( JsonObject node, int parent, Kind kind, string name, JsonArray container, bool isGameObject )
	{
		if ( !TryGetId( node[GameObject.JsonKeys.Id], out var id, out _ ) ) return false;
		if ( !_index.TryAdd( id, _entries.Count ) ) return false;

		_entries.Add( new Entry( id, parent, kind, name ) );
		_nodes[id] = node;
		if ( container is not null ) _containers[id] = container;

		var values = new Dictionary<string, byte[]>( node.Count );
		foreach ( var (key, value) in node )
		{
			if ( isGameObject && key is GameObject.JsonKeys.Children or GameObject.JsonKeys.Components ) continue;
			values[key] = Write( value ).ToArray();
		}

		_values[id] = values;
		return true;
	}

	[ThreadStatic] static ArrayBufferWriter<byte> t_buffer;
	[ThreadStatic] static Utf8JsonWriter t_writer;

	/// <summary>
	/// <paramref name="value"/> as JSON, valid until the next call.
	/// </summary>
	static ReadOnlySpan<byte> Write( JsonNode value )
	{
		t_buffer ??= new ArrayBufferWriter<byte>( 1024 );
		t_buffer.ResetWrittenCount();

		if ( t_writer is null ) t_writer = new Utf8JsonWriter( t_buffer );
		else t_writer.Reset( t_buffer );

		if ( value is null ) t_writer.WriteNullValue();
		else value.WriteTo( t_writer );

		t_writer.Flush();
		return t_buffer.WrittenSpan;
	}

	/// <summary>
	/// The id a clone of the prefab gives entry <paramref name="entry"/>, given the ids of the entries before it.
	/// </summary>
	Guid DeriveId( int entry, Guid[] ids )
	{
		var e = _entries[entry];

		return e.Kind switch
		{
			Kind.Bone => GameObject.DeriveGeneratedChildId( ids[e.Parent], false, e.Name ),
			Kind.Attachment => GameObject.DeriveGeneratedChildId( ids[e.Parent], true, e.Name ),
			_ => PrefabInstanceData.DeriveInstanceGuid( ids[0], e.Id )
		};
	}

	/// <summary>
	/// Write <paramref name="json"/>, the network serialization of <paramref name="go"/>, as a patch on its prefab.
	/// Returns null if it has to be sent in full. <paramref name="json"/> is changed either way.
	/// </summary>
	internal static JsonObject TryWriteReference( GameObject go, JsonObject json )
	{
		// The editor can change a prefab after the other end loaded it
		if ( Application.IsEditor ) return null;

		if ( !go.IsOutermostPrefabInstanceRoot ) return null;

		var instance = go.PrefabInstance;
		if ( GameObject.GetPrefab( instance.PrefabSource ) is not PrefabCacheScene prefab ) return null;

		// A package mounted mid-game might not have reached the other end yet
		if ( prefab.Source?.Package is not null ) return null;

		var template = prefab.NetworkTemplate;
		if ( template is null ) return null;

		var lookup = instance.InstanceToPrefabLookup;
		if ( lookup.Count == 0 ) return null;

		var count = template._entries.Count;
		var actual = new Guid[count];
		var toEntry = new Dictionary<Guid, int>( count ) { [go.Id] = 0 };
		actual[0] = go.Id;

		foreach ( var (instanceId, prefabId) in lookup )
		{
			if ( !template._index.TryGetValue( prefabId, out var entry ) || entry == 0 ) continue;
			if ( !toEntry.TryAdd( instanceId, entry ) ) return null;

			actual[entry] = instanceId;
		}

		var found = new List<FoundId>( count );
		FindIds( json, found );

		var referenced = new bool[count];
		foreach ( var item in found )
		{
			if ( toEntry.TryGetValue( item.Id, out var entry ) )
			{
				referenced[entry] = true;
			}
			else if ( template._index.ContainsKey( item.Id ) )
			{
				// A prefab id the other end would turn into an instance id
				return null;
			}
		}

		// Into prefab ids, so whatever the instance didn't change matches the template
		foreach ( var item in found )
		{
			if ( toEntry.TryGetValue( item.Id, out var entry ) )
			{
				item.ReplaceWith( template._entries[entry].Id );
			}
		}

		// Ids the other end can't derive. Only matters for the ones the JSON mentions.
		var resolved = new Guid[count];
		resolved[0] = go.Id;
		JsonObject ids = null;

		for ( int i = 1; i < count; i++ )
		{
			resolved[i] = template.DeriveId( i, resolved );
			if ( !referenced[i] || actual[i] == resolved[i] ) continue;

			resolved[i] = actual[i];
			ids ??= new JsonObject();
			ids[template._entries[i].Id.ToString()] = actual[i];
		}

		var patch = new JsonObject();
		template.DiffGameObject( json, template._json, patch );

		var reference = new JsonObject
		{
			["Path"] = template._path,
			["Hash"] = template._hash,
		};

		if ( patch.Count > 0 ) reference["Patch"] = patch;
		if ( ids is not null ) reference["Ids"] = ids;

		return new JsonObject
		{
			[GameObject.JsonKeys.Id] = go.Id,
			[GameObject.JsonKeys.NetworkPrefabReference] = reference
		};
	}

	/// <summary>
	/// Turn a reference written by <see cref="TryWriteReference"/> back into the full JSON.
	/// <paramref name="prefabToInstance"/> gets the prefab to instance id mapping it was rebuilt with.
	/// </summary>
	internal static JsonObject Read( JsonObject node, out Dictionary<Guid, Guid> prefabToInstance )
	{
		if ( node[GameObject.JsonKeys.NetworkPrefabReference] is not JsonObject reference )
			throw new InvalidDataException( "Networked prefab reference isn't an object" );

		var path = reference["Path"]?.GetValue<string>();
		if ( string.IsNullOrEmpty( path ) || GameObject.GetPrefab( path ) is not PrefabCacheScene prefab || prefab.NetworkTemplate is not { } template )
			throw new InvalidDataException( $"Networked prefab \"{path}\" couldn't be loaded" );

		if ( reference["Hash"]?.GetValue<ulong>() != template._hash && !template._warnedMismatch )
		{
			template._warnedMismatch = true;
			Log.Warning( $"Networked prefab \"{path}\" differs from the sender's, its instances might not match" );
		}

		var count = template._entries.Count;
		var resolved = new Guid[count];
		resolved[0] = node[GameObject.JsonKeys.Id].Deserialize<Guid>();

		if ( reference["Ids"] is JsonObject ids )
		{
			foreach ( var (prefabId, instanceId) in ids )
			{
				if ( Guid.TryParse( prefabId, out var id ) && template._index.TryGetValue( id, out var entry ) && entry > 0 )
				{
					resolved[entry] = instanceId.Deserialize<Guid>();
				}
			}
		}

		for ( int i = 1; i < count; i++ )
		{
			if ( resolved[i] == Guid.Empty ) resolved[i] = template.DeriveId( i, resolved );
		}

		var json = template._json.DeepClone().AsObject();

		if ( reference["Patch"] is JsonObject patch )
		{
			Apply( json, patch );
		}

		var found = new List<FoundId>( count );
		FindIds( json, found );

		foreach ( var item in found )
		{
			if ( template._index.TryGetValue( item.Id, out var entry ) )
			{
				item.ReplaceWith( resolved[entry] );
			}
		}

		prefabToInstance = new Dictionary<Guid, Guid>( count );
		for ( int i = 0; i < count; i++ )
		{
			prefabToInstance[template._entries[i].Id] = resolved[i];
		}

		return json;
	}

	/// <summary>
	/// Add what <paramref name="node"/> changed from <paramref name="original"/>, and the same for everything
	/// under it, to <paramref name="patch"/>. Values that go in the patch are taken out of <paramref name="node"/>.
	/// </summary>
	void DiffGameObject( JsonObject node, JsonObject original, JsonObject patch )
	{
		var change = DiffProperties( node, IdOf( original ), true );
		DiffContainer( node, original, GameObject.JsonKeys.Components, patch, ref change );
		DiffContainer( node, original, GameObject.JsonKeys.Children, patch, ref change );

		if ( change is not null ) patch[IdOf( original ).ToString()] = change;
	}

	JsonObject DiffProperties( JsonObject node, Guid id, bool isGameObject )
	{
		var original = _values[id];
		List<string> changed = null;
		JsonArray unset = null;

		// Same text, not just equal values, so what the other end rebuilds reads exactly the same
		for ( int i = 0; i < node.Count; i++ )
		{
			var (key, value) = node.GetAt( i );
			if ( isGameObject && key is GameObject.JsonKeys.Children or GameObject.JsonKeys.Components ) continue;
			if ( original.TryGetValue( key, out var originalValue ) && Write( value ).SequenceEqual( originalValue ) ) continue;

			(changed ??= new()).Add( key );
		}

		// The serializer leaves nulls out, so a property the template has might just not be there
		foreach ( var (key, _) in original )
		{
			if ( node.ContainsKey( key ) ) continue;

			(unset ??= new()).Add( key );
		}

		if ( changed is null && unset is null ) return null;

		var change = new JsonObject();

		if ( changed is not null )
		{
			var set = new JsonObject();
			foreach ( var key in changed )
			{
				node.Remove( key, out var value );
				set[key] = value;
			}

			change[Set] = set;
		}

		if ( unset is not null ) change[Unset] = unset;
		return change;
	}

	void DiffContainer( JsonObject node, JsonObject original, string key, JsonObject patch, ref JsonObject change )
	{
		var items = node[key] as JsonArray;
		var originals = original[key] as JsonArray;
		var isChildren = key == GameObject.JsonKeys.Children;

		HashSet<Guid> kept = null;
		List<JsonNode> added = null;
		var ordered = true;
		var nextOriginal = 0;

		if ( items is not null )
		{
			foreach ( var item in items )
			{
				if ( item is JsonObject itemNode && TryGetId( itemNode[GameObject.JsonKeys.Id], out var id, out _ ) && originals is not null
					&& _containers.TryGetValue( id, out var container ) && container == originals )
				{
					// Still where the template has it, so only send what changed
					var originalNode = _nodes[id];
					if ( isChildren )
					{
						DiffGameObject( itemNode, originalNode, patch );
					}
					else if ( DiffProperties( itemNode, id, false ) is { } componentChange )
					{
						patch[id.ToString()] = componentChange;
					}

					(kept ??= new()).Add( id );

					// Kept ones have to come in template order, and before anything added
					if ( added is not null ) ordered = false;
					while ( ordered && nextOriginal < originals.Count && IdOf( originals[nextOriginal] ) != id ) nextOriginal++;
					if ( nextOriginal == originals.Count ) ordered = false;
					nextOriginal++;
				}
				else
				{
					(added ??= new()).Add( item );
				}
			}
		}

		JsonArray removed = null;
		if ( originals is not null )
		{
			foreach ( var originalItem in originals )
			{
				var id = IdOf( originalItem );
				if ( kept is null || !kept.Contains( id ) ) (removed ??= new()).Add( id );
			}
		}

		if ( removed is null && added is null && ordered ) return;

		var containerChange = new JsonObject();

		if ( removed is not null ) containerChange[Remove] = removed;

		// Before the added ones are taken out
		if ( !ordered )
		{
			var order = new JsonArray();
			foreach ( var item in items ) order.Add( IdOf( item ) );
			containerChange[Order] = order;
		}

		if ( added is not null )
		{
			var add = new JsonArray();
			foreach ( var item in added )
			{
				items.Remove( item );
				add.Add( item );
			}

			containerChange[Add] = add;
		}

		change ??= new JsonObject();
		change[key] = containerChange;
	}

	/// <summary>
	/// Apply a patch written by <see cref="DiffGameObject"/> to a copy of the template.
	/// </summary>
	static void Apply( JsonObject json, JsonObject patch )
	{
		var nodes = new Dictionary<Guid, JsonObject>();
		Index( json, nodes );

		foreach ( var (idText, changeNode) in patch )
		{
			if ( !Guid.TryParse( idText, out var id ) || !nodes.TryGetValue( id, out var node ) || changeNode is not JsonObject change )
				throw new InvalidDataException( $"Networked prefab patch targets unknown object {idText}" );

			if ( change[Set] is JsonObject set )
			{
				foreach ( var key in set.Select( x => x.Key ).ToArray() )
				{
					set.Remove( key, out var value );
					node[key] = value;
				}
			}

			if ( change[Unset] is JsonArray unset )
			{
				foreach ( var key in unset ) node.Remove( key.GetValue<string>() );
			}

			ApplyContainer( node, change, GameObject.JsonKeys.Components );
			ApplyContainer( node, change, GameObject.JsonKeys.Children );
		}
	}

	static void ApplyContainer( JsonObject node, JsonObject change, string key )
	{
		if ( change[key] is not JsonObject containerChange ) return;

		if ( node[key] is not JsonArray items )
		{
			items = new JsonArray();
			node[key] = items;
		}

		if ( containerChange[Remove] is JsonArray remove )
		{
			foreach ( var removedId in remove )
			{
				var id = removedId.Deserialize<Guid>();
				var item = items.FirstOrDefault( x => IdOf( x ) == id );
				if ( item is not null ) items.Remove( item );
			}
		}

		if ( containerChange[Add] is JsonArray add )
		{
			foreach ( var item in add.ToArray() )
			{
				add.Remove( item );
				items.Add( item );
			}
		}

		if ( containerChange[Order] is JsonArray order )
		{
			var byId = new Dictionary<Guid, JsonNode>( items.Count );
			foreach ( var item in items ) byId[IdOf( item )] = item;

			if ( order.Count != byId.Count )
				throw new InvalidDataException( "Networked prefab patch order doesn't match the objects" );

			items.Clear();
			foreach ( var orderedId in order )
			{
				if ( !byId.TryGetValue( orderedId.Deserialize<Guid>(), out var item ) )
					throw new InvalidDataException( "Networked prefab patch order doesn't match the objects" );

				items.Add( item );
			}
		}
	}

	static void Index( JsonObject go, Dictionary<Guid, JsonObject> nodes )
	{
		if ( TryGetId( go[GameObject.JsonKeys.Id], out var id, out _ ) ) nodes[id] = go;

		if ( go[GameObject.JsonKeys.Components] is JsonArray components )
		{
			foreach ( var component in components )
			{
				if ( component is JsonObject componentNode && TryGetId( componentNode[GameObject.JsonKeys.Id], out var componentId, out _ ) ) nodes[componentId] = componentNode;
			}
		}

		if ( go[GameObject.JsonKeys.Children] is JsonArray children )
		{
			foreach ( var child in children )
			{
				if ( child is JsonObject childNode ) Index( childNode, nodes );
			}
		}
	}

	static Guid IdOf( JsonNode node ) => node is JsonObject obj && TryGetId( obj[GameObject.JsonKeys.Id], out var id, out _ ) ? id : default;

	readonly record struct FoundId( JsonValue Value, Guid Id, bool IsString )
	{
		/// <summary>
		/// Swap in another id, written the same way.
		/// </summary>
		public void ReplaceWith( Guid id )
		{
			if ( IsString ) Value.ReplaceWith( id.ToString() );
			else Value.ReplaceWith( id );
		}
	}

	/// <summary>
	/// Every value in <paramref name="node"/> that holds a guid. Strings only count in the form guids are
	/// written in, so swapping one for another guid writes the same kind of text.
	/// </summary>
	static void FindIds( JsonNode node, List<FoundId> found )
	{
		switch ( node )
		{
			case JsonObject obj:
				for ( int i = 0; i < obj.Count; i++ ) FindIds( obj.GetAt( i ).Value, found );
				break;

			case JsonArray array:
				for ( int i = 0; i < array.Count; i++ ) FindIds( array[i], found );
				break;

			case JsonValue value when TryGetId( value, out var id, out var isString ):
				found.Add( new FoundId( value, id, isString ) );
				break;
		}
	}

	static bool TryGetId( JsonNode node, out Guid id, out bool isString )
	{
		id = default;
		isString = false;
		if ( node is not JsonValue value ) return false;

		Span<char> canonical = stackalloc char[36];

		// Parsed JSON, read it in place rather than making a string of every value
		if ( value.TryGetValue( out JsonElement element ) )
		{
			if ( element.ValueKind != JsonValueKind.String || !element.TryGetGuid( out id ) ) return false;

			isString = true;
			return id.TryFormat( canonical, out _ ) && element.ValueEquals( canonical );
		}

		if ( value.TryGetValue( out string text ) )
		{
			isString = true;
			return text.Length == 36 && Guid.TryParseExact( text, "D", out id ) && id.TryFormat( canonical, out _ ) && text.AsSpan().SequenceEqual( canonical );
		}

		return value.TryGetValue( out id );
	}
}
