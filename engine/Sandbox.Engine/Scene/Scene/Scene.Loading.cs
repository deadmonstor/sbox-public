using NativeEngine;

namespace Sandbox;

public partial class Scene : GameObject
{
	readonly List<LoadingContext> _loadingTasks = [];
	Task _loadingMainTask;

	internal void AddLoadingTask( LoadingContext loadingTask )
	{
		_loadingTasks.Add( loadingTask );
		LoadingScreen.UpdateLoadingTasks( _loadingTasks );
	}

	public void StartLoading()
	{
		if ( _loadingMainTask is not null )
			return;

		_loadingMainTask = WaitForLoading();
	}

	/// <summary>
	/// Return true if we're in an initial loading phase
	/// </summary>
	public bool IsLoading
	{
		get
		{
			_loadingTasks.RemoveAll( x => x.IsCompleted );

			if ( _loadingMainTask is null ) return false;
			if ( _loadingMainTask.IsCompleted ) return false;

			return true;
		}
	}

	/// <summary>
	/// Wait for scene loading to finish
	/// </summary>
	internal async Task WaitForLoading()
	{
		if ( _loadingMainTask is not null )
		{
			await _loadingMainTask;
			return;
		}

		try
		{
			var instance = IGameInstance.Current;

			Log.Info( $"Scene.WaitForLoading: starting. PendingTasks={_loadingTasks.Count}" );

			// wait one frame for all the tasks to build up
			await Task.Yield();

			// wait for all the loading tasks to finish
			while ( _loadingTasks.Count > 0 )
			{
				Log.Trace( $"Scene.WaitForLoading: waiting for {_loadingTasks.Count} tasks" );
				LoadingScreen.UpdateLoadingTasks( _loadingTasks );
				await Task.WhenAny( _loadingTasks.Select( x => x.Task ) );
				_loadingTasks.RemoveAll( x => x.IsCompleted );
			}

			// Remove all the tasks
			LoadingScreen.UpdateLoadingTasks( [] );

			if ( !IsValid )
			{
				Log.Warning( "Scene.WaitForLoading: scene became invalid before finishing load" );
				return;
			}

			// generated after everything is loaded
			if ( NavMesh.IsEnabled && this is not PrefabScene )
			{
				Log.Info( "Scene.WaitForLoading: Generating NavMesh.." );
				LoadingScreen.Subtitle = "Generating NavMesh..";

				await NavMesh.Load( PhysicsWorld );

				LoadingScreen.Subtitle = "Loading Finished..";
				Log.Info( "Scene.WaitForLoading: NavMesh generation finished" );
			}

			if ( !IsValid )
			{
				Log.Warning( "Scene.WaitForLoading: scene became invalid after navmesh" );
				return;
			}

			using ( Push() )
			{
				Log.Trace( "Scene.WaitForLoading: Entering Push scope to finalize load" );
				instance?.OnLoadingFinished();
				RunEvent<ISceneLoadingEvents>( x => x.AfterLoad( this ) );
				Log.Trace( "Scene.WaitForLoading: Ran AfterLoad events" );

				RunPendingStarts();

				if ( WantsSystemScene && this is not PrefabScene )
					g_pRenderDevice.FlushPipelineCache();

				var sceneInformation = Components.Get<SceneInformation>();
				var loadedSceneName = sceneInformation?.Title ?? Name ?? Source?.ResourcePath ?? "";
				Log.Info( $"Scene.WaitForLoading: Loaded scene title='{sceneInformation?.Title ?? "(null)"}', resolvedName='{loadedSceneName}' - notifying networking" );
				SceneNetworkSystem.OnLoadedScene( loadedSceneName );
			}
		}
		finally
		{
			_loadingMainTask = default;
			LoadingScreen.IsVisible = false;
			Log.Trace( "Scene.WaitForLoading: finished, loading screen hidden" );
		}
	}
}

public class LoadingContext
{
	/// <summary>
	/// The title of this loading task
	/// </summary>
	public string Title { get; set; }

	/// <summary>
	/// True if the task has completed
	/// </summary>
	public bool IsCompleted => Task?.IsCompleted ?? true;

	/// <summary>
	/// The task itself
	/// </summary>
	internal Task Task { get; set; }
}
