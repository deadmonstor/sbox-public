using Borealis.Backend;
using Borealis.Gpu;

namespace BorealisGpuTests;

/// <summary>
/// GPU-driven index pages (docs/reference/borealis-gpu-driven.md, "Generated LODs"): meshes' indices fill a page from its
/// start and generated levels from its end, and a page keeps free only the room for the levels its meshes' ranges may get.
/// </summary>
[TestClass]
[DoNotParallelize]
public class IndexPageTests : GpuTest
{
	[TestMethod]
	public void SparseIndexFormatStartsSmallAndBusyFormatReachesTheCap()
	{
		using var tables = new GpuDrivenTables( Device ) { LevelRoom = 0.75f };

		// Even with room for levels, the dump's 0.2 MiB of UInt16 indices needs only a small page.
		var indices = new byte[200 << 10];
		var (_, narrow, _) = tables.AddIndices( indices, IndexFormat.UInt16, rangeBytes: indices.Length );
		Assert.AreEqual( 512L << 10, tables.Bytes.Pages );
		Assert.AreEqual( 150L << 10, tables.LevelBytes.Room );

		// Fill successive pages of another format. Once at the cap its next shared page stays that size.
		const int firstSize = 512 << 10;
		var data = new byte[GpuDrivenTables.IndexPageSize / 4];
		var firstMesh = data.AsSpan( 0, firstSize );
		var (_, wide, _) = tables.AddIndices( firstMesh, IndexFormat.UInt32 );
		Assert.AreNotEqual( narrow, wide );
		var size = tables.Bytes.Pages - firstSize;
		Assert.AreEqual( (long)firstSize, size );
		while ( true )
		{
			var before = tables.Bytes.Pages;
			for ( long bytes = firstSize; bytes < size; )
			{
				var count = (int)Math.Min( size - bytes, data.Length );
				var (_, slot, _) = tables.AddIndices( data.AsSpan( 0, count ), IndexFormat.UInt32 );
				Assert.AreEqual( wide, slot );
				bytes += count;
			}

			var (_, next, first) = tables.AddIndices( firstMesh, IndexFormat.UInt32 );
			Assert.AreNotEqual( wide, next );
			Assert.AreEqual( 0u, first );
			var grown = tables.Bytes.Pages - before;
			if ( size == GpuDrivenTables.IndexPageSize )
			{
				Assert.AreEqual( size, grown );
				break;
			}

			Assert.IsTrue( grown > size && grown <= GpuDrivenTables.IndexPageSize );
			wide = next;
			size = grown;
		}
		Device.WaitIdle();
	}

	[TestMethod]
	public void PagesKeepRoomOnlyForLevelCandidatesAndFillLevelsFromTheEnd()
	{
		using var tables = new GpuDrivenTables( Device ) { LevelRoom = 0.5f };
		const int candidateBytes = 512 << 10;
		const int reserved = candidateBytes / 2;
		const int plainBytes = 16 << 10;

		// A mesh whose ranges may all get levels keeps half its indices again free; one with none keeps nothing
		var (_, slot, first) = tables.AddIndices( new byte[candidateBytes], IndexFormat.UInt16, rangeBytes: candidateBytes );
		Assert.AreEqual( 0u, first );
		Assert.AreEqual( ((long)reserved, 0L), tables.LevelBytes, "room for the candidate's levels, none used" );
		var pageSize = (int)tables.Bytes.Pages;
		Assert.IsTrue( pageSize >= candidateBytes + reserved, "the first page is sized for the mesh and its levels together" );
		var (_, sameSlot, second) = tables.AddIndices( new byte[plainBytes], IndexFormat.UInt16 );
		Assert.AreEqual( slot, sameSlot );
		Assert.AreEqual( (uint)(candidateBytes / 2), second, "after the first mesh's indices: the room isn't between them" );
		Assert.AreEqual( (long)reserved, tables.LevelBytes.Room );

		// Levels go from the page's end down, taking the room
		Assert.IsTrue( tables.TryAddLevelIndices( slot, IndexFormat.UInt16, 1000, out _, out var level ) );
		Assert.AreEqual( (uint)((pageSize - 1008) / 2), level );
		Assert.IsTrue( tables.TryAddLevelIndices( slot, IndexFormat.UInt16, 2000, out _, out var next ) );
		Assert.AreEqual( level - 2000 / 2, next );
		Assert.AreEqual( (reserved - 3008L, 3008L), tables.LevelBytes );

		// A mesh that would cut into the room left goes in a new page; one that fits doesn't
		var free = pageSize - candidateBytes - plainBytes - reserved;
		var (_, fits, _) = tables.AddIndices( new byte[free], IndexFormat.UInt16 );
		Assert.AreEqual( slot, fits, "exactly the space between meshes and the room kept" );
		var (_, other, otherFirst) = tables.AddIndices( new byte[16], IndexFormat.UInt16 );
		Assert.AreNotEqual( slot, other );
		Assert.AreEqual( 0u, otherFirst );

		// The full page's room is still there for its levels
		Assert.IsTrue( tables.TryAddLevelIndices( slot, IndexFormat.UInt16, reserved - 3008, out _, out _ ) );
		Assert.IsFalse( tables.TryAddLevelIndices( slot, IndexFormat.UInt16, 16, out _, out _ ), "and then it's full" );
		Device.WaitIdle();
	}
}
