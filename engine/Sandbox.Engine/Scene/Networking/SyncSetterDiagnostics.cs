using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Sandbox.Network;

/// <summary>
/// Opt-in, process-local counts of sync wrapper activity. Never retains component instances or values.
/// </summary>
internal static class SyncSetterDiagnostics
{
	internal sealed class Counts
	{
		internal long Calls;
		internal long Unchanged;
		internal long LocalChanges;
		internal long ReplicationUpdates;
		internal long Incoming;
	}

	private static readonly object Gate = new();
	private static readonly Dictionary<(string Type, string Property), Counts> Entries = new();
	private static readonly Stopwatch Duration = new();
	private static volatile bool Enabled;
	private static long Dropped;
	internal static bool IsEnabled => Enabled;

	internal static Counts Begin( string type, string property, bool incoming )
	{
		if ( !Enabled ) return null;
		lock ( Gate )
		{
			if ( !Enabled ) return null;
			var key = (type, property);
			if ( !Entries.TryGetValue( key, out var counts ) )
			{
				if ( Entries.Count >= 4096 )
				{
					Dropped++;
					return null;
				}
				Entries.Add( key, counts = new() );
			}
			Interlocked.Increment( ref counts.Calls );
			if ( incoming ) Interlocked.Increment( ref counts.Incoming );
			return counts;
		}
	}

	[ConCmd( "net_sync_track_start", ConVarFlags.Protected )]
	internal static void Start()
	{
		lock ( Gate )
		{
			Enabled = false;
			Entries.Clear();
			Dropped = 0;
			Duration.Restart();
			Enabled = true;
		}
		Log.Info( "Sync setter tracking started (counts only; instrumentation affects performance)." );
	}

	[ConCmd( "net_sync_track_stop", ConVarFlags.Protected )]
	internal static void Stop()
	{
		lock ( Gate )
		{
			Enabled = false;
			Duration.Stop();
		}
		Report();
	}

	[ConCmd( "net_sync_track_report", ConVarFlags.Protected )]
	internal static void Report()
	{
		string report;
		lock ( Gate )
		{
			var text = new StringBuilder();
			text.AppendLine( $"Sync setter counts: {Duration.Elapsed.TotalSeconds:F2}s, active={Enabled}, dropped calls={Dropped}" );
			text.AppendLine( "Ranked by local unequal-value assignments, across all instances of each property." );
			text.AppendLine( "Property\tCalls\tLocal changes\tUnchanged\tReplication updates\tIncoming\tLocal changes/sec" );
			foreach ( var row in Entries.OrderByDescending( x => x.Value.LocalChanges ).ThenByDescending( x => x.Value.Calls ) )
			{
				var c = row.Value;
				text.AppendLine( $"{row.Key.Type}.{row.Key.Property}\t{c.Calls}\t{c.LocalChanges}\t{c.Unchanged}\t{c.ReplicationUpdates}\t{c.Incoming}\t{c.LocalChanges / Math.Max( Duration.Elapsed.TotalSeconds, 0.001 ):F2}" );
			}
			report = text.ToString();
		}
		FileSystem.Data.WriteAllText( "sync-setters.txt", report );
		foreach ( var line in report.Split( '\n' ) ) Log.Info( line );
		Log.Info( "Saved sync-setters.txt in the process data filesystem. Run separately on host/client." );
	}
}
