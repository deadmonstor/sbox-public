using Sandbox;
using Sandbox.UI;

public sealed class TextureLifetimePanel : PanelComponent
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "TextureLifetime" );

	[Property] public Texture Png { get; set; }
	[Property] public Texture Vtex { get; set; }
	[Property] public bool MarkTexturesUsed { get; set; }

	readonly System.Collections.Generic.List<Image> images = new();
	readonly System.Collections.Generic.List<bool> valid = new();
	float nextReport;

	protected override void OnTreeFirstBuilt()
	{
		images.Clear();
		valid.Clear();
		Panel.AddClass( "lifetime" );
		Panel.AddChild<Label>().Text = "Texture lifetime: leave running without editing or hotloading";
		Panel.AddChild<Label>().Text = "Rows: serialized property / direct load / path. Columns: PNG / VTEX";
		AddRow( "Serialized Texture property", Png, Vtex, false );
		AddRow( "Texture.Load assigned directly", Texture.Load( "textures/lifetime.png" ), Texture.Load( "textures/lifetime.vtex" ), false );
		AddRow( "Image.SetTexture(path)", null, null, true );
	}

	void AddRow( string name, Texture png, Texture vtex, bool path )
	{
		var row = Panel.AddChild<Panel>( "row" );
		row.AddChild<Label>().Text = name;
		AddImage( row, png, "textures/lifetime.png", path );
		AddImage( row, vtex, "textures/lifetime.vtex", path );
	}

	void AddImage( Panel row, Texture texture, string path, bool loadPath )
	{
		var image = row.AddChild<Image>();
		if ( loadPath ) image.SetTexture( path );
		else image.Texture = texture;
		images.Add( image );
		valid.Add( image.Texture.IsValid() );
	}

	protected override void OnUpdate()
	{
		for ( int i = 0; i < images.Count; i++ )
		{
			var texture = images[i].Texture;
			if ( MarkTexturesUsed && texture.IsValid() ) texture.MarkUsed();
			var current = texture.IsValid();
			if ( current != valid[i] )
			{
				Log.Info( $"Image {i}: valid {valid[i]} -> {current}, path={texture?.ResourcePath}" );
				valid[i] = current;
			}
		}
		if ( Time.Now < nextReport ) return;
		nextReport = Time.Now + 30;
		for ( int i = 0; i < images.Count; i++ )
		{
			var texture = images[i].Texture;
			Log.Info( $"Image {i}: valid={texture.IsValid()}, error={texture?.IsError}, lastUsed={(texture.IsValid() ? texture.LastUsed : -1)}, markUsed={MarkTexturesUsed}" );
		}
	}
}
