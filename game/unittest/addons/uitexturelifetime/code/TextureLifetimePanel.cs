using Sandbox;
using Sandbox.UI;

public sealed class TextureLifetimePanel : PanelComponent
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "TextureLifetime" );

	[Property] public Texture Png { get; set; }
	[Property] public Texture Vtex { get; set; }
	[Property] public bool MarkTexturesUsed { get; set; }
	[Property] public bool CycleVisibility { get; set; }
	[Property] public bool TextureChurn { get; set; }
	[Property] public bool PanelChurn { get; set; }
	[Property] public bool AllocationPressure { get; set; }

	readonly System.Collections.Generic.List<Image> images = new();
	readonly System.Collections.Generic.List<bool> valid = new();
	float nextReport;
	float nextStress;
	int cycle;
	Panel temporary;
	Label stressStatus;
	Label stressCounts;
	int texturesCreated;
	int texturesDisposed;
	int panelsCreated;
	int panelsRemoved;
	int allocatedMegabytes;
	bool checkedTextureIdentity;
	readonly System.Random random = new( 11859 );
	readonly System.Collections.Generic.Queue<byte[]> allocations = new();

	protected override void OnTreeFirstBuilt()
	{
		images.Clear();
		valid.Clear();
		checkedTextureIdentity = false;
		Panel.AddClass( "lifetime" );
		Panel.AddChild<Label>().Text = "Texture lifetime: leave running without editing or hotloading";
		Panel.AddChild<Label>().Text = "Rows: serialized property / direct load / path. Columns: PNG / VTEX";
		var button = Panel.AddChild<Button>();
		button.Text = "Toggle all stress modes";
		button.AddEventListener( "onclick", () =>
		{
			var enabled = !(CycleVisibility || TextureChurn || PanelChurn || AllocationPressure);
			CycleVisibility = TextureChurn = PanelChurn = AllocationPressure = enabled;
			Log.Info( $"All stress modes: {enabled}" );
		} );
		stressStatus = Panel.AddChild<Label>();
		stressCounts = Panel.AddChild<Label>();
		AddRow( "Serialized Texture property", Png, Vtex, false );
		AddRow( "Texture.Load assigned directly", Texture.Load( "textures/direct-png.png" ), Texture.Load( "textures/direct-vtex.vtex" ), false );
		AddRow( "Image.SetTexture(path)", null, null, true );
		AddRow( "Panel owns generated textures", CreateCheckerboard(), CreateCheckerboard(), false );
	}

	Texture CreateCheckerboard( int size = 128 )
	{
		var pixels = new byte[size * size * 4];
		for ( int y = 0; y < size; y++ )
			for ( int x = 0; x < size; x++ )
			{
				var offset = (y * size + x) * 4;
				var bright = ((x / 32 + y / 32) & 1) == 0;
				pixels[offset] = bright ? (byte)255 : (byte)32;
				pixels[offset + 1] = bright ? (byte)128 : (byte)255;
				pixels[offset + 2] = 32;
				pixels[offset + 3] = 255;
			}
		return Texture.Create( size, size ).WithData( pixels ).WithMips().Finish();
	}

	void Stress()
	{
		if ( Time.Now < nextStress ) return;
		nextStress = Time.Now + 1;
		cycle++;
		for ( int i = 0; i < images.Count; i++ )
		{
			var hidden = CycleVisibility && cycle % 30 < 20 && (i & 1) == (cycle / 30 & 1);
			var display = hidden ? DisplayMode.None : DisplayMode.Flex;
			if ( images[i].Style.Display != display )
			{
				images[i].Style.Display = display;
				Log.Info( $"Stress {cycle}: image {i} hidden={hidden}" );
			}
		}
		if ( TextureChurn )
		{
			for ( int i = 0; i < 8; i++ )
			{
				var texture = CreateCheckerboard( 128 << random.Next( 0, 3 ) );
				texturesCreated++;
				if ( (i & 1) == 0 )
				{
					texture.Dispose();
					texturesDisposed++;
				}
			}
		}
		if ( temporary is not null )
		{
			temporary.Delete();
			panelsRemoved += 12;
		}
		temporary = null;
		if ( PanelChurn )
		{
			temporary = Panel.AddChild<Panel>( "temporary" );
			for ( int i = 0; i < 12; i++ )
			{
				var image = temporary.AddChild<Image>();
				image.Texture = i % 3 == 0 ? CreateCheckerboard() : Texture.Load( i % 3 == 1 ? "textures/lifetime.png" : "textures/lifetime.vtex" );
				panelsCreated++;
				if ( i % 3 == 0 ) texturesCreated++;
			}
		}
		if ( AllocationPressure )
		{
			var bytes = new byte[2 * 1024 * 1024];
			bytes[0] = (byte)cycle;
			allocations.Enqueue( bytes );
			allocatedMegabytes += 2;
			while ( allocations.Count > 4 ) allocations.Dequeue();
		}
		else allocations.Clear();
		stressStatus.Text = $"Cycle {cycle}: hide={CycleVisibility}, textures={TextureChurn}, panels={PanelChurn}, allocations={AllocationPressure}, markUsed={MarkTexturesUsed}";
		var visibility = !CycleVisibility ? "All images visible" : cycle % 30 < 20
			? $"One column intentionally hidden; returns in {20 - cycle % 30}s"
			: $"All images visible; next hide in {30 - cycle % 30}s";
		stressCounts.Text = $"{visibility} | Textures: {texturesCreated} created / {texturesDisposed} disposed | Images: {panelsCreated} created / {panelsRemoved} removed | Allocated: {allocatedMegabytes} MB total / {allocations.Count * 2} MB retained";
		if ( cycle % 10 == 0 ) Log.Info( stressStatus.Text );
	}

	void AddRow( string name, Texture png, Texture vtex, bool path )
	{
		var row = Panel.AddChild<Panel>( "row" );
		row.AddChild<Label>().Text = name;
		AddImage( row, png, "textures/path-png.png", path );
		AddImage( row, vtex, "textures/path-vtex.vtex", path );
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
		if ( stressStatus is not null ) Stress();
		if ( !checkedTextureIdentity && images.Count == 8 && images.TrueForAll( image => image.Texture.IsValid() && image.Texture.IsLoaded ) )
		{
			checkedTextureIdentity = true;
			var shared = false;
			for ( int i = 0; i < images.Count; i++ )
				for ( int j = i + 1; j < images.Count; j++ )
					if ( object.ReferenceEquals( images[i].Texture, images[j].Texture ) )
					{
						shared = true;
						Log.Warning( $"Images {i} and {j} share a texture; isolation failed" );
					}
			if ( !shared ) Log.Info( "All eight test images have distinct Texture wrappers" );
		}
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
