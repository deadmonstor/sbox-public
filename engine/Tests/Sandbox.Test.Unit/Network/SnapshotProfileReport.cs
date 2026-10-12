using System;
using System.Globalization;
using System.IO;
using Editor.Diagnostics;

namespace NetworkTests;

[TestClass]
public class SnapshotProfileReportTest
{
	private const string Metadata = "# elapsed_seconds=47.9970751,stopwatch_frequency=10000000,active=False,dropped_detail_samples=0\n";
	private const string Header = "scope,metric,connection,object,version,slot,count,value,unit\n";
	private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";
	private const string MessageTotal = "total,MessageBytes," + EmptyGuid + "," + EmptyGuid + ",0,0,708,2186178,bytes\n";

	private static SnapshotProfileReport Parse( string csv )
	{
		using var reader = new StringReader( csv );
		return SnapshotProfileReport.Parse( reader );
	}

	[TestMethod]
	public void CapturedSchemaPreservesAggregatesAndConvertsTimingRegardlessOfLocale()
	{
		var previous = CultureInfo.CurrentCulture;
		try
		{
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo( "fr-FR" );
			// Rows from a real capture; the file is deliberately not an external test dependency.
			var report = Parse( "# Snapshot profiler: outbound snapshot protocol bytes, not transport wire bytes\n\n"
				+ Metadata + Header + MessageTotal
				+ "total,SendTicks," + EmptyGuid + "," + EmptyGuid + ",0,0,708,858468,stopwatch_ticks\n"
				+ "total,Acknowledged," + EmptyGuid + "," + EmptyGuid + ",0,0,35716,35716,count\n"
				+ "# detail rows follow\n"
				+ "detail,MessageBytes,4b4aa67e-cc36-4fa8-9eb0-8dd368476792," + EmptyGuid + ",0,0,708,2186178,bytes\n"
				+ "detail,FieldBytes,4b4aa67e-cc36-4fa8-9eb0-8dd368476792,009ac57e-7574-4c70-ba51-197050c8660d,65535,-2147483648,2,16,bytes\n" );

			Assert.AreEqual( 47.9970751, report.ElapsedSeconds, 0.000000001 );
			Assert.AreEqual( 10000000L, report.StopwatchFrequency );
			Assert.IsFalse( report.Active );
			Assert.AreEqual( 0L, report.DroppedDetailSamples );
			Assert.AreEqual( 3, report.Totals.Count );
			Assert.AreEqual( 2, report.Details.Count );
			var messages = report.Totals.Single( x => x.Metric == "MessageBytes" );
			Assert.AreEqual( 708L, messages.Count );
			Assert.AreEqual( 2186178L, messages.Value );
			Assert.AreEqual( 2186178.0, report.DisplayValue( messages ) );
			Assert.AreEqual( Guid.Empty, messages.Connection );
			Assert.AreEqual( Guid.Empty, messages.ObjectId );
			Assert.AreEqual( 85.8468, report.DisplayValue( report.Totals.Single( x => x.Metric == "SendTicks" ) ), 0.000001 );
			Assert.AreEqual( 35716.0, report.DisplayValue( report.Totals.Single( x => x.Metric == "Acknowledged" ) ) );
			var field = report.Details.Single( x => x.Metric == "FieldBytes" );
			Assert.AreEqual( int.MinValue, field.Slot );
			Assert.AreEqual( ushort.MaxValue, field.Version );
			Assert.AreEqual( Guid.Parse( "009ac57e-7574-4c70-ba51-197050c8660d" ), field.ObjectId );
		}
		finally
		{
			CultureInfo.CurrentCulture = previous;
		}
	}

	[TestMethod]
	public void DroppedDetailDoesNotReplaceOrAddToAuthoritativeTotals()
	{
		var report = Parse( Metadata.Replace( "dropped_detail_samples=0", "dropped_detail_samples=42" )
			+ Header + MessageTotal
			+ "detail,MessageBytes," + EmptyGuid + "," + EmptyGuid + ",0,0,2,100,bytes\n" );

		Assert.AreEqual( 42L, report.DroppedDetailSamples );
		Assert.AreEqual( 708L, report.Totals.Single().Count );
		Assert.AreEqual( 2186178L, report.Totals.Single().Value );
		Assert.AreEqual( 2L, report.Details.Single().Count );
		Assert.AreEqual( 100L, report.Details.Single().Value );
	}

	[DataTestMethod]
	[DataRow( "stopwatch_frequency=10000000", "stopwatch_frequency=0" )]
	[DataRow( "stopwatch_frequency=10000000", "stopwatch_frequency=-1" )]
	[DataRow( "stopwatch_frequency=10000000", "stopwatch_frequency=9223372036854775808" )]
	[DataRow( "elapsed_seconds=47.9970751", "elapsed_seconds=NaN" )]
	[DataRow( "elapsed_seconds=47.9970751", "elapsed_seconds=Infinity" )]
	[DataRow( "elapsed_seconds=47.9970751", "elapsed_seconds=-1" )]
	[DataRow( "active=False", "active=1" )]
	[DataRow( "dropped_detail_samples=0", "dropped_detail_samples=-1" )]
	[DataRow( "elapsed_seconds=47.9970751", "elapsed_seconds=47,9970751" )]
	[DataRow( "active=False", "active=False,active=True" )]
	public void InvalidMetadataCannotProduceReport( string original, string replacement )
	{
		Assert.ThrowsException<FormatException>( () => Parse( Metadata.Replace( original, replacement ) + Header + MessageTotal ) );
	}

	[DataTestMethod]
	[DataRow( 0, "other" )]
	[DataRow( 1, "" )]
	[DataRow( 1, "\"MessageBytes\"" )]
	[DataRow( 2, "not-a-uuid" )]
	[DataRow( 3, "not-a-uuid" )]
	[DataRow( 4, "65536" )]
	[DataRow( 4, "-1" )]
	[DataRow( 5, "2147483648" )]
	[DataRow( 6, "-1" )]
	[DataRow( 6, "1.5" )]
	[DataRow( 7, "-1" )]
	[DataRow( 7, "9223372036854775808" )]
	[DataRow( 8, "milliseconds" )]
	public void InvalidRowFieldsCannotProduceReport( int column, string invalid )
	{
		var fields = MessageTotal.TrimEnd().Split( ',' );
		fields[column] = invalid;
		Assert.ThrowsException<FormatException>( () => Parse( Metadata + Header + string.Join( ',', fields ) ) );
	}

	[TestMethod]
	public void WrongColumnCountCannotProduceReport()
	{
		Assert.ThrowsException<FormatException>( () => Parse( Metadata + Header + "total,MessageBytes" ) );
		Assert.ThrowsException<FormatException>( () => Parse( Metadata + Header + MessageTotal.TrimEnd() + ",extra" ) );
	}

	[TestMethod]
	public void DuplicateTotalsAndInconsistentMetricUnitsAreRejected()
	{
		Assert.ThrowsException<FormatException>( () => Parse( Metadata + Header + MessageTotal + MessageTotal ) );
		Assert.ThrowsException<FormatException>( () => Parse( Metadata + Header + MessageTotal
			+ MessageTotal.Replace( "total,", "detail," ).Replace( ",bytes", ",count" ) ) );
		Assert.ThrowsException<FormatException>( () => Parse( Metadata + Header
			+ MessageTotal.Replace( "total,", "detail," ).Replace( ",bytes", ",count" ) + MessageTotal ) );
	}

	[DataTestMethod]
	[DataRow( "elapsed_seconds=47.9970751," )]
	[DataRow( "stopwatch_frequency=10000000," )]
	[DataRow( "active=False," )]
	[DataRow( ",dropped_detail_samples=0" )]
	public void EachRequiredMetadataFieldMustBePresent( string missing )
	{
		Assert.ThrowsException<FormatException>( () => Parse( Metadata.Replace( missing, "" ) + Header + MessageTotal ) );
	}

	[TestMethod]
	public void RequiredHeaderAndMetadataCannotBeOmitted()
	{
		Assert.ThrowsException<FormatException>( () => Parse( Header + MessageTotal ) );
		Assert.ThrowsException<FormatException>( () => Parse( Metadata + MessageTotal ) );
		Assert.ThrowsException<FormatException>( () => Parse( Metadata + Header.Replace( "object,", "object_id," ) + MessageTotal ) );
		Assert.ThrowsException<FormatException>( () => Parse( Metadata ) );
		Assert.ThrowsException<FormatException>( () => Parse( "" ) );
	}
}
