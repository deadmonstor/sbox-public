using Sandbox.Engine;

namespace Sandbox;

public static partial class Game
{
	static bool _loggedMissingCameraWhileLoading;
	static bool _loggedMissingCameraWhilePlaying;
	static bool _loggedInvalidCameraWhileLoading;
	static bool _loggedInvalidCameraWhilePlaying;
	static Guid? _loggedSceneRenderId;
	static bool _isPlaying = true;

	/// <summary>
	/// Indicates whether the game is currently running and actively playing a scene.
	/// </summary>
	public static bool IsPlaying
	{
		get => _isPlaying;
		internal set
		{
			if ( _isPlaying == value )
				return;

			Log.Info( $"Game.IsPlaying: {_isPlaying} -> {value} (activeScene={(ActiveScene is null ? "(null)" : ActiveScene.Id.ToString())})" );
			_isPlaying = value;
		}
	}

	/// <summary>
	/// Indicates whether the game is currently paused.
	/// </summary>
	public static bool IsPaused { get; set; }

	/// <summary>
	/// The current scene that is being played.
	/// </summary>
	public static Scene ActiveScene
	{
		get => GlobalContext.Current.ActiveScene;
		internal set => GlobalContext.Current.ActiveScene = value;
	}

	/// <summary>
	/// Change the active scene and optionally bring all connected clients to
	/// the new scene (broadcast the scene change.) If we're in a networking
	/// session, then only the host can change the scene.
	/// </summary>
	/// <param name="options">The <see cref="SceneLoadOptions"/> to use which also specifies which scene to load.</param>
	/// <returns>Whether the scene was changed successfully.</returns>
	public static bool ChangeScene( SceneLoadOptions options )
	{
		if ( !Networking.IsHost )
			return false;

		// We don't want to send any networked messages to do with deletion or creation
		// of GameObjects here. Because the client will destroy their scene locally
		// anyway. This saves us sending a message for potentially 100s of objects.
		using ( SceneNetworkSystem.SuppressSpawnMessages() )
		{
			using ( SceneNetworkSystem.SuppressDestroyMessages() )
			{
				if ( !ActiveScene.Load( options ) )
					return false;
			}
		}

		// Conna: We want to send a new snapshot to every client.
		SceneNetworkSystem.Instance?.LoadSceneBroadcast( options );
		return true;
	}

	internal static void Render( SwapChainHandle_t swapChain )
	{
		Log.Trace( $"Game.Render: enter isPlaying={IsPlaying} activeScene={(ActiveScene is null ? "(null)" : ActiveScene.Id.ToString())} isLoading={(ActiveScene?.IsLoading ?? false)} loadingScreen={LoadingScreen.IsVisible} connecting={Networking.IsConnecting}" );

		// IToolsDll.OnRender handles the case where game is not playing (render from editor scene)
		if ( !IsPlaying )
		{
			Log.Trace( "Game.Render: exit (not playing)" );
			return;
		}

		// Could be loading still
		if ( ActiveScene is null )
		{
			Log.Trace( "Game.Render: exit (no active scene)" );
			return;
		}

		if ( _loggedSceneRenderId != ActiveScene.Id )
		{
			_loggedSceneRenderId = ActiveScene.Id;
			Log.Info( $"Game.Render: scene summary sceneId={ActiveScene.Id} name='{ActiveScene.Name}' rootChildren={ActiveScene.Children.Count} gameObjects={ActiveScene.Directory.GameObjectCount} components={ActiveScene.Directory.ComponentCount} cameras={ActiveScene.GetAllComponents<CameraComponent>().Count()} modelRenderers={ActiveScene.GetAllComponents<ModelRenderer>().Count()} skinnedModelRenderers={ActiveScene.GetAllComponents<SkinnedModelRenderer>().Count()} lights={ActiveScene.GetAllComponents<Light>().Count() + ActiveScene.GetAllComponents<AmbientLight>().Count()} sceneWorld={ActiveScene.HasSceneWorld} physicsBodies={ActiveScene.PhysicsWorld.BodyCount}" );
		}

		if ( ActiveScene.IsLoading || LoadingScreen.IsVisible || Networking.IsConnecting )
		{
			if ( ActiveScene.Camera is null && !_loggedMissingCameraWhileLoading )
			{
				Log.Warning( $"Game.Render: no active camera while loading. sceneId={ActiveScene.Id} sceneName='{ActiveScene.Name}' isLoading={ActiveScene.IsLoading} loadingScreen={LoadingScreen.IsVisible} connecting={Networking.IsConnecting}" );
				_loggedMissingCameraWhileLoading = true;
			}
			else if ( ActiveScene.Camera is not null )
			{
				var cam = ActiveScene.Camera;
				var invalid = !cam.Active || cam.Viewport.z <= 0 || cam.Viewport.w <= 0;
				if ( invalid && !_loggedInvalidCameraWhileLoading )
				{
					Log.Warning( $"Game.Render: selected camera is not renderable while loading. name='{cam.GameObject?.Name ?? "(null)"}' active={cam.Active} viewport={cam.Viewport} renderTarget={(cam.RenderTarget is not null)}" );
					_loggedInvalidCameraWhileLoading = true;
				}
				else if ( !invalid )
				{
					_loggedInvalidCameraWhileLoading = false;
				}
			}

			ActiveScene.RenderEnvmaps();

			// Make sure overlays are rendered even when we are loading
			if ( ActiveScene.Camera is not null )
			{
				_loggedMissingCameraWhileLoading = false;
				ActiveScene.Camera.SceneCamera.EnableEngineOverlays = true;
				ActiveScene.Camera.AddToRenderList( swapChain, default );
			}

			Log.Trace( "Game.Render: exit (loading path)" );
			return;
		}

		if ( ActiveScene.Camera is null && !_loggedMissingCameraWhilePlaying )
		{
			Log.Warning( $"Game.Render: no active camera while playing. sceneId={ActiveScene.Id} sceneName='{ActiveScene.Name}'" );
			_loggedMissingCameraWhilePlaying = true;
		}
		else if ( ActiveScene.Camera is not null )
		{
			_loggedMissingCameraWhilePlaying = false;
			var cam = ActiveScene.Camera;
			var invalid = !cam.Active || cam.Viewport.z <= 0 || cam.Viewport.w <= 0;
			if ( invalid && !_loggedInvalidCameraWhilePlaying )
			{
				Log.Warning( $"Game.Render: selected camera is not renderable while playing. name='{cam.GameObject?.Name ?? "(null)"}' active={cam.Active} viewport={cam.Viewport} renderTarget={(cam.RenderTarget is not null)}" );
				_loggedInvalidCameraWhilePlaying = true;
			}
			else if ( !invalid )
			{
				_loggedInvalidCameraWhilePlaying = false;
			}
		}

		ActiveScene.Camera?.SceneCamera.EnableEngineOverlays = true;
		SceneCamera.RecordingCamera = ActiveScene.Camera?.SceneCamera;

		ActiveScene.Render( swapChain, default );
		Log.Trace( "Game.Render: exit (rendered scene)" );
	}

	internal static void Shutdown()
	{
		IsClosing = true;
		IsPlaying = false;

		ActiveScene?.Destroy();
		ActiveScene = null;

		IsClosing = false;
	}
}
