namespace BotSwarm;

/// <summary>
/// Engine message types the bots read or write. Field names must match the engine's
/// properties exactly, since they decide the wire order.
/// </summary>
internal static class Protocol
{
	// Systems/Networking/Messages/NetworkMessages.cs
	public static readonly PackSchema ChannelInfo = PackSchema.Pod( "Sandbox.Network.ChannelInfo", 16 );
	public static readonly PackSchema KickMsg = PackSchema.Struct( "Sandbox.Network.KickMsg", "Reason" );
	public static readonly PackSchema RestartHandshakeMsg = PackSchema.Pod( "Sandbox.Network.RestartHandshakeMsg", 1 );
	public static readonly PackSchema Welcome = PackSchema.Pod( "Sandbox.Network.Welcome", 16 );
	public static readonly PackSchema RequestMountedVPKs = PackSchema.Pod( "Sandbox.Network.RequestMountedVPKs", 16 );
	public static readonly PackSchema MountedVPKsResponse = PackSchema.Struct( "Sandbox.Network.MountedVPKsResponse", "MountedVPKs", "HandshakeId" );
	public static readonly PackSchema RequestInitialSnapshot = PackSchema.Struct( "Sandbox.Network.RequestInitialSnapshot", "UserData", "HandshakeId" );
	public static readonly PackSchema InitialSnapshotResponse = PackSchema.Struct( "Sandbox.Network.InitialSnapshotResponse", "Snapshot", "HandshakeId" );
	public static readonly PackSchema ClientReady = PackSchema.Pod( "Sandbox.Network.ClientReady", 16 );
	public static readonly PackSchema Activate = PackSchema.Pod( "Sandbox.Network.Activate", 16 );
	public static readonly PackSchema ReconnectMsg = PackSchema.Struct( "Sandbox.Network.ReconnectMsg", "Game", "Map" );

	public static readonly PackSchema ServerInfo = PackSchema.Struct( "Sandbox.Network.ServerInfo",
		"ServerData", "ServerName", "MapName", "Host", "Assigned", "MaxPlayers", "EngineVersion",
		"GamePackage", "Map", "HandshakeId", "IsDeveloperHost", "HostMigration", "PasswordRequired" );

	// Systems/Networking/PlayerInfo/UserInfo.cs
	public static readonly PackSchema UserInfo = PackSchema.Struct( "Sandbox.UserInfo",
		"SteamId", "Name", "ConnectionTime", "UserData", "EngineVersion", "PartyId", "AuthTicket",
		"HandshakeId", "IsVr", "InventoryBlob", "Password" );

	// Systems/Chat/Chat.cs
	public static readonly PackSchema ChatMsg = PackSchema.Struct( "Sandbox.Platform.ChatMsg", "Message" );

	// Scene/Networking/SceneNetworkSystem.cs
	public static readonly PackSchema ObjectCreateMsg = PackSchema.Struct( "Sandbox.ObjectCreateMsg",
		"SnapshotVersion", "JsonData", "BlobData", "Transform", "Guid", "Creator", "Parent", "Owner", "TableData", "Enabled" );
	public static readonly PackSchema ObjectCreateBatchMsg = PackSchema.Struct( "Sandbox.ObjectCreateBatchMsg", "CreateMsgs" );
	public static readonly PackSchema ObjectRefreshMsg = PackSchema.Struct( "Sandbox.ObjectRefreshMsg",
		"JsonData", "BlobData", "TableData", "Snapshot", "Parent", "Guid" );
	public static readonly PackSchema ObjectDestroyMsg = PackSchema.Struct( "Sandbox.ObjectDestroyMsg", "Guid" );

	// Scene/GameObject/GameObject.Network.cs ownership RPCs, by TypeLibrary method identity
	public static readonly int RpcAssignOwnership = FastHash( "Void.Sandbox.GameObject.Msg_AssignOwnership.Guid,UInt16" );
	public static readonly int RpcTakeOwnership = FastHash( "Void.Sandbox.GameObject.Msg_TakeOwnership.UInt16" );
	public static readonly int RpcDropOwnership = FastHash( "Void.Sandbox.GameObject.Msg_DropOwnership.UInt16" );

	// NetworkObject root transform snapshot slots
	public const int SlotPosition = 1;
	public const int SlotRotation = 2;

	static Protocol()
	{
		foreach ( var schema in new[] { ChannelInfo, KickMsg, RestartHandshakeMsg, Welcome, RequestMountedVPKs, MountedVPKsResponse,
			RequestInitialSnapshot, InitialSnapshotResponse, ClientReady, Activate, ReconnectMsg, ServerInfo, UserInfo, ChatMsg,
			ObjectCreateMsg, ObjectCreateBatchMsg, ObjectRefreshMsg, ObjectDestroyMsg } )
		{
			BytePack.Register( schema );
		}
	}

	/// <summary>
	/// Force the static constructor so schemas are registered before the first read.
	/// </summary>
	public static void Init() { }

	static int FastHash( string value ) => PackSchema.FastHash( value );
}
