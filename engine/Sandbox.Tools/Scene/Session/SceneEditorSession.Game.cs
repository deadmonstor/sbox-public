namespace Editor;

partial class SceneEditorSession
{
	/// <summary>
	/// The game session of this editor session, if playing.
	/// </summary>
	public GameEditorSession GameSession { get; private set; }

	public virtual bool IsPlaying => GameSession != null;

	public void SetPlaying( Scene scene )
	{
		Log.Info( $"SceneEditorSession.SetPlaying: sceneId={scene?.Id} name='{scene?.Name ?? "(null)"}' rootChildren={scene?.Children.Count ?? 0} gameObjects={scene?.Directory?.GameObjectCount ?? 0}" );
		GameSession = new GameEditorSession( this, scene );
		GameSession.MakeActive();
	}

	public virtual void StopPlaying()
	{
		Log.Info( $"SceneEditorSession.StopPlaying: active={(GameSession is not null)}" );
		GameSession?.Destroy();
		GameSession = null;

		MakeActive();
	}
}
