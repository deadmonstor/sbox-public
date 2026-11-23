using Borealis.Backend;
using Borealis.Backend.Lods;
using Borealis.Backend.Meshlets;
using Borealis.Gpu;
using Borealis.Source2;
using Borealis.Source2.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using V3 = System.Numerics.Vector3;

namespace BorealisGpuTests;

/// <summary>
/// Buffers that grow into new ones while a frame records (the GPU-driven tables, the meshlet run table): what reads them
/// must read the buffer that holds what it looks up, not one read before the growth, or it reads past that buffer's end.
/// </summary>
[TestClass]
[DoNotParallelize]
public class GrowingTablesTests : GpuTest
{
	/// <summary>
	/// A table growing moves <see cref="GpuDrivenTables.Generation"/> on and gives it a new slot, which is how a call that read
	/// the slots before knows to read them again; and the meshlet run table covers every draw with a link to its run,
	/// including draws with no meshlets.
	/// </summary>
	[TestMethod]
	public void GrownTablesAreReadAtTheirNewSlots()
	{
		using var tables = new GpuDrivenTables( Device );
		using var meshlets = new MeshletStore( Device, MeshletConfig.Default, 1 );
		var generation = tables.Generation;
		var draws = tables.Slots.Draws;

		// 1 MiB holds 32768 draw records
		for ( uint i = 0; i < 40000; i++ ) tables.Draw( 0, 0, 0, 0, i, 3, 0, 0 );
		Assert.AreNotEqual( generation, tables.Generation, "growing moves the generation on" );
		Assert.AreNotEqual( draws, tables.Slots.Draws, "at a new slot" );
		Assert.IsTrue( tables.DrawRecords >= 40000 );

		var runs = meshlets.RunSlot;
		var ranges = meshlets.RunBytesHeld;
		var slot = meshlets.RunSlotCovering( tables.DrawRecords );
		Assert.AreNotEqual( runs, slot, "the run table grew for the draws it didn't cover" );
		Assert.IsTrue( meshlets.DrawIndexBytesHeld >= tables.DrawRecords * sizeof( uint ) );
		Assert.AreEqual( ranges, meshlets.RunBytesHeld, "draws without meshlets don't grow the range runs" );
		Assert.AreEqual( slot, meshlets.RunSlotCovering( tables.DrawRecords ), "and doesn't grow again while it covers them" );
		Device.WaitIdle();
	}

	[TestMethod]
	public void LodTableDoesNotGrowForMoreDrawsOfOneRange()
	{
		RequireMeshOptimizer();
		using var tables = new GpuDrivenTables( Device ) { LevelRoom = 1 };
		using var lods = new LodStore( Device, tables, LodConfig.Default with { MaxLevels = 1 }, 1 );
		tables.Lods = lods;
		var range = RequestLod( tables, lods, color: true );
		uint Draw( uint material ) => tables.Draw( 0, material, material, 2, range.FirstIndex, range.IndexCount, range.BaseVertex, range.PageSlot );
		Draw( 1 );
		WaitForLodBuild( lods );
		Assert.IsTrue( lods.TryGetLevels( range, out var placed, out _ ) );
		Assert.AreEqual( 1, placed.Length );
		var rangeBytes = lods.RangeBytesHeld;
		var slots = lods.Table;

		for ( uint material = 2; material <= 8193; material++ ) Draw( material );
		Device.BeginFrame();
		lods.BeginFrame();
		Assert.AreEqual( rangeBytes, lods.RangeBytesHeld, "the draws share one LOD range record" );
		Assert.AreEqual( slots.Ranges, lods.Table.Ranges );
		Assert.AreNotEqual( slots.Draws, lods.Table.Draws, "only the draw links and level draw IDs grew" );

		var (first, count) = placed[0];
		var draws = tables.DrawRecords;
		var firstLevel = tables.Draw( 0, 1, 1, 2, first, count, range.BaseVertex, range.PageSlot );
		var lastLevel = tables.Draw( 0, 8193, 8193, 2, first, count, range.BaseVertex, range.PageSlot );
		Assert.AreNotEqual( firstLevel, lastLevel, "level draws keep the base draws' materials" );
		Assert.AreEqual( draws, tables.DrawRecords, "both level draw records already exist" );
		Device.WaitIdle();
	}

	[TestMethod]
	public void BuiltLodWaitingForColourDoesNotPinItsIndexPage()
	{
		RequireMeshOptimizer();
		using var tables = new GpuDrivenTables( Device ) { LevelRoom = 1 };
		using var lods = new LodStore( Device, tables, LodConfig.Default, 1 );
		var range = RequestLod( tables, lods, color: false );
		WaitForLodBuild( lods );
		Assert.AreEqual( 1, lods.WaitingForColor.Ranges );
		Assert.IsTrue( lods.TryForgetPage( range.PageSlot ), "a range still waiting for colour won't upload" );
		Assert.AreEqual( 0, lods.WaitingForColor.Ranges );
		Assert.IsFalse( lods.TryGetLevels( range, out _, out _ ) );
		Device.WaitIdle();
	}

	static void RequireMeshOptimizer()
	{
		var core = ContentFiles.FindGame().Roots.FirstOrDefault();
		if ( core is not null ) MeshOptimizer.Directory = EngineFolders.NativeIn( Path.GetDirectoryName( core ) );
		if ( !MeshOptimizer.Available ) Assert.Inconclusive( "meshopt isn't built" );
	}

	static MeshletStore.Range RequestLod( GpuDrivenTables tables, LodStore lods, bool color )
	{
		const int Side = 20;
		var vertices = new V3[(Side + 1) * (Side + 1)];
		for ( int y = 0; y <= Side; y++ )
			for ( int x = 0; x <= Side; x++ ) vertices[y * (Side + 1) + x] = new V3( x, y, 0 );

		var indices = new List<uint>();
		for ( int y = 0; y < Side; y++ )
		{
			for ( int x = 0; x < Side; x++ )
			{
				var a = (uint)(y * (Side + 1) + x);
				indices.AddRange( [a, a + 1, a + Side + 1, a + 1, a + Side + 2, a + Side + 1] );
			}
		}

		var bytes = MemoryMarshal.AsBytes( CollectionsMarshal.AsSpan( indices ) ).ToArray();
		var (page, slot, first) = tables.AddIndices( bytes, IndexFormat.UInt32, bytes.Length );
		var range = new MeshletStore.Range( slot, first, (uint)indices.Count, 0 );
		var position = new Borealis.Source2.Models.VertexField( "POSITION", 0, ColorFormat.R32G32B32_FLOAT, 0, 0, false, 0 );
		var vertexBuffer = new MeshVertexBuffer( vertices.Length, 12, [position], MemoryMarshal.AsBytes( vertices.AsSpan() ).ToArray() );
		var indexBuffer = new MeshIndexBuffer( indices.Count, 4, bytes );
		lods.Request( range, new MeshletStore.Source( page, IndexFormat.UInt32, indexBuffer, 0, vertexBuffer, position, 0 ), color );
		return range;
	}

	void WaitForLodBuild( LodStore lods )
	{
		for ( int i = 0; i < 300 && lods.Built == 0; i++ )
		{
			Device.BeginFrame();
			lods.BeginFrame();
			if ( lods.Built == 0 ) Thread.Sleep( 10 );
		}

		Assert.AreEqual( 1L, lods.Built );
	}

	/// <summary>
	/// Table growth keeps the old buffers for recording frames, but once they retire their large allocations go back
	/// without leaving a mostly empty mapped block beside the live table or keeping spares of obsolete sizes.
	/// </summary>
	[TestMethod]
	public void GrowingTableDoesNotStrandLargeMappedBlocks()
	{
		using var tables = new GpuDrivenTables( Device );
		for ( int i = 0; i < 5; i++ )
		{
			var key = new GpuDrivenTables.MaterialRecordKey( null, i, null, false, 0 );
			tables.Material( key, 1 << (20 + i), 0, static ( _, bytes ) => bytes.Clear() );
		}

		var buffers = Device.ListResources().Buffers.Where( b => b.Name == "Borealis GPU-driven materials" ).OrderBy( b => b.Size ).ToArray();
		Assert.AreEqual( 6, buffers.Length, "1, 2, 4, 8, 16 and 32 MiB generations" );
		var large = buffers.Skip( 3 ).Select( b => Device.Get( b.Handle ).Memory ).ToArray();
		Device.WaitIdle();
		for ( ulong i = 1; i < GpuDrivenTables.RetiredFrames; i++ ) Device.BeginFrame();
		tables.FreeEmptyPages();
		Assert.IsTrue( buffers.All( b => Device.IsAlive( b.Handle ) ), "the old buffers still have their slots" );

		Device.BeginFrame();
		tables.FreeEmptyPages();
		Device.BeginFrame();
		Assert.IsTrue( buffers.Take( buffers.Length - 1 ).All( b => !Device.IsAlive( b.Handle ) ), "the old generations retired" );
		Assert.IsTrue( Device.IsAlive( buffers[^1].Handle ) );
		Assert.AreEqual( large[^1].Size, large[^1].Block.Size, "the live table doesn't pin unused room in a large block" );
		for ( int i = 0; i < large.Length - 1; i++ )
		{
			Assert.AreEqual( 0ul, large[i].Block.Used, "the retired table's block emptied" );
			Assert.IsFalse( large[i].Block.Spare, "its size won't be needed on the next growth" );
		}
	}
}
