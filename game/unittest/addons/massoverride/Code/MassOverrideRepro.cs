using System;

public sealed class MassOverrideRepro : Component
{
	private readonly List<Rigidbody> _clones = new();
	private int _frames;

	[Property, ReadOnly] public string Results { get; private set; } = "Waiting for play mode";

	protected override void OnStart()
	{
		var single = Model.Builder.WithMass( 40 ).AddCollisionBox( new Vector3( 8 ) ).Create();
		CloneBody( "Single part (40 kg)", single, 1500 );

		var builder = Model.Builder;
		builder.AddBody( 40 ).AddBox( new Vector3( 8 ) );
		builder.AddBody( 4 ).AddBox( new Vector3( 2 ) );
		builder.AddBody( 4 ).AddBox( new Vector3( 2 ) );
		CloneBody( "Three parts (40, 4, 4 kg)", builder.Create(), 40 );
	}

	private void CloneBody( string name, Model model, float massOverride )
	{
		var source = new GameObject( false, name + " source" );
		source.Parent = GameObject;
		var renderer = source.Components.Create<ModelRenderer>();
		renderer.Model = model;
		var collider = source.Components.Create<ModelCollider>();
		collider.Model = model;
		var body = source.Components.Create<Rigidbody>();
		body.Gravity = false;
		body.MassOverride = massOverride;

		var clone = source.Clone( source.WorldTransform );
		clone.Name = name + " clone";
		clone.Enabled = true;
		_clones.Add( clone.Components.Get<Rigidbody>() );
		Log.Info( $"[MassOverride] {name}: model part masses = {string.Join( ", ", model.Physics.Parts.Select( x => x.Mass ) )}" );
	}

	protected override void OnUpdate()
	{
		if ( ++_frames == 2 )
		{
			Results = string.Join( "\n", _clones.Select( body =>
				$"{body.GameObject.Name}: expected {body.MassOverride} kg, actual {body.Mass} kg — {(MathF.Abs( body.Mass - body.MassOverride ) < 0.01f ? "PASS" : "FAIL")}" ) );
			Log.Info( $"[MassOverride] {Results}" );
		}

		Scene.DebugOverlay.ScreenText( new Vector2( 24, 24 ), "Issue #10958 — cloned Rigidbody mass\n\n" + Results,
			20, TextFlag.LeftTop, Color.White );
	}
}
