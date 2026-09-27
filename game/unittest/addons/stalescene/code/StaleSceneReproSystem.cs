using Sandbox;

namespace StaleSceneRepro;

/// <summary>
/// Provides a setting that can be edited in Project Settings > Systems > Global.
/// </summary>
public sealed class StaleSceneReproSystem : GameObjectSystem<StaleSceneReproSystem>
{
	private bool _attemptedLoad;

	public StaleSceneReproSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, LoadWorld, nameof( StaleSceneReproSystem ) );
	}

	[Property] public SceneFile World { get; set; }

	private void LoadWorld()
	{
		if ( _attemptedLoad || Scene.IsEditor )
			return;

		_attemptedLoad = true;

		var startScene = ResourceLibrary.Get<SceneFile>( "scenes/start.scene" );
		var source = Scene.Source as SceneFile;
		Log.Info( $"Stale scene repro: playing {Scene.Name} (source id {source?.Id}, start id {startScene?.Id})" );

		if ( source is null || startScene is null || source.Id != startScene.Id )
			return;

		if ( !World.IsValid() )
		{
			Log.Error( "Stale scene repro: World is missing or invalid. Assign it in Project Settings > Systems > Global." );
			return;
		}

		var path = World.ResourcePath;

		if ( Scene.Load( World ) )
			Log.Info( $"Stale scene repro: loaded {path}. Check that your latest scene changes appear." );
		else
			Log.Error( $"Stale scene repro: failed to load {path}." );
	}
}
