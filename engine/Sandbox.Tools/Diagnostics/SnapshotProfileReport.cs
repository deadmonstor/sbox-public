using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Editor.Diagnostics;

/// <summary>
/// An offline aggregate capture. Totals are authoritative; detail rows may be incomplete.
/// </summary>
internal sealed class SnapshotProfileReport
{
	internal readonly record struct Entry( string Metric, Guid Connection, Guid ObjectId, ushort Version,
		int Slot, long Count, long Value, string Unit );

	internal double ElapsedSeconds { get; }
	internal long StopwatchFrequency { get; }
	internal bool Active { get; }
	internal long DroppedDetailSamples { get; }
	internal IReadOnlyList<Entry> Totals { get; }
	internal IReadOnlyList<Entry> Details { get; }

	private SnapshotProfileReport( double elapsedSeconds, long stopwatchFrequency, bool active,
		long droppedDetailSamples, List<Entry> totals, List<Entry> details )
	{
		ElapsedSeconds = elapsedSeconds;
		StopwatchFrequency = stopwatchFrequency;
		Active = active;
		DroppedDetailSamples = droppedDetailSamples;
		Totals = totals.AsReadOnly();
		Details = details.AsReadOnly();
	}

	internal double DisplayValue( Entry entry ) => entry.Unit == "stopwatch_ticks"
		? (double)entry.Value / StopwatchFrequency * 1000.0
		: entry.Value;

	internal static SnapshotProfileReport Parse( TextReader reader )
	{
		ArgumentNullException.ThrowIfNull( reader );
		const string header = "scope,metric,connection,object,version,slot,count,value,unit";
		var metadata = new Dictionary<string, string>( StringComparer.Ordinal );
		var totals = new List<Entry>();
		var details = new List<Entry>();
		var totalMetrics = new HashSet<string>( StringComparer.Ordinal );
		var units = new Dictionary<string, string>( StringComparer.Ordinal );
		double elapsed = 0;
		long frequency = 0;
		long dropped = 0;
		bool active = false;
		bool hasHeader = false;
		int lineNumber = 0;
		string line = null;

		FormatException Invalid( string reason ) => new( $"Line {lineNumber}: {reason} [{line ?? "end of file"}]" );

		while ( (line = reader.ReadLine()) is not null )
		{
			lineNumber++;
			if ( string.IsNullOrWhiteSpace( line ) ) continue;
			if ( line.StartsWith( '#' ) )
			{
				var metadataFields = line[1..].Trim().Split( ',' );
				bool isMetadata = false;
				foreach ( var field in metadataFields )
				{
					var separator = field.IndexOf( '=' );
					var key = (separator < 0 ? field : field[..separator]).Trim();
					if ( key is "elapsed_seconds" or "stopwatch_frequency" or "active" or "dropped_detail_samples" )
						isMetadata = true;
				}
				if ( !isMetadata ) continue;
				if ( hasHeader ) throw Invalid( "Capture metadata must precede the CSV header." );
				foreach ( var field in metadataFields )
				{
					var separator = field.IndexOf( '=' );
					if ( separator < 0 ) throw Invalid( "Malformed capture metadata." );
					var key = field[..separator].Trim();
					if ( key is not ("elapsed_seconds" or "stopwatch_frequency" or "active" or "dropped_detail_samples") )
						throw Invalid( $"Unknown capture metadata field '{key}'." );
					if ( !metadata.TryAdd( key, field[(separator + 1)..].Trim() ) )
						throw Invalid( $"Duplicate metadata field '{key}'." );
				}
				continue;
			}

			if ( !hasHeader )
			{
				if ( line != header ) throw Invalid( "Expected the snapshot profile CSV header." );
				if ( !metadata.TryGetValue( "elapsed_seconds", out var elapsedText )
					|| !metadata.TryGetValue( "stopwatch_frequency", out var frequencyText )
					|| !metadata.TryGetValue( "active", out var activeText )
					|| !metadata.TryGetValue( "dropped_detail_samples", out var droppedText ) )
					throw Invalid( "Missing required capture metadata." );
				if ( !double.TryParse( elapsedText, NumberStyles.Float, CultureInfo.InvariantCulture, out elapsed )
					|| !double.IsFinite( elapsed ) || elapsed < 0 )
					throw Invalid( "Elapsed seconds must be finite and nonnegative." );
				if ( !long.TryParse( frequencyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out frequency ) || frequency <= 0 )
					throw Invalid( "Stopwatch frequency must be positive." );
				if ( !bool.TryParse( activeText, out active ) ) throw Invalid( "Active must be a boolean." );
				if ( !long.TryParse( droppedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out dropped ) || dropped < 0 )
					throw Invalid( "Dropped detail samples must be nonnegative." );
				hasHeader = true;
				continue;
			}

			var fields = line.Split( ',' );
			if ( fields.Length != 9 || line.Contains( '"' ) ) throw Invalid( "Expected nine unquoted CSV columns." );
			if ( fields[0] is not ("total" or "detail") ) throw Invalid( "Scope must be total or detail." );
			if ( string.IsNullOrWhiteSpace( fields[1] ) ) throw Invalid( "Metric must not be empty." );
			if ( !Guid.TryParseExact( fields[2], "D", out var connection ) || !Guid.TryParseExact( fields[3], "D", out var objectId ) )
				throw Invalid( "Connection and object must be UUIDs." );
			if ( !ushort.TryParse( fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version ) )
				throw Invalid( "Version must be an unsigned 16-bit integer." );
			if ( !int.TryParse( fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot ) )
				throw Invalid( "Slot must be a signed 32-bit integer." );
			if ( !long.TryParse( fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count ) || count < 0
				|| !long.TryParse( fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value ) || value < 0 )
				throw Invalid( "Count and value must be nonnegative 64-bit integers." );
			if ( fields[8] is not ("bytes" or "stopwatch_ticks" or "count") ) throw Invalid( "Unsupported unit." );
			if ( units.TryGetValue( fields[1], out var unit ) && unit != fields[8] )
				throw Invalid( $"Inconsistent units for metric '{fields[1]}'." );
			units[fields[1]] = fields[8];
			var entry = new Entry( fields[1], connection, objectId, version, slot, count, value, fields[8] );
			if ( fields[0] == "total" )
			{
				if ( !totalMetrics.Add( entry.Metric ) ) throw Invalid( $"Duplicate total metric '{entry.Metric}'." );
				totals.Add( entry );
			}
			else
			{
				details.Add( entry );
			}
		}

		if ( !hasHeader ) throw Invalid( "Missing CSV header and required capture metadata." );
		return new SnapshotProfileReport( elapsed, frequency, active, dropped, totals, details );
	}
}
