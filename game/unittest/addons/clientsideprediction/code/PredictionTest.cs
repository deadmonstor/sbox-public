using Sandbox;
using Sandbox.Network;
using System;
using System.Linq;
using System.Threading.Tasks;

public sealed class PredictionTest : Component, Component.INetworkListener
{
	[Property] public bool PredictionEnabled { get; set; } = true;
	[Property] public bool StartServer { get; set; } = true;

	protected override async Task OnLoad()
	{
		if ( Scene.IsEditor || !StartServer || Networking.IsActive ) return;
		await Task.DelayRealtimeSeconds( 0.1f );
		Networking.CreateLobby( new LobbyConfig { Name = "Clientside prediction test", MaxPlayers = 8, Privacy = LobbyPrivacy.FriendsOnly } );
	}

	public void OnActive( Connection connection )
	{
		if ( !Networking.IsHost ) return;
		if ( Scene.GetAllComponents<PlayerController>().Any( p => p.Network.OwnerId == connection.Id ) ) return;
		var platform = Scene.GetAllComponents<PredictionPlatform>().FirstOrDefault();
		if ( platform.IsValid() && !platform.GameObject.Network.Active ) platform.GameObject.NetworkSpawn();
		foreach ( var pusher in Scene.GetAllComponents<PredictionPusher>() )
			if ( !pusher.GameObject.Network.Active ) pusher.GameObject.NetworkSpawn();

		var player = new GameObject( true, $"Player - {connection.DisplayName}" );
		player.WorldPosition = SpawnPosition( Scene.GetAllComponents<PlayerController>().Count() );
		var controller = player.Components.Create<PlayerController>();
		controller.UseClientPrediction = PredictionEnabled;
		controller.PredictionDebugLogging = true;
		controller.UseAnimatorControls = false;
		controller.EnableFootstepSounds = false;
		controller.ThirdPerson = false;
		player.Components.Create<PredictionTestPlayer>();
		var visual = new GameObject( player, true, "Body" );
		visual.LocalPosition = Vector3.Up * 36;
		visual.LocalScale = new Vector3( 0.5f, 0.5f, 1.44f );
		var renderer = visual.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = new Color( 0.15f, 0.65f, 1 );

		player.NetworkSpawn( connection );
		Log.Info( $"Prediction test: spawned {connection.DisplayName}, prediction={PredictionEnabled}. Join with a second client to test latency." );
	}

	public void OnDisconnected( Connection connection )
	{
		foreach ( var player in Scene.GetAllComponents<PlayerController>().Where( p => p.Network.OwnerId == connection.Id ).ToArray() )
			player.GameObject.Destroy();
	}

	public static Vector3 SpawnPosition( int index = 0 ) => new( -500, (index % 4) * 80 - 120, 4 );

	public void TogglePrediction()
	{
		if ( !Networking.IsHost ) return;
		PredictionEnabled = !PredictionEnabled;
		foreach ( var player in Scene.GetAllComponents<PlayerController>() ) player.UseClientPrediction = PredictionEnabled;
		Log.Info( $"Prediction test: prediction={PredictionEnabled}" );
	}

	public void ResetEveryone()
	{
		if ( !Networking.IsHost ) return;
		int index = 0;
		foreach ( var player in Scene.GetAllComponents<PlayerController>() ) ResetPlayer( player, index++ );
	}

	public static void ResetPlayer( PlayerController player, int index = 0 )
	{
		player.WorldPosition = SpawnPosition( index );
		player.Body.Velocity = Vector3.Zero;
		player.Transform.ClearInterpolation();
		Log.Info( $"Prediction test: host teleport {player.GameObject.Name}" );
	}
}

public sealed class PredictionTestPlayer : Component
{
	protected override void OnUpdate()
	{
		foreach ( var renderer in GetComponentsInChildren<ModelRenderer>() ) renderer.GameObject.Tags.Set( "viewer", !IsProxy );

	}

	[Rpc.Host( NetFlags.OwnerOnly )]
	public void RequestRespawn()
	{
		PredictionTest.ResetPlayer( GetComponent<PlayerController>() );
	}
}

public sealed class PredictionPlatform : Component
{
	Vector3 origin;

	protected override void OnStart() => origin = WorldPosition;

	protected override void OnFixedUpdate()
	{
		if ( Scene.IsEditor || !Networking.IsHost || IsProxy ) return;
		WorldPosition = origin + Vector3.Right * MathF.Sin( Time.Now * 0.7f ) * 120;
		WorldRotation = Rotation.FromYaw( MathF.Sin( Time.Now * 0.4f ) * 35 );
	}
}

public sealed class PredictionPusher : Component
{
	float direction = 1;

	protected override void OnFixedUpdate()
	{
		if ( Scene.IsEditor || !Networking.IsHost || IsProxy ) return;
		if ( WorldPosition.x > -260 ) direction = -1;
		if ( WorldPosition.x < -740 ) direction = 1;
		GetComponent<Rigidbody>().Velocity = Vector3.Forward * direction * 120;
	}
}
