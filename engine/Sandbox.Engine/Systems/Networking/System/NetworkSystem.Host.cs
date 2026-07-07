namespace Sandbox.Network;

internal partial class NetworkSystem
{
	public void InitializeHost()
	{
		Log.Info( $"NetworkSystem.InitializeHost: begin. ActiveSceneId={Game.ActiveScene?.Id} rootChildren={Game.ActiveScene?.Children.Count ?? 0} gameObjects={Game.ActiveScene?.Directory?.GameObjectCount ?? 0}" );
		IsHost = true;
		InstallStringTables();

		// Conna: if we're the host then set our state as Connected.
		Connection.Local.State = Connection.ChannelState.Connected;

		if ( !Application.IsDedicatedServer )
		{
			// Add connection info for the local connection
			var localConnectionInfo = ConnectionInfo.Add( Connection.Local );
			localConnectionInfo.Update( UserInfo.Local );
		}

		InitializeGameSystem();
		Log.Info( $"NetworkSystem.InitializeHost: end. ActiveSceneId={Game.ActiveScene?.Id} rootChildren={Game.ActiveScene?.Children.Count ?? 0} gameObjects={Game.ActiveScene?.Directory?.GameObjectCount ?? 0}" );
	}
}

