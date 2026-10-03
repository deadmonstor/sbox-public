using System;
using System.Linq;
using Sandbox;
using Editor;

public static class IdentityReproSetup
{
	[ConCmd( "identity_repro_dirty" )]
	public static void Prepare()
	{
		var session = SceneEditorSession.Active;
		if ( Game.IsPlaying || session?.Scene?.Source?.ResourcePath != "scenes/level.scene" )
		{
			Log.Warning( "[Identity repro] Stop Play and select the level.scene editor tab first." );
			return;
		}

		SceneCompileSession.Current.Refresh();
		if ( !SceneCompileSession.Current.HasCompilation )
		{
			Log.Warning( "[Identity repro] Save level.scene and Compile Scene (F9) first." );
			return;
		}

		var component = session.Scene.GetAllObjects( true ).SelectMany( x => x.Components.GetAll() )
			.FirstOrDefault( x => x.GetType().Name == "IdentityRepro" );
		if ( component is null )
		{
			Log.Warning( "[Identity repro] IdentityRepro component is missing." );
			return;
		}

		var marker = "unsaved-" + Guid.NewGuid().ToString( "N" )[..8];
		EditorTypeLibrary.GetSerializedObject( component ).GetProperty( "Marker" ).SetValue( marker );
		session.HasUnsavedChanges = true;
		Log.Info( $"[Identity repro] Prepared dirty editor snapshot: Marker={marker}. Leave unsaved, play menu.scene and load level.scene." );
	}
}
