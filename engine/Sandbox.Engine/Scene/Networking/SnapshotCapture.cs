using Sandbox.Network;
using System.Text.Json.Nodes;

namespace Sandbox;

/// <summary>
/// Owns the data captured from a scene. Materialization only reads detached JSON and bytes;
/// scene callbacks, sync tables and blob serializers have already run on the main thread.
/// </summary>
internal sealed class SnapshotCapture
{
	/// <summary>
	/// A networked object as captured on the main thread. Immutable, so captures in the same batch
	/// and their encode workers can share it.
	/// </summary>
	internal sealed record CapturedObject( ObjectCreateMsg Message, string Json, BlobDataSerializer.CapturedBlob[] Blobs );

	internal SnapshotMsg Snapshot = SnapshotMsg.Create();
	internal Lazy<string> SceneJson;
	internal Lazy<byte[]> SceneBlobs;
	readonly List<string> objectJson = new();
	readonly List<BlobDataSerializer.CapturedBlob[]> objectBlobs = new();

	/// <summary>
	/// Objects already captured by this batch. Captures created from a shared one serialize each object
	/// once for the whole batch, which is only valid while nothing runs between them.
	/// </summary>
	readonly Dictionary<NetworkObject, CapturedObject> batch;

	internal SnapshotCapture()
	{
		batch = new();
	}

	/// <summary>
	/// A capture in the same batch as <paramref name="shared"/>, taken without anything running in between.
	/// </summary>
	internal SnapshotCapture( SnapshotCapture shared )
	{
		batch = shared.batch;
	}

	internal void AddObject( JsonNode node, BlobDataSerializer.BlobContext blobs = null )
	{
		objectJson.Add( Detach( node ) );
		objectBlobs.Add( blobs?.Detach() );
	}

	/// <summary>
	/// The object's capture from earlier in this batch, if there is one.
	/// </summary>
	internal bool TryGetCaptured( NetworkObject target, out CapturedObject captured ) => batch.TryGetValue( target, out captured );

	/// <summary>
	/// Capture an object for this batch and add it to this snapshot.
	/// </summary>
	internal CapturedObject AddObject( NetworkObject target, ObjectCreateMsg message, JsonNode node, BlobDataSerializer.BlobContext blobs )
	{
		var captured = new CapturedObject( message, Detach( node ), blobs?.Detach() );
		batch[target] = captured;
		Add( captured );
		return captured;
	}

	/// <summary>
	/// Add an object captured earlier in this batch.
	/// </summary>
	internal void Add( CapturedObject captured )
	{
		objectJson.Add( captured.Json );
		objectBlobs.Add( captured.Blobs );
	}

	internal byte[][] Encode<T>( BytePack serializer, Func<SnapshotMsg, T> envelope, NetFlags flags )
	{
		var stream = ByteStream.Create( 1024 );
		try
		{
			stream.Write( InternalMessageType.Packed );
			serializer.SerializeTo( ref stream, envelope( Materialize() ) );
			var packet = Connection.EncodePacket( stream );
			var packets = Connection.CreatePackets( packet.Span, flags );
			packet.Release();
			return packets;
		}
		finally
		{
			stream.Dispose();
		}
	}

	internal SnapshotMsg Materialize()
	{
		var result = new SnapshotMsg
		{
			Time = Snapshot.Time,
			SceneData = SceneJson.Value,
			BlobData = SceneBlobs?.Value ?? Snapshot.BlobData,
			GameObjectSystems = Snapshot.GameObjectSystems,
			NetworkObjects = new List<object>( Snapshot.NetworkObjects.Count )
		};

		for ( int i = 0; i < Snapshot.NetworkObjects.Count; i++ )
		{
			var message = (ObjectCreateMsg)Snapshot.NetworkObjects[i];
			message.JsonData = objectJson[i];
			if ( objectBlobs[i] is { } blobs )
			{
				message.BlobData = BlobDataSerializer.PackBlobs( blobs );
			}
			result.NetworkObjects.Add( message );
		}

		return result;
	}

	// Custom JSON populators can return nodes they still own, or JsonValue<T> with a
	// deferred converter. Write the JSON now, so those converters run during capture and
	// later changes to the scene can't leak in. Cheaper than cloning the tree.
	internal static string Detach( JsonNode node ) => node?.ToJsonString();
}
