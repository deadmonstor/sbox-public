using Sandbox;
using Sandbox.Network;
using System;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// Creates the multiplayer prediction test scene and manages its players.
/// </summary>
public sealed class PredictionTest : Component, Component.INetworkListener
{
	/// <summary>
	/// Whether players use clientside prediction.
	/// </summary>
	[Property] public bool PredictionEnabled { get; set; } = true;

	/// <summary>
	/// Whether loading the scene creates a lobby.
	/// </summary>
	[Property] public bool StartServer { get; set; } = true;

	protected override async Task OnLoad()
	{
		if ( Scene.IsEditor || !StartServer || Networking.IsActive )
			return;

		await Task.DelayRealtimeSeconds( 0.1f );
		Networking.CreateLobby( new LobbyConfig { Name = "Clientside prediction test", MaxPlayers = 8, Privacy = LobbyPrivacy.FriendsOnly } );
	}

	/// <summary>
	/// Spawns test objects and a player for an active connection.
	/// </summary>
	public void OnActive( Connection connection )
	{
		if ( !Networking.IsHost )
			return;

		if ( Scene.GetAllComponents<PlayerController>().Any( p => p.Network.OwnerId == connection.Id ) )
			return;

		var platform = Scene.GetAllComponents<PredictionPlatform>().FirstOrDefault();

		if ( platform.IsValid() && !platform.GameObject.Network.Active )
			platform.GameObject.NetworkSpawn();

		foreach ( var pusher in Scene.GetAllComponents<PredictionPusher>() )
		{
			if ( !pusher.GameObject.Network.Active )
				pusher.GameObject.NetworkSpawn();
		}

		if ( !Scene.GetAllComponents<PredictionMarkTarget>().Any() )
		{
			var target = new GameObject( true, "Historical marking target" );
			target.WorldPosition = new Vector3( -180, -120, 64 );
			target.Components.Create<BoxCollider>().Scale = new Vector3( 32, 32, 72 );
			target.Components.Create<PredictionMarkTarget>();
			target.Components.Create<LagCompensation>();

			var targetBody = new GameObject( target, true, "Target visual" );
			targetBody.LocalScale = new Vector3( 0.64f, 0.64f, 1.44f );

			var targetVisual = targetBody.Components.Create<ModelRenderer>();
			targetVisual.Model = Model.Load( "models/dev/box.vmdl" );
			targetVisual.Tint = Color.Cyan;
			target.NetworkSpawn();
		}

		var dial = new GameObject( true, $"Predicted panel - {connection.DisplayName}" );
		dial.WorldPosition = new Vector3( -320, -280 - Scene.GetAllComponents<PredictionDial>().Count() * 90, 48 );
		dial.Components.Create<BoxCollider>().Scale = new Vector3( 8, 96, 96 );
		dial.Components.Create<PredictionDial>();
		dial.Components.Create<LagCompensation>();

		var dialVisual = new GameObject( dial, true, "Panel visual" );
		dialVisual.LocalScale = new Vector3( 0.16f, 1.92f, 1.92f );

		var dialRenderer = dialVisual.Components.Create<ModelRenderer>();
		dialRenderer.Model = Model.Load( "models/dev/box.vmdl" );
		dialRenderer.Tint = Color.Green;
		dial.NetworkSpawn( connection );

		var player = new GameObject( true, $"Player - {connection.DisplayName}" );
		player.WorldPosition = SpawnPosition( Scene.GetAllComponents<PlayerController>().Count() );
		var controller = player.Components.Create<PlayerController>();
		controller.UseClientPrediction = PredictionEnabled;
		controller.UseAnimatorControls = false;
		controller.EnableFootstepSounds = false;
		controller.ThirdPerson = false;
		player.Components.Create<PredictionTestPlayer>();
		player.Components.Create<PredictionProbe>().Enabled = PredictionEnabled;
		player.Components.Create<LagCompensation>();

		var visual = new GameObject( player, true, "Body" );
		visual.LocalPosition = Vector3.Up * 36;
		visual.LocalScale = new Vector3( 0.5f, 0.5f, 1.44f );

		var renderer = visual.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = new Color( 0.15f, 0.65f, 1 );

		player.NetworkSpawn( connection );
	}

	/// <summary>
	/// Removes objects owned by a disconnected player.
	/// </summary>
	public void OnDisconnected( Connection connection )
	{
		foreach ( var player in Scene.GetAllComponents<PlayerController>().Where( p => p.Network.OwnerId == connection.Id ).ToArray() )
		{
			player.GameObject.Destroy();
		}

		foreach ( var dial in Scene.GetAllComponents<PredictionDial>().Where( p => p.Network.OwnerId == connection.Id ).ToArray() )
		{
			dial.GameObject.Destroy();
		}
	}

	/// <summary>
	/// Gets a player's starting position.
	/// </summary>
	public static Vector3 SpawnPosition( int index = 0 ) => new( -500, (index % 4) * 80 - 120, 4 );

	/// <summary>
	/// Toggles clientside prediction for all players on the host.
	/// </summary>
	public void TogglePrediction()
	{
		if ( !Networking.IsHost )
			return;

		PredictionEnabled = !PredictionEnabled;

		foreach ( var player in Scene.GetAllComponents<PlayerController>() )
		{
			player.GetComponent<PredictionProbe>( true ).Enabled = PredictionEnabled;
			player.UseClientPrediction = PredictionEnabled;
		}
	}

	/// <summary>
	/// Respawns all players on the host.
	/// </summary>
	public void ResetEveryone()
	{
		if ( !Networking.IsHost )
			return;

		int index = 0;

		foreach ( var player in Scene.GetAllComponents<PlayerController>() )
		{
			ResetPlayer( player, index++ );
		}
	}

	/// <summary>
	/// Restores a player's spawn position and resets prediction and historical poses.
	/// </summary>
	public static void ResetPlayer( PlayerController player, int index = 0 )
	{
		player.WorldPosition = SpawnPosition( index );
		player.Body.Velocity = Vector3.Zero;
		player.Transform.ClearInterpolation();
		player.GetComponent<Prediction>()?.ResetTimeline();
		player.GetComponent<LagCompensation>()?.ResetHistory();
	}
}

/// <summary>
/// Handles local player visuals and owner-requested respawning.
/// </summary>
public sealed class PredictionTestPlayer : Component
{
	protected override void OnStart()
	{
		foreach ( var renderer in GetComponentsInChildren<ModelRenderer>() )
		{
			renderer.GameObject.Tags.Set( "viewer", !IsProxy );
		}
	}

	/// <summary>
	/// Requests an authoritative respawn for this player.
	/// </summary>
	[Rpc.Host( NetFlags.OwnerOnly )]
	public void RequestRespawn()
	{
		PredictionTest.ResetPlayer( GetComponent<PlayerController>() );
	}
}

/// <summary>
/// Moves and rotates an authoritative platform for prediction testing.
/// </summary>
public sealed class PredictionPlatform : Component
{
	Vector3 origin;

	protected override void OnStart() => origin = WorldPosition;

	protected override void OnFixedUpdate()
	{
		if ( Scene.IsEditor || !Networking.IsHost || IsProxy )
			return;

		WorldPosition = origin + Vector3.Right * MathF.Sin( Time.Now * 0.7f ) * 120;
		WorldRotation = Rotation.FromYaw( MathF.Sin( Time.Now * 0.4f ) * 35 );
	}
}

/// <summary>
/// Moves an authoritative rigidbody between the test area's boundaries.
/// </summary>
public sealed class PredictionPusher : Component
{
	float direction = 1;

	protected override void OnFixedUpdate()
	{
		if ( Scene.IsEditor || !Networking.IsHost || IsProxy )
			return;

		if ( WorldPosition.x > -260 )
			direction = -1;

		if ( WorldPosition.x < -740 )
			direction = 1;

		GetComponent<Rigidbody>().Velocity = Vector3.Forward * direction * 120;
	}
}
