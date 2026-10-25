using System;
using Editor;
using Sandbox.Internal;
using System.Linq;
using System.Text.Json.Nodes;

namespace SceneTests.GameObjects;

/// <summary>
/// While game assemblies are unloaded or reloading, live components can have types that aren't in
/// <see cref="Game.TypeLibrary"/>. Serializing then leaves them out, so saving or undoing must not use
/// that output, or the components are deleted (issue #985).
/// </summary>
[TestClass]
[DoNotParallelize]
public class UnloadedComponentTypesTest : SceneTest
{
	TypeLibrary _oldTypeLibrary;

	[TestInitialize]
	public void SwapTypeLibrary()
	{
		_oldTypeLibrary = Game.TypeLibrary;
		Game.TypeLibrary = SharedTypeLibrary.EngineAndTests;
	}

	[TestCleanup]
	public void RestoreTypeLibrary()
	{
		Game.TypeLibrary = _oldTypeLibrary;
	}

	/// <summary>
	/// Game assemblies dropping out of the type library, like during a game reload.
	/// </summary>
	static void UnloadGameTypes() => Game.TypeLibrary = SharedTypeLibrary.Engine;
	static void ReloadGameTypes() => Game.TypeLibrary = SharedTypeLibrary.EngineAndTests;

	[TestMethod]
	public void SaveIsRefusedWhileComponentTypesAreUnloaded()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();
		var go = new GameObject( true, "Player" );
		go.Components.Create<GameComponent>().Value = 42;
		var session = new GameEditorSession( null, scene );

		try
		{
			UnloadGameTypes();
			Assert.IsNull( session.CreateResourceForSave(), "saving would have written the scene without GameComponent" );

			ReloadGameTypes();
			var file = (SceneFile)session.CreateResourceForSave();
			Assert.IsNotNull( file );
			var component = file.GameObjects.Single()["Components"].AsArray().Single();
			Assert.AreEqual( typeof( GameComponent ).FullName, component["__type"].GetValue<string>() );
			Assert.AreEqual( 42, component["Value"].GetValue<int>() );
		}
		finally { session.Destroy(); }
	}

	const string PrefabPath = "unloaded_component_types.prefab";

	static readonly string PrefabJson = $$"""
	{
		"__guid": "{{Guid.NewGuid()}}",
		"__version": 2,
		"Flags": 0,
		"Name": "Enemy",
		"Enabled": true,
		"Components": [
			{
				"__type": "{{typeof( GameComponent ).FullName}}",
				"__guid": "{{Guid.NewGuid()}}",
				"__enabled": true,
				"Value": 42
			}
		],
		"Children": []
	}
	""";

	static JsonObject PrefabComponentJson( PrefabFile file ) => file.RootObject["Components"].AsArray().Single().AsObject();

	[TestMethod]
	public void PrefabSaveIsRefusedWithoutTouchingThePrefabWhileComponentTypesAreUnloaded()
	{
		using var registration = Helpers.RegisterPrefabFromJson( PrefabPath, PrefabJson );
		var file = ResourceLibrary.Get<PrefabFile>( PrefabPath );

		// An instance in another scene, with an override, so a successful save has to refresh its patch
		var scene = new Scene();
		GameObject instance;
		using ( scene.Push() )
		{
			instance = GameObject.Clone( PrefabPath );
			instance.Components.Get<GameComponent>().Value = 7;
			instance.PrefabInstance.RefreshPatch();
		}

		var editing = PrefabScene.CreateForEditing();
		using var editingScope = editing.Push();
		Assert.IsTrue( editing.Load( file ) );
		var session = new GameEditorSession( null, editing );

		try
		{
			editing.Components.Get<GameComponent>().Value = 43;

			UnloadGameTypes();
			Assert.IsNull( session.CreateResourceForSave(), "saving would have written the prefab without GameComponent" );
			ReloadGameTypes();

			// The refused save mustn't have touched the shared prefab, its cache or its instances
			Assert.AreEqual( 42, PrefabComponentJson( file )["Value"].GetValue<int>() );
			Assert.AreEqual( 42, SceneUtility.GetPrefabScene( file ).Components.Get<GameComponent>().Value );
			Assert.AreEqual( 7, instance.Components.Get<GameComponent>().Value );

			Assert.AreSame( file, session.CreateResourceForSave() );
			Assert.AreEqual( 43, PrefabComponentJson( file )["Value"].GetValue<int>() );
			Assert.AreEqual( 43, SceneUtility.GetPrefabScene( file ).Components.Get<GameComponent>().Value );

			using ( scene.Push() )
			{
				instance.UpdateFromPrefab();
				Assert.AreEqual( 7, instance.Components.Get<GameComponent>().Value, "the instance's override was lost" );
			}
		}
		finally
		{
			session.Destroy();
			scene.Destroy();
		}
	}

	[TestMethod]
	public void DeletionUndoIsNotRecordedWhileComponentTypesAreUnloaded()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();
		var player = new GameObject( true, "Player" );
		player.Components.Create<GameComponent>().Value = 42;
		var other = new GameObject( true, "Other" );
		var session = new GameEditorSession( null, scene );

		try
		{
			UnloadGameTypes();
			using ( session.UndoScope( "Delete" ).WithGameObjectDestructions( other ).Push() )
				other.DestroyImmediate();

			ReloadGameTypes();
			var undone = session.UndoSystem.Undo();
			Assert.AreEqual( 42, scene.Directory.FindByGuid( player.Id )?.Components.Get<GameComponent>()?.Value, "undo restored a scene snapshot that was missing GameComponent" );
			Assert.IsFalse( undone, "an undo with an incomplete snapshot was recorded" );
		}
		finally { session.Destroy(); }
	}

	[TestMethod]
	public void ObjectUndoIsNotRecordedWhileComponentTypesAreUnloaded()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();
		var player = new GameObject( true, "Player" );
		player.Components.Create<GameComponent>().Value = 42;
		var session = new GameEditorSession( null, scene );

		try
		{
			UnloadGameTypes();
			using ( session.UndoScope( "Rename" ).WithGameObjectChanges( player, GameObjectUndoFlags.All ).Push() )
				player.Name = "Renamed";

			ReloadGameTypes();
			var undone = session.UndoSystem.Undo();
			Assert.AreEqual( 42, player.Components.Get<GameComponent>()?.Value, "undo restored an object snapshot that was missing GameComponent" );
			Assert.IsFalse( undone, "an undo with an incomplete snapshot was recorded" );
		}
		finally { session.Destroy(); }
	}

	public class GameComponent : Component
	{
		[Property] public int Value { get; set; }
	}
}
