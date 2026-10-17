using Editor;
using Sandbox;
using System;
using System.Linq;
using System.Reflection;
using static Sandbox.Internal.GlobalGameNamespace;

namespace NetworkSnapshotRepro;

public static class HotloadReproMenu
{
	[Menu( "Editor", "Repros/Network snapshots/Hotload host scene" )]
	public static void HotloadHostScene()
	{
		if ( !Game.IsPlaying )
		{
			Log.Warning( "Play the network snapshot hotload repro before invoking this menu." );
			return;
		}

		var scene = Game.ActiveScene;
		if ( !scene.IsValid() || scene.IsEditor )
		{
			Log.Warning( "No active playing scene. Open and play the hotload repro scene." );
			return;
		}

		using var scope = scene.Push();
		var controller = scene.GetAllComponents<HotloadRepro>().FirstOrDefault();
		if ( !controller.IsValid() )
		{
			Log.Warning( "This menu only operates on the project scene containing HotloadRepro." );
			return;
		}

		if ( !Networking.IsActive || !Networking.IsHost )
		{
			Log.Warning( "Invoke this menu on the network host only, not on a client." );
			return;
		}

		if ( !controller.HasClient || !controller.Ready )
		{
			Log.Warning( "Keep at least one warmup client connected and wait for the host HUD to say READY. Confirm that client shows Value=239 before invoking this menu. Restart Play to repeat a completed run." );
			return;
		}

		var onHotload = typeof( Scene ).GetMethod( "OnHotload", BindingFlags.Instance | BindingFlags.NonPublic );
		if ( onHotload is null )
			throw new MissingMethodException( typeof( Scene ).FullName, "OnHotload" );

		// Deliberately invoke the real lifecycle only on this host, not a source hotload mirrored to every peer.
		onHotload.Invoke( scene, null );
		controller.SetValueAfterHostHotload();
	}
}
