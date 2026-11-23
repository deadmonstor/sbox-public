using Borealis;
using Borealis.Backend;
using Borealis.Backend.Lods;
using Borealis.Backend.Meshlets;
using Borealis.Gpu;
using Borealis.Source2;
using Borealis.Source2.Models;
using Sandbox;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using V3 = System.Numerics.Vector3;

namespace BorealisGpuTests;

/// <summary>
/// Generated levels of a loaded model without LODs of its own (docs/reference/borealis-gpu-driven.md, "Generated LODs"), as
/// a view draws its own meshes (classic draws, or GPU-driven instance batches): the model's draws ask for their levels
/// through <see cref="BackendModel"/>, a far mesh draws a coarser level than a near one, with fewer triangles
/// than whole, and a level kept doesn't flip back as the camera sways where it changed.
/// </summary>
[TestClass]
[DoNotParallelize]
public class GeneratedLevelTests : GpuTest
{
	const string Model = "models/dev/sphere.vmdl";

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void ViewsDrawTheirOwnMeshesAtGeneratedLevels( bool gpuDriven )
	{
		var core = ContentFiles.FindGame().Roots.FirstOrDefault();
		if ( core is not null ) Borealis.Backend.Meshlets.MeshOptimizer.Directory = EngineFolders.NativeIn( Path.GetDirectoryName( core ) );
		if ( !Borealis.Backend.Meshlets.MeshOptimizer.Available ) Assert.Inconclusive( "meshopt isn't built" );

		// Classic draws, or GPU-driven instance batches (resident meshes pick their levels on the GPU, borealis_resident_cull_cs)
		using var backend = new BackendDevice( Device ) { GpuDrivenGeometry = true, GpuDriven = gpuDriven, GpuDrivenResidentMeshes = false };
		var can = backend.LoadModel( Model );
		if ( can is null ) Assert.Inconclusive( $"{Model} isn't built" );
		if ( can.LodCount != 1 ) Assert.Inconclusive( $"{Model} has LODs of its own" );

		var previousHost = RenderHost.Current;
		RenderHost.Current = new BackendRenderHost( backend );
		try
		{
			using var system = new RenderSystem { ParallelRecording = false };
			using var target = ViewTarget.Create( new Vector2Int( 640, 360 ), 1, TextureFormat.RGBA16161616F, TextureFormat.D32FS8, "Generated levels test" );

			var world = new RenderWorld();
			world.Lighting.SunDirection = new Vector3( 0.5f, 0.35f, -0.8f ).Normal;
			world.Lighting.SunColor = new Color( 1.25f, 1.15f, 1.0f );
			world.Lighting.AmbientColor = new Color( 0.12f, 0.15f, 0.2f, 1 );
			// Static, as GPU-driven calls take static meshes' draws as instance batches
			var near = new MeshObject( can, new Transform( new Vector3( 120, -40, -20 ) ) ) { IsStatic = true };
			var far = new MeshObject( can, new Transform( new Vector3( 4000, 300, -20 ) ) ) { IsStatic = true };
			world.Add( near );
			world.Add( far );

			var view = new RenderView { Name = "Generated levels test", FieldOfView = 70, ZNear = 4, ZFar = 20000, ClearColor = new Color( 0.3f, 0.4f, 0.5f ), Viewport = new Rect( 0, 0, 640, 360 ) };
			view.Rotation = Rotation.LookAt( Vector3.Forward );

			RenderStats Frame()
			{
				backend.BeginFrame();
				RenderHost.Current.NextFrame();
				system.Render( world, view, target );
				Device.WaitIdle();
				return system.Stats;
			}

			// The levels are built on workers and published at a frame's start, after a view first asks for them
			var waited = Stopwatch.StartNew();
			while ( can.LevelCount == 0 && waited.Elapsed.TotalSeconds < 30 )
			{
				Frame();
				Thread.Sleep( 20 );
			}

			Assert.IsTrue( can.LevelCount > 0, $"the model's draws have generated levels: {backend.LodReport}" );
			var own = can.DrawsForLod( 0 ).ToArray();
			var first = can.DrawsForLod( 1 ).ToArray();
			Assert.AreEqual( own.Length, first.Length, "a level is the model's draws over fewer indices" );
			Assert.IsTrue( first.Sum( d => d.IndexCount ) < own.Sum( d => d.IndexCount ) );
			Assert.IsTrue( first.Zip( own ).All( p => p.First.IndexCount == p.Second.IndexCount || p.First.StartIndex > p.Second.StartIndex ), "levels sit after the model's own indices in their page" );

			var meshes = system.GetFeature<Borealis.Features.MeshRenderFeature>();
			var withLevels = Frame();
			Assert.IsTrue( meshes.LodFor( far ) > 0, "the far mesh draws a generated level" );
			Assert.IsTrue( meshes.LodFor( near ) < meshes.LodFor( far ), $"the near one a finer one: {meshes.LodFor( near )} against {meshes.LodFor( far )}" );

			far.LodOverride = 0;
			var whole = Frame();
			Assert.AreEqual( 0, meshes.LodFor( far ), "a fixed LOD draws whole" );
			Assert.IsTrue( withLevels.Triangles < whole.Triangles, $"fewer triangles at the level: {withLevels.Triangles} against {whole.Triangles}" );
			far.LodOverride = -1;
			Frame();

			// Dolly towards the far mesh until its level goes finer, then sway there: the level it keeps doesn't flip back
			var level = meshes.LodFor( far );
			var at = view.Position;
			for ( int i = 0; i < 400 && meshes.LodFor( far ) == level; i++ )
			{
				at += Vector3.Forward * 10;
				view.Position = at;
				Frame();
			}

			Assert.AreNotEqual( level, meshes.LodFor( far ), "dollying in changes the far mesh's level" );
			var reversals = Borealis.Features.MeshRenderFeature.LevelReversals;
			var distance = far.Transform.Position.x - at.x;
			for ( int i = 0; i < 40; i++ )
			{
				view.Position = at + Vector3.Forward * distance * 0.005f * MathF.Sin( i / 40.0f * 4 * MathF.Tau );
				Frame();
			}

			Assert.AreEqual( reversals, Borealis.Features.MeshRenderFeature.LevelReversals, "no level changes back where it changed" );
		}
		finally
		{
			Device.WaitIdle();
			RenderHost.Current = previousHost;
		}
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void LateMeshletsAndLevelsUseTheUploadedModelsOriginalGeometry( bool cooked )
	{
		var core = ContentFiles.FindGame().Roots.FirstOrDefault();
		if ( core is not null ) MeshOptimizer.Directory = EngineFolders.NativeIn( Path.GetDirectoryName( core ) );
		if ( !MeshOptimizer.Available ) Assert.Inconclusive( "meshopt isn't built" );

		using var backend = new BackendDevice( Device ) { GpuDrivenGeometry = true, GeneratedLods = true };

		var config = MeshletConfig.Default;
		var lodConfig = LodConfig.Default;
		var mesh = MakeGeometryModel( backend, cooked, config, lodConfig );
		var model = (BackendModel)mesh.Geometry;
		var draw = mesh.DrawsForLod( 0 ).ToArray().MaxBy( d => d.IndexCount );
		var geometry = ((IBackendGeometry)model).For( draw.Mesh, draw.DrawCall );
		Assert.IsTrue( geometry.Pooled.HasValue, "the draw is in an index page" );
		var pooled = geometry.Pooled.Value;
		var range = new MeshletStore.Range( pooled.IndexPageSlot, pooled.FirstIndex + (uint)draw.StartIndex, (uint)draw.IndexCount, geometry.BaseVertex );
		var source = MeshletStore.SourceOf( model, draw.Mesh, draw.DrawCall, pooled.IndexPage, pooled.IndexFormat );
		source = source with { IndexBase = source.IndexBase + draw.StartIndex, VertexBase = source.VertexBase + geometry.BaseVertex };

		var (ordered, meshletCount, levels, errors) = ExpectedGeometry( source, draw.IndexCount, config, lodConfig, cooked );
		var originals = Payloads( model.File );
		for ( int i = 0; i < 3; i++ ) backend.BeginFrame();
		AssertCollected( originals );

		var useCooked = MeshletStore.UseCooked;
		MeshletStore.UseCooked = true;
		using var meshlets = new MeshletStore( Device, config, threads: 1 );
		using var lods = new LodStore( Device, backend.GpuDrivenTables, lodConfig, threads: 1 );
		try
		{
			void Finish( Func<int> pending, Action beginFrame )
			{
				var waited = Stopwatch.StartNew();
				while ( pending() > 0 && waited.Elapsed.TotalSeconds < 30 )
				{
					backend.BeginFrame();
					beginFrame();
					Thread.Sleep( 2 );
				}

				Assert.AreEqual( 0, pending(), "the build settled" );
			}

			meshlets.Request( range, source );
			Finish( () => meshlets.Pending, meshlets.BeginFrame );
			Assert.AreEqual( 1L, meshlets.Published );
			Assert.AreEqual( (long)meshletCount, meshlets.Meshlets );
			Assert.AreEqual( cooked ? 1L : 0L, meshlets.CookedHits );
			CollectionAssert.AreEqual( ordered, ReadIndices( pooled, range.FirstIndex, range.IndexCount ) );

			// Collect the reloaded bytes too. A later LOD request needs the original order, not the meshlet index page's.
			AssertCollected( Payloads( model.File, cooked ? null : source.Indices ) );
			lods.Request( range, source, color: true );
			Finish( () => lods.Pending, lods.BeginFrame );
			Assert.AreEqual( cooked ? 1L : 0L, lods.CookedHits );
			Assert.IsTrue( lods.TryGetLevels( range, out var placed, out var placedErrors ) );
			Assert.IsTrue( placed.Length > 0 && placed.Length <= levels.Length, "the model has levels in its index page" );
			CollectionAssert.AreEqual( errors, placedErrors );
			for ( int k = 0; k < placed.Length; k++ )
				CollectionAssert.AreEqual( levels[k], ReadIndices( pooled, placed[k].First, placed[k].Count ) );
			AssertCollected( Payloads( model.File, cooked ? null : source.Indices ) );
		}
		finally
		{
			MeshletStore.UseCooked = useCooked;
			Device.WaitIdle();
		}
	}

	[MethodImpl( MethodImplOptions.NoInlining )]
	static RenderMesh MakeGeometryModel( BackendDevice backend, bool cooked, MeshletConfig config, LodConfig lodConfig )
	{
		if ( !cooked )
		{
			if ( backend.ReadModel( Model ).GetAwaiter().GetResult() is null ) Assert.Inconclusive( $"{Model} isn't built" );
			backend.BeginFrame();
			var uploaded = backend.LoadModelAsync( Model, ulong.MaxValue, out _ );
			Assert.IsNotNull( uploaded, "the streamed model has been read and uploads" );
			if ( uploaded.LodCount != 1 ) Assert.Inconclusive( $"{Model} has LODs of its own" );
			return uploaded;
		}

		if ( backend.Content.TryRead( Model ) is not { } data ) { Assert.Inconclusive( $"{Model} isn't built" ); return null; }
		var file = ModelFile.Read( data, backend.Content.TryRead );
		if ( file.LodCount != 1 ) Assert.Inconclusive( $"{Model} has LODs of its own" );
		var cut = MeshletBuilder.Cut( config );
		var levelCut = LodStore.Cut( lodConfig );
		file = file.WithMeshes( file.Meshes.Select( m => MeshletCooker.Cook( m, config.MaxVertices, config.MaxTriangles, cut, LodStore.Cooked( lodConfig ), levelCut ) ?? m ).ToArray() );
		return BackendModel.Create( backend, file );
	}

	[MethodImpl( MethodImplOptions.NoInlining )]
	static (uint[] Ordered, int Meshlets, uint[][] Levels, float[] Errors) ExpectedGeometry( MeshletStore.Source source, int count, MeshletConfig config, LodConfig lodConfig, bool cooked )
	{
		var indices = Enumerable.Range( source.IndexBase, count ).Select( i => source.Indices[i] ).ToArray();
		if ( cooked )
		{
			var meshlets = source.Indices.Meshlets.Find( source.IndexBase, count, source.Vertices, source.VertexBase );
			var levels = source.Indices.Meshlets.FindLevels( source.IndexBase, count, source.Vertices, source.VertexBase );
			Assert.IsNotNull( levels, "the test draw has cooked levels" );
			return (indices, meshlets.Length / MeshletSet.RecordBytes, levels.Buffers.Select( b => MemoryMarshal.Cast<byte, uint>( b.Data ).ToArray() ).ToArray(), [.. levels.Errors]);
		}

		V3 Position( int vertex )
		{
			var p = source.Vertices.Read( source.Position, vertex );
			return new V3( p.X, p.Y, p.Z );
		}

		var set = MeshletBuilder.Build( indices, Position, source.VertexBase, config );
		Assert.IsNotNull( set, "the test draw has meshlets" );
		var made = LodStore.Cut( lodConfig )( indices, source.Vertices, source.Position, source.VertexBase );
		Assert.IsTrue( made.HasValue && made.Value.Levels.Length > 0, "the test draw simplifies" );
		return (set.Indices, set.Meshlets.Length, made.Value.Levels, made.Value.Errors);
	}

	[MethodImpl( MethodImplOptions.NoInlining )]
	static WeakReference<byte[]>[] Payloads( ModelFile file, MeshIndexBuffer rewritten = null )
	{
		var arrays = new List<WeakReference<byte[]>>();
		foreach ( var mesh in file.Meshes )
		{
			foreach ( var buffer in mesh.VertexBuffers ) if ( buffer.Count > 0 && !buffer.LegacyTangentFrame ) arrays.Add( new( buffer.Data ) );
			foreach ( var buffer in mesh.IndexBuffers ) if ( buffer.Count > 0 && !ReferenceEquals( buffer, rewritten ) ) arrays.Add( new( buffer.Data ) );
		}
		return arrays.ToArray();
	}

	static void AssertCollected( WeakReference<byte[]>[] arrays )
	{
		GC.Collect( GC.MaxGeneration, GCCollectionMode.Forced, blocking: true );
		foreach ( var bytes in arrays ) Assert.IsFalse( bytes.TryGetTarget( out _ ), "a live model and its stores don't hold the decoded arrays" );
	}

	uint[] ReadIndices( in PooledGeometry pooled, uint first, uint count )
	{
		var size = pooled.IndexFormat == IndexFormat.UInt32 ? 4 : 2;
		var bytes = count * (long)size;
		var buffer = Device.CreateBuffer( new BufferInfo { Size = bytes, Memory = MemoryLocation.Readback, Name = "Generated geometry readback" } );
		var list = Device.BeginCommandList( QueueType.Graphics, "Read generated geometry" );
		list.Barrier( PipelineStage.AllCommands, MemoryAccess.MemoryWrite, PipelineStage.Transfer, MemoryAccess.TransferRead );
		list.CopyBuffer( pooled.IndexPage, first * (long)size, buffer, 0, bytes );
		list.Barrier( new BufferBarrier { Buffer = buffer, SourceStage = PipelineStage.Transfer, SourceAccess = MemoryAccess.TransferWrite, DestinationStage = PipelineStage.Host, DestinationAccess = MemoryAccess.HostRead } );
		Device.Wait( Device.Submit( list ) );
		var data = Device.Map( buffer );
		var indices = new uint[count];
		if ( size == 4 ) MemoryMarshal.Cast<byte, uint>( data ).CopyTo( indices );
		else
		{
			var narrow = MemoryMarshal.Cast<byte, ushort>( data );
			for ( int i = 0; i < indices.Length; i++ ) indices[i] = narrow[i];
		}

		Device.Destroy( buffer );
		return indices;
	}

	/// <summary>
	/// A model made at run time (a water surface's clipmap, whose vertices hold cells its vertex stage places) and a draw
	/// whose material places its vertices itself get no generated levels: they'd be simplified from positions that aren't
	/// where they draw, and Construct's pit water drew a few strips from far away.
	/// </summary>
	[TestMethod]
	public void RunTimeModelsAndMaterialsThatPlaceTheirVerticesGetNoLevels()
	{
		var core = ContentFiles.FindGame().Roots.FirstOrDefault();
		if ( core is not null ) Borealis.Backend.Meshlets.MeshOptimizer.Directory = EngineFolders.NativeIn( Path.GetDirectoryName( core ) );
		if ( !Borealis.Backend.Meshlets.MeshOptimizer.Available ) Assert.Inconclusive( "meshopt isn't built" );

		using var backend = new BackendDevice( Device ) { GpuDrivenGeometry = true, GpuDriven = false, GpuDrivenResidentMeshes = false };
		var can = backend.LoadModel( Model );
		if ( can is null ) Assert.Inconclusive( $"{Model} isn't built" );
		if ( can.LodCount != 1 ) Assert.Inconclusive( $"{Model} has LODs of its own" );

		// The same model made as a run-time model is (MirrorContent.RuntimeMesh)
		var runTime = BackendModel.Create( backend, Borealis.Source2.Models.ModelFile.Read( backend.Content.TryRead( Model ), backend.Content.TryRead ), meshGroups: ulong.MaxValue, drawFastPath: true, generateLevels: false );
		Assert.AreSame( GeneratedLevels.None, runTime.Geometry.LevelsFor( runTime.DrawsForLod( 0 ).ToArray()[0] ), "a run-time model's draws: none" );

		var model = (BackendModel)can.Geometry;
		var draw = can.DrawsForLod( 0 ).ToArray()[0];
		using var placing = new PlacingMaterial( true );
		Assert.AreSame( GeneratedLevels.None, model.LevelsFor( draw with { Material = placing } ), "a material that places its vertices itself: none" );
	}

	sealed class PlacingMaterial( bool? places ) : GpuMaterial
	{
		public override string Name => "Places its vertices";
		public override bool IsTranslucent => false;
		public override bool IsAlphaTest => false;
		public override bool ReadsFrameBuffer => false;
		public override bool ShadowFastPath => false;
		public override bool NeedsLightProbe => false;
		public override ulong SimilarityKey => 1;
		public override bool? PlacesVertices => places;
		public override void Dispose() { }
	}
}
