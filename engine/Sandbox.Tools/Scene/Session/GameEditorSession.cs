namespace Editor;

public class GameEditorSession : SceneEditorSession
{
	internal static GameEditorSession Current = null;

	public SceneEditorSession Parent { get; init; }

	public override bool IsPlaying => true;

	public GameEditorSession( SceneEditorSession parent, Scene scene ) : base( scene )
	{
		Parent = parent;
		Log.Info( $"GameEditorSession.ctor: parentSceneId={parent?.Scene?.Id} childSceneId={scene?.Id} childName='{scene?.Name ?? "(null)"}'" );

		Assert.IsNull( Current, "Attempted to create new GameEditorSession when one already exists!" );
		Current = this;
	}

	public override void Destroy()
	{
		Log.Info( $"GameEditorSession.Destroy: childSceneId={Scene?.Id} current={(Current == this)}" );
		base.Destroy();

		Current = null;
	}

	public override void StopPlaying() => Parent.StopPlaying();

	public override void FrameTo( in BBox box )
	{
		Parent.FrameTo( box );
	}
}
