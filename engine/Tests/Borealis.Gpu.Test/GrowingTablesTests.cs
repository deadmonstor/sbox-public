using Borealis.Backend;
using Borealis.Backend.Meshlets;

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
	/// the slots before knows to read them again; and the meshlet run table grows to cover every draw record before a cull
	/// reads it, since the culls read a run for every draw, with one or not.
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
		var slot = meshlets.RunSlotCovering( tables.DrawRecords );
		Assert.AreNotEqual( runs, slot, "the run table grew for the draws it didn't cover" );
		Assert.IsTrue( meshlets.RunBytesHeld >= tables.DrawRecords * MeshletStore.RunBytes );
		Assert.AreEqual( slot, meshlets.RunSlotCovering( tables.DrawRecords ), "and doesn't grow again while it covers them" );
		Device.WaitIdle();
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
