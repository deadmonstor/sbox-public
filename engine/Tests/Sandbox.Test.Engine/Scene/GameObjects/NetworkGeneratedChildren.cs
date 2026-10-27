using Sandbox.Internal;
using System;
using System.Text.Json.Nodes;

namespace SceneTests.GameObjects;

/// <summary>
/// Networked serialization writes plain bone and attachment objects as a table on their parent.
/// Whatever it leaves out must come back exactly as the full JSON would have built it.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NetworkGeneratedChildrenTest : SceneTest
{
	TypeLibrary _oldTypeLibrary;

	static readonly GameObject.SerializeOptions NetworkOptions = new() { SingleNetworkObject = true, SkipNulls = true };

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

	static GameObject Generated( GameObject parent, GameObjectFlags kind, string name )
	{
		var go = new GameObject( parent, true, name ) { Flags = kind };
		go.SetDeterministicId( GameObject.DeriveGeneratedChildId( parent.Id, kind == GameObjectFlags.Attachment, name ) );
		return go;
	}

	/// <summary>
	/// A body with every case side by side: plain bones and attachments (left out), a plain bone that has to stay
	/// because a real object hangs under it, plain bones with ids that aren't derived, and bones that stop being
	/// plain because of a tag, a component, extra flags or being disabled.
	/// </summary>
	static GameObject BuildBody( Scene scene )
	{
		var root = scene.CreateObject();
		root.Name = "Root";
		root.NetworkMode = NetworkMode.Object;
		var body = new GameObject( root, true, "Body" );

		Generated( body, GameObjectFlags.Attachment, "hand_R" );

		var normal = new GameObject( body, true, "Normal" );
		normal.LocalPosition = new Vector3( 1, 2, 3 );

		var pelvis = Generated( body, GameObjectFlags.Bone, "pelvis" );
		Generated( pelvis, GameObjectFlags.Bone, "spine" );
		var ik = new GameObject( pelvis, true, "ik_target" );
		ik.LocalPosition = new Vector3( 4, 5, 6 );
		var thigh = Generated( pelvis, GameObjectFlags.Bone, "thigh" );
		Generated( thigh, GameObjectFlags.Bone, "calf" );

		new GameObject( body, true, "random_id_bone" ) { Flags = GameObjectFlags.Bone };

		var tagged = Generated( body, GameObjectFlags.Bone, "tagged" );
		tagged.Tags.Add( "keep" );

		var withComponent = Generated( body, GameObjectFlags.Bone, "with_component" );
		withComponent.Components.Create<RefreshTestComponent>();

		var procedural = Generated( body, GameObjectFlags.Bone, "procedural" );
		procedural.Flags |= GameObjectFlags.ProceduralBone;
		procedural.LocalPosition = new Vector3( 7, 8, 9 );

		var disabled = Generated( body, GameObjectFlags.Bone, "disabled" );
		disabled.Enabled = false;

		Generated( body, GameObjectFlags.Attachment, "muzzle" );

		return root;
	}

	static void AssertSameHierarchy( GameObject expected, GameObject actual, string path = "" )
	{
		path = $"{path}/{expected.Name}";

		Assert.AreEqual( expected.Id, actual.Id, $"{path}: id" );
		Assert.AreEqual( expected.Name, actual.Name, $"{path}: name" );
		Assert.AreEqual( expected.Enabled, actual.Enabled, $"{path}: enabled" );
		Assert.AreEqual( expected.LocalPosition, actual.LocalPosition, $"{path}: position" );
		Assert.AreEqual( expected.Components.Count, actual.Components.Count, $"{path}: components" );
		Assert.AreEqual( string.Join( ",", expected.Tags.TryGetAll( false ) ), string.Join( ",", actual.Tags.TryGetAll( false ) ), $"{path}: tags" );
		Assert.AreEqual( expected.Children.Count, actual.Children.Count, $"{path}: child count" );

		for ( int i = 0; i < expected.Children.Count; i++ )
		{
			AssertSameHierarchy( expected.Children[i], actual.Children[i], path );
		}
	}

	/// <summary>
	/// Deserialize <paramref name="json"/> into a new object in a scene of its own, as a client would.
	/// </summary>
	static GameObject Receive( JsonObject json, GameObject.DeserializeOptions options = default )
	{
		var scene = new Scene();
		using var scope = scene.Push();

		var copy = scene.CreateObject( false );
		copy.Deserialize( json, options );
		return copy;
	}

	static readonly GameObject.DeserializeOptions Create = new() { ClearAbsentFields = true };
	static readonly GameObject.DeserializeOptions Refresh = new() { IsRefreshing = true, IsNetworkRefresh = true, ClearAbsentFields = true };

	[TestMethod]
	public void NetworkRoundTripRebuildsGeneratedChildren()
	{
		var source = BuildBody( new Scene() );
		var copy = Receive( source.Serialize( NetworkOptions ), Create );

		AssertSameHierarchy( source, copy );
	}

	[TestMethod]
	public void SceneSnapshotRebuildsGeneratedChildren()
	{
		var source = BuildBody( new Scene() );
		source.NetworkMode = NetworkMode.Snapshot;

		var copy = Receive( source.Serialize( new GameObject.SerializeOptions { SceneForNetwork = true } ) );

		AssertSameHierarchy( source, copy );
	}

	[TestMethod]
	public void NetworkRefreshKeepsAndPrunesGeneratedChildren()
	{
		var source = BuildBody( new Scene() );
		var copy = Receive( source.Serialize( NetworkOptions ), Create );

		// The host's calf goes away; the refresh has to remove it and keep everything else as it was
		using ( source.Scene.Push() )
		{
			source.Children[0].Children.First( x => x.Name == "pelvis" ).Children.First( x => x.Name == "thigh" ).Children[0].DestroyImmediate();
		}

		var refresh = source.Serialize( NetworkOptions );

		using ( copy.Scene.Push() )
		{
			copy.Deserialize( refresh, Refresh );
		}

		AssertSameHierarchy( source, copy );
	}

	[TestMethod]
	public void DeserializingTheSameJsonTwiceDoesNotDuplicateGeneratedChildren()
	{
		var source = BuildBody( new Scene() );
		var json = source.Serialize( NetworkOptions );
		var copy = Receive( json, Create );

		using ( copy.Scene.Push() )
		{
			copy.Deserialize( json, Refresh );
		}

		AssertSameHierarchy( source, copy );
	}

	[TestMethod]
	public void ClonedBonesAndAttachmentsGetDerivedIds()
	{
		var source = BuildBody( new Scene() );

		using var scope = source.Scene.Push();
		var clone = source.Clone();

		var body = clone.Children[0];
		var pelvis = body.Children.First( x => x.Name == "pelvis" );
		var thigh = pelvis.Children.First( x => x.Name == "thigh" );

		Assert.AreEqual( GameObject.DeriveGeneratedChildId( body.Id, true, "hand_R" ), body.Children.First( x => x.Name == "hand_R" ).Id );
		Assert.AreEqual( GameObject.DeriveGeneratedChildId( body.Id, false, "pelvis" ), pelvis.Id );
		Assert.AreEqual( GameObject.DeriveGeneratedChildId( thigh.Id, false, "calf" ), thigh.Children[0].Id );

		// Not a plain bone or attachment: the clone keeps a fresh id of its own
		var procedural = body.Children.First( x => x.Name == "procedural" );
		Assert.AreNotEqual( GameObject.DeriveGeneratedChildId( body.Id, false, "procedural" ), procedural.Id );
	}

	[TestMethod]
	public void TruncatedTableIsRejected()
	{
		var json = BuildBody( new Scene() ).Serialize( NetworkOptions );
		var body = json["Children"][0].AsObject();
		var table = Convert.FromBase64String( body[GameObject.JsonKeys.GeneratedChildren].GetValue<string>() );
		body[GameObject.JsonKeys.GeneratedChildren] = Convert.ToBase64String( table.AsSpan( 0, table.Length / 2 ) );

		Assert.ThrowsException<System.IO.InvalidDataException>( () => Receive( json ) );
	}
}
