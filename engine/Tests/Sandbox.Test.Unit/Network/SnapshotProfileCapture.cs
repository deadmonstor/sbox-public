using System;
using Sandbox.Network;

namespace NetworkTests;

[TestClass]
public class SnapshotProfileCaptureTest
{
	[TestMethod]
	public void CapacityOverflowPreservesTotalsAndExistingRows()
	{
		var capture = new SnapshotProfileCapture( capacity: 1 );
		var first = Guid.NewGuid();
		var second = Guid.NewGuid();
		capture.Record( SnapshotProfileCapture.Metric.FieldBytes, first, slot: 7, value: 10 );
		capture.Record( SnapshotProfileCapture.Metric.FieldBytes, second, slot: 7, value: 20 );
		capture.Record( SnapshotProfileCapture.Metric.FieldBytes, first, slot: 7, value: 30 );
		capture.Stop();

		var rows = capture.Export().Split( '\n' ).Where( x => !x.StartsWith( '#' ) )
			.Select( x => x.Trim().Split( ',' ) ).Where( x => x.Length == 9 && x[1] == "FieldBytes" ).ToArray();
		var total = rows.Single( x => x[0] == "total" );
		var detail = rows.Single( x => x[0] == "detail" );
		Assert.AreEqual( 3L, long.Parse( total[6] ) );
		Assert.AreEqual( 60L, long.Parse( total[7] ) );
		Assert.AreEqual( first, Guid.Parse( detail[2] ) );
		Assert.AreEqual( 2L, long.Parse( detail[6] ) );
		Assert.AreEqual( 40L, long.Parse( detail[7] ) );
		Assert.AreEqual( 1L, capture.DroppedSamples );
	}

	[TestMethod]
	public void StopFreezesCaptureIncludingOutstandingTimingScopes()
	{
		var capture = new SnapshotProfileCapture();
		capture.Record( SnapshotProfileCapture.Metric.MessageBytes, value: 42 );
		var timing = capture.Measure( SnapshotProfileCapture.Metric.AckTicks );
		capture.Stop();
		var before = capture.Export();
		capture.Record( SnapshotProfileCapture.Metric.MessageBytes, value: 100 );
		timing.Dispose();
		capture.Stop();
		Assert.IsFalse( capture.Active );
		Assert.AreEqual( before, capture.Export() );
	}
}
