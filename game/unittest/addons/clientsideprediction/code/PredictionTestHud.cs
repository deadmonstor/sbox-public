using Sandbox;
using Sandbox.UI;
using System;
using System.Linq;

public sealed class PredictionTestHud : PanelComponent
{
	Label status;
	Button toggle;
	Button reset;
	Button respawn;
	bool menuOpen = true;
	MouseVisibility previousMouse;

	protected override void OnStart()
	{
		base.OnStart();
		previousMouse = Mouse.Visibility;
	}

	protected override void OnTreeFirstBuilt()
	{
		Panel.Style.Set( "position: absolute; left: 24px; top: 24px; width: 540px; padding: 20px; flex-direction: column; gap: 12px; background-color: #14202fee; color: white; font-family: Poppins; font-size: 18px; border-radius: 12px; pointer-events: all;" );
		status = Panel.AddChild( new Label() );
		status.Style.Set( "white-space: pre-line;" );
		toggle = AddButton( "Toggle prediction (host)", () => Scene.GetAllComponents<PredictionTest>().FirstOrDefault()?.TogglePrediction() );
		reset = AddButton( "Reset everyone (host)", () => Scene.GetAllComponents<PredictionTest>().FirstOrDefault()?.ResetEveryone() );
		respawn = AddButton( "Respawn me", () => LocalPlayer()?.GetComponent<PredictionTestPlayer>()?.RequestRespawn() );
		AddButton( "Play — hold Tab to show controls", () => menuOpen = false );
	}

	Button AddButton( string text, Action clicked )
	{
		var button = Panel.AddChild( new Button( text, clicked ) );
		button.Style.Set( "padding: 12px 16px; background-color: #29618c; color: white; border-radius: 6px; cursor: pointer;" );
		return button;
	}

	PlayerController LocalPlayer() => Scene.GetAllComponents<PlayerController>().FirstOrDefault( p => !p.IsProxy );

	protected override void OnUpdate()
	{
		if ( Scene.IsEditor || status is null ) return;
		var local = LocalPlayer();
		var visible = menuOpen || Input.Keyboard.Down( "tab" );
		Panel.Style.Display = visible ? DisplayMode.Flex : DisplayMode.None;
		Mouse.Visibility = visible ? MouseVisibility.Visible : MouseVisibility.Hidden;
		if ( local.IsValid() ) local.UseLookControls = !visible;
		toggle.Disabled = !Networking.IsHost;
		reset.Disabled = !Networking.IsHost;
		respawn.Disabled = !local.IsValid();
		var mode = local.IsValid() ? (local.UseClientPrediction ? "PREDICTION ON" : "PREDICTION OFF (rigidbody)") : "Waiting for player / lobby";
		var details = local.IsValid() ? $"Speed {local.Velocity.Length:F1} | Grounded {local.IsOnGround}\nDucking {local.IsDucking} | Corrections {local.PredictionCorrections}" : "";
		status.Text = $"{mode} — {(Networking.IsHost ? "HOST" : "CLIENT")}\n{details}\nWASD move / Shift run / Ctrl crouch / Space jump\nAhead: stairs and wall. Left: slopes.\nRight: crouch tunnel and moving platform.";
		if ( !visible ) Scene.DebugOverlay.ScreenText( new Vector2( 24, 24 ), $"{mode} | Corrections {local?.PredictionCorrections ?? 0}\nHold Tab for clickable test controls", 18, TextFlag.LeftTop );
	}

	protected override void OnDisabled()
	{
		Mouse.Visibility = previousMouse;
		var local = LocalPlayer();
		if ( local.IsValid() ) local.UseLookControls = true;
		base.OnDisabled();
	}
}
