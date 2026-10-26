using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Sandbox;
using Editor;

public static class EditorSlowdownProbe
{
	const long MB = 1024 * 1024;

	static SceneEditorSession EditorSession => SceneEditorSession.Active is GameEditorSession game ? game.Parent : SceneEditorSession.Active;

	static int Count( Delegate d ) => d?.GetInvocationList().Length ?? 0;

	[ConCmd( "slowdown_probe" )]
	public static void Probe()
	{
		var undo = EditorSession?.UndoSystem;
		var menuCleanup = typeof( MenuBar ).GetField( "Unregister", BindingFlags.NonPublic | BindingFlags.Static )?.GetValue( null ) as Delegate;

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		using var process = Process.GetCurrentProcess();

		Log.Info( $"[Slowdown] undo entries={(undo?.Back.Count ?? 0) + (undo?.Forward.Count ?? 0)} " +
			$"undo callbacks={Count( undo?.OnUndo )} redo callbacks={Count( undo?.OnRedo )} " +
			$"menu cleanup callbacks={Count( menuCleanup )} " +
			$"managed={GC.GetTotalMemory( false ) / MB} MB private={process.PrivateMemorySize64 / MB} MB " +
			$"load contexts={AssemblyLoadContext.All.Count()}" );
	}

	[ConCmd( "slowdown_clutter" )]
	public static void Clutter( int count = 1000 )
	{
		var session = SceneEditorSession.Active;
		if ( session is null || session is GameEditorSession )
		{
			Log.Warning( "[Slowdown] Stop Play and select the scene tab first." );
			return;
		}

		using var scope = session.Scene.Push();
		var root = new GameObject( true, "Clutter" );
		var model = Model.Load( "models/dev/box.vmdl" );

		for ( int i = 0; i < count; i++ )
		{
			var go = new GameObject( root, true, $"Cube {i}" );
			go.WorldPosition = new Vector3( (i % 32) * 60, (i / 32) * 60, 0 );
			go.AddComponent<ModelRenderer>().Model = model;
		}

		session.HasUnsavedChanges = true;
		Log.Info( $"[Slowdown] Added {count} cubes." );
	}

	[ConCmd( "slowdown_undo" )]
	public static void UndoHistory( int count = 200 )
	{
		var session = SceneEditorSession.Active;
		if ( session is null || session is GameEditorSession )
		{
			Log.Warning( "[Slowdown] Stop Play and select the scene tab first." );
			return;
		}

		var target = session.Scene.GetAllObjects( true ).FirstOrDefault( x => x.Name == "Target" );
		if ( target is null )
		{
			Log.Warning( "[Slowdown] The scene has no Target object." );
			return;
		}

		using var scope = session.Scene.Push();
		var timer = Stopwatch.StartNew();

		for ( int i = 0; i < count; i++ )
		{
			PointLight light;
			using ( session.UndoScope( "Add Point Light" ).WithComponentCreations().Push() )
			{
				light = target.AddComponent<PointLight>();
			}

			using ( session.UndoScope( "Remove Point Light" ).WithComponentDestructions( light ).Push() )
			{
				light.Destroy();
			}
		}

		Log.Info( $"[Slowdown] {count} add/remove pairs took {timer.ElapsedMilliseconds} ms." );
		Probe();
	}

	[ConCmd( "slowdown_refresh" )]
	public static void Refresh( int count = 20 )
	{
		var timer = Stopwatch.StartNew();

		for ( int i = 0; i < count; i++ )
		{
			EditorEvent.Run( "refresh" );
		}

		Log.Info( $"[Slowdown] {count} refreshes took {timer.ElapsedMilliseconds} ms." );
		Probe();
	}
}
