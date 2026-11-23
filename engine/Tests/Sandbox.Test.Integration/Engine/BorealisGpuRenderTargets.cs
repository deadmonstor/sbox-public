using Sandbox.Rendering;
using TextureHandle = Borealis.Gpu.TextureHandle;

namespace EngineTests;

[TestClass]
[DoNotParallelize]
public class BorealisGpuRenderTargetsTest
{
	/// <summary>
	/// Temporary targets with nonoverlapping lifetimes use the same backing within a render block and across frames.
	/// A named history texture is still independent, and unused pool entries are freed.
	/// </summary>
	[TestMethod]
	public void ScratchBackbufferSharesAndRetiresItsBacking()
	{
		TestGraphics.Require();
		using var borealis = BorealisGpuRenderBackend.Create( validation: true );
		const int width = 73, height = 43;
		using var output = Texture.CreateRenderTarget().WithSize( width, height ).WithFormat( ImageFormat.RGBA8888 ).Create();
		using var outputTarget = RenderTarget.From( output );
		var outputHandle = borealis.TextureOf( output );

		var firstPass = new CommandList( "First scratch pass" );
		var firstCopy = firstPass.Attributes.GrabScratchFrameTexture( "FirstColor" );
		firstPass.ReleaseRenderTarget( firstCopy );

		var secondPass = new CommandList( "Second scratch pass" );
		var secondCopy = secondPass.Attributes.GrabScratchFrameTexture( "SecondColor" );
		secondPass.Clear( secondCopy, Color.Red );
		secondPass.ReleaseRenderTarget( secondCopy );

		Texture scratchTexture = null;
		TextureHandle scratchHandle = default, historyHandle = default;
		for ( int frame = 0; frame < 10; frame++ )
		{
			using ( Graphics.Scope.CreateOn( borealis ) )
			{
				Graphics.RenderTarget = outputTarget;
				Graphics.Clear( Color.Black, true, false, false );

				using ( var scratch = RenderTarget.GetTemporary( width, height, ImageFormat.RGBA8888, ImageFormat.None ) )
				{
					scratchTexture = scratch.ColorTarget;
					if ( frame == 0 ) scratchHandle = borealis.TextureOf( scratchTexture );
					Assert.AreEqual( scratchHandle, borealis.TextureOf( scratchTexture ), "reuse across frames" );
				}

				firstPass.ExecuteOnRenderThread();
				secondPass.ExecuteOnRenderThread();

				var history = Graphics.GrabFrameTexture( "IndependentHistory" );
				if ( frame == 0 ) historyHandle = borealis.TextureOf( history.ColorTarget );
				Assert.AreEqual( historyHandle, borealis.TextureOf( history.ColorTarget ), "history keeps its own backing" );
				Assert.AreNotEqual( scratchHandle, historyHandle );
			}

			var matching = borealis.Device.ListResources().Textures.FindAll( t => t.Info.Width == width && t.Info.Height == height );
			CollectionAssert.AreEquivalent( new[] { outputHandle, scratchHandle, historyHandle }, matching.ConvertAll( t => t.Handle ) );

			RenderTarget.EndOfFrame();
			borealis.NextFrame();
		}

		Assert.AreEqual( new Color32( 255, 0, 0, 255 ), borealis.ReadPixels( scratchTexture )[0], "the second pass used the scratch backing" );

		for ( int frame = 0; frame < 8; frame++ )
		{
			RenderTarget.EndOfFrame();
			borealis.NextFrame();
		}

		Assert.IsFalse( borealis.Device.IsAlive( scratchHandle ), "idle scratch backing was freed" );
		Assert.IsFalse( borealis.Device.IsAlive( historyHandle ), "idle history backing was freed" );
		Assert.AreEqual( 0, borealis.ValidationErrors );
	}
}
