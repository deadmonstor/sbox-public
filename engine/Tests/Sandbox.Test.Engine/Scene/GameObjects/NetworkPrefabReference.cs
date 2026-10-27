using Sandbox.Internal;
using System;
using System.Text.Json.Nodes;

namespace SceneTests.GameObjects;

/// <summary>
/// Networked prefab instances are sent as a patch on their prefab. The receiver has to end up with exactly
/// the object the full JSON would have built.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NetworkPrefabReferenceTest : SceneTest
{
	TypeLibrary _oldTypeLibrary;

	static readonly GameObject.SerializeOptions NetworkOptions = new() { SingleNetworkObject = true, SkipNulls = true };
	static readonly GameObject.DeserializeOptions Create = new() { ClearAbsentFields = true };

	[TestInitialize]
	public void TestInitialize()
	{
		_oldTypeLibrary = Game.TypeLibrary;
		Game.TypeLibrary = SharedTypeLibrary.EngineAndTests;
	}

	[TestCleanup]
	public void Cleanup()
	{
		Game.TypeLibrary = _oldTypeLibrary;
	}

	/// <summary>
	/// A pawn with a body, a bone that stays as full JSON because something hangs off it, plain bones and
	/// attachments, a child the instance will lose and a networked child that's never part of the pawn's JSON.
	/// </summary>
	static IDisposable RegisterPawnPrefab( string path )
	{
		var scene = new Scene();
		using var scope = scene.Push();

		var pawn = scene.CreateObject();
		pawn.Name = "Pawn";
		pawn.NetworkMode = NetworkMode.Object;

		var body = new GameObject( pawn, true, "Body" );
		body.AddComponent<NetworkPrefabTestComponent>().Label = "body";

		var pelvis = new GameObject( body, true, "pelvis" ) { Flags = GameObjectFlags.Bone };
		new GameObject( pelvis, true, "spine" ) { Flags = GameObjectFlags.Bone };
		var ear = new GameObject( pelvis, true, "ear" );
		ear.AddComponent<NetworkPrefabTestComponent>().Label = "ear";
		new GameObject( body, true, "hand_R" ) { Flags = GameObjectFlags.Attachment };

		var hat = new GameObject( pawn, true, "Hat" );
		hat.AddComponent<NetworkPrefabTestComponent>().Label = "hat";

		new GameObject( pawn, true, "Weapon" ) { NetworkMode = NetworkMode.Object };

		var component = pawn.AddComponent<NetworkPrefabTestComponent>();
		component.Label = "prefab";
		component.Note = "prefab note";
		component.Target = body;

		return Helpers.RegisterPrefabFromJson( path, pawn.Serialize().ToJsonString() );
	}

	static GameObject Spawn( string path )
	{
		var scene = new Scene();
		using var scope = scene.Push();
		return GameObject.Clone( path );
	}

	/// <summary>
	/// Deserialize <paramref name="json"/> into a new object in a scene of its own, as a client would.
	/// </summary>
	static GameObject Receive( JsonObject json )
	{
		var scene = new Scene();
		using var scope = scene.Push();

		var copy = scene.CreateObject();
		copy.Deserialize( json, Create );
		return copy;
	}

	static JsonObject WriteReference( GameObject go, out JsonObject full )
	{
		var json = go.Serialize( NetworkOptions );
		full = json.DeepClone().AsObject();

		var reference = NetworkPrefabTemplate.TryWriteReference( go, json );
		Assert.IsNotNull( reference, "Expected the instance to be sent as a reference" );
		return reference;
	}

	static void AssertRoundTrip( GameObject source )
	{
		var reference = WriteReference( source, out var full );
		var copy = Receive( reference );
		var rebuilt = copy.Serialize( NetworkOptions );

		Assert.IsTrue( JsonNode.DeepEquals( full, rebuilt ), $"Rebuilt instance differs.\nSent:\n{full}\nRebuilt:\n{rebuilt}\nReference:\n{reference}" );
	}

	[TestMethod]
	public void FreshCloneNeedsNoIds()
	{
		const string path = "network_reference_fresh.prefab";
		using var registration = RegisterPawnPrefab( path );

		var pawn = Spawn( path );
		var reference = WriteReference( pawn, out _ );

		Assert.IsNull( reference[GameObject.JsonKeys.NetworkPrefabReference]["Ids"] );
		AssertRoundTrip( pawn );
	}

	[TestMethod]
	public void ChangedInstanceRoundTrips()
	{
		const string path = "network_reference_changed.prefab";
		using var registration = RegisterPawnPrefab( path );

		var pawn = Spawn( path );

		using ( pawn.Scene.Push() )
		{
			pawn.Name = "Pawn (Player 1)";
			pawn.LocalPosition = new Vector3( 10, 20, 30 );

			var body = pawn.Children.First( x => x.Name == "Body" );
			var ear = body.Children.First( x => x.Name == "pelvis" ).Children.First( x => x.Name == "ear" );

			// Changed, nulled out (so skipped), and pointed at another object in the instance
			var component = pawn.Components.Get<NetworkPrefabTestComponent>();
			component.Label = "changed";
			component.Note = null;
			component.Number = 5;
			component.Target = ear;

			pawn.Children.First( x => x.Name == "Hat" ).DestroyImmediate();

			var added = new GameObject( body, true, "Added" );
			added.AddComponent<NetworkPrefabTestComponent>().Target = pawn;
		}

		AssertRoundTrip( pawn );
	}

	[TestMethod]
	public void ReorderedAndMovedObjectsRoundTrip()
	{
		const string path = "network_reference_reordered.prefab";
		using var registration = RegisterPawnPrefab( path );

		var pawn = Spawn( path );

		using ( pawn.Scene.Push() )
		{
			var body = pawn.Children.First( x => x.Name == "Body" );
			var hat = pawn.Children.First( x => x.Name == "Hat" );

			// Moved somewhere the template doesn't have it, with a template object inside
			hat.SetParent( body.Children.First( x => x.Name == "pelvis" ) );

			// Added in between template objects, then template objects swapped around
			var first = new GameObject( pawn, true, "First" );
			first.AddComponent<NetworkPrefabTestComponent>().Label = "first";
			pawn.Children.Remove( first );
			pawn.Children.Insert( 0, first );

			pawn.Children.Remove( body );
			pawn.Children.Add( body );
		}

		AssertRoundTrip( pawn );
	}

	[TestMethod]
	public void ReceivedInstanceCanBeSentOn()
	{
		const string path = "network_reference_relay.prefab";
		using var registration = RegisterPawnPrefab( path );

		var pawn = Spawn( path );
		using ( pawn.Scene.Push() )
		{
			pawn.Components.Get<NetworkPrefabTestComponent>().Label = "relayed";
		}

		var copy = Receive( WriteReference( pawn, out _ ) );

		// A host passing on what a client spawned
		AssertRoundTrip( copy );
	}

	[TestMethod]
	public void CloneOfAnInstanceSendsTheIdsItCantDerive()
	{
		const string path = "network_reference_reclone.prefab";
		using var registration = RegisterPawnPrefab( path );

		var pawn = Spawn( path );

		GameObject clone;
		using ( pawn.Scene.Push() )
		{
			clone = pawn.Clone();
		}

		var reference = WriteReference( clone, out _ );

		Assert.IsNotNull( reference[GameObject.JsonKeys.NetworkPrefabReference]["Ids"] );
		AssertRoundTrip( clone );
	}

	[TestMethod]
	public void PrefabIdInTheInstanceIsSentInFull()
	{
		const string path = "network_reference_literal.prefab";
		using var registration = RegisterPawnPrefab( path );

		var prefab = GameObject.GetPrefab( path );
		var pawn = Spawn( path );

		// Would read as a prefab id on the other end and be turned into an instance id
		using ( pawn.Scene.Push() )
		{
			pawn.Components.Get<NetworkPrefabTestComponent>().Label = prefab.Children[0].Id.ToString();
		}

		Assert.IsNull( NetworkPrefabTemplate.TryWriteReference( pawn, pawn.Serialize( NetworkOptions ) ) );
	}
}

public class NetworkPrefabTestComponent : Component
{
	[Property] public string Label { get; set; } = "default";
	[Property] public string Note { get; set; }
	[Property] public int Number { get; set; }
	[Property] public GameObject Target { get; set; }
}
