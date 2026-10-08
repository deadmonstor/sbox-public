using Sandbox;
using Sandbox.Diagnostics;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NetworkObjectLoad;

public sealed partial class NetworkObjectScenario
{
	[Property] public bool CaptureMetrics { get; set; } = true;
	[Property] public string RunLabel { get; set; } = "manual";
	[Property] public bool ProfiledRun { get; set; }
	private struct Sample
	{
		public double Seconds, FrameMs, NetworkMs, UpdateMs, RenderMs, EditorMs, IdleMs;
		public long Bytes, PauseTicks;
		public ulong Memory;
		public int Gen0, Gen1, Gen2, Exceptions, Tick;
	}
	private readonly Sample[] _samples = new Sample[120000];
	private int _sampleCount;
	private bool _previousFrameRunning, _sampleOverflow;
	private double _measureStarted, _measureEnded;

	private void BeginMeasurement()
	{
		_sampleCount = 0;
		_sampleOverflow = false;
		_previousFrameRunning = false;
		_measureStarted = RealTime.Now;
	}

	protected override void OnUpdate()
	{
		if ( !CaptureMetrics || !Networking.IsActive || !Networking.IsHost ) return;
		// Engine counters describe the preceding frame. Skip entry; include the final running frame.
		if ( _previousFrameRunning )
		{
			if ( _sampleCount == _samples.Length ) _sampleOverflow = true;
			else _samples[_sampleCount++] = new Sample
			{
				Seconds = RealTime.Now - _measureStarted,
				FrameMs = PerformanceStats.FrameTime * 1000,
				NetworkMs = PerformanceStats.Timings.Network.AverageMs( 1 ),
				UpdateMs = PerformanceStats.Timings.Update.AverageMs( 1 ),
				RenderMs = PerformanceStats.Timings.Render.AverageMs( 1 ),
				EditorMs = PerformanceStats.Timings.Editor.AverageMs( 1 ),
				IdleMs = PerformanceStats.Timings.Idle.AverageMs( 1 ),
				Bytes = PerformanceStats.BytesAllocated,
				PauseTicks = PerformanceStats.GcPause,
				Gen0 = PerformanceStats.Gen0Collections,
				Gen1 = PerformanceStats.Gen1Collections,
				Gen2 = PerformanceStats.Gen2Collections,
				Exceptions = PerformanceStats.Exceptions,
				Memory = PerformanceStats.ApproximateProcessMemoryUsage,
				Tick = _completedRunTicks
			};
		}
		_previousFrameRunning = _phase == Phase.Running;
	}

	private void ExportMeasurement( bool success, string reason )
	{
		if ( !CaptureMetrics ) return;
		var rows = _samples.Take( _sampleCount ).ToArray();
		bool valid = success && !_sampleOverflow && rows.Length > 0 && _completedRunTicks == RunTicks && rows.All( s => s.Exceptions == 0 );
		if ( _sampleOverflow ) reason = "Sample capacity exceeded; incomplete measurements excluded";
		else if ( rows.Any( s => s.Exceptions > 0 ) ) reason = "Engine exceptions during measurement; run excluded";
		var label = new string( RunLabel.Where( c => char.IsLetterOrDigit( c ) || c == '-' || c == '_' ).ToArray() );
		if ( label.Length == 0 ) label = "manual";
		string folder = $"network-object-load/{label}-{Guid.NewGuid():N}";
		FileSystem.Data.CreateDirectory( folder );
		var csv = new StringBuilder( "sample,seconds,frame_ms,network_ms,update_ms,render_ms,editor_ms,idle_ms,allocation_bytes,gen0,gen1,gen2,gc_pause_ticks,exceptions,working_set_bytes,completed_tick\n" );
		for ( int i = 0; i < rows.Length; i++ )
		{
			var s = rows[i];
			csv.AppendLine( string.Format( CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R},{8},{9},{10},{11},{12},{13},{14},{15}",
				i, s.Seconds, s.FrameMs, s.NetworkMs, s.UpdateMs, s.RenderMs, s.EditorMs, s.IdleMs, s.Bytes, s.Gen0, s.Gen1, s.Gen2, s.PauseTicks, s.Exceptions, s.Memory, s.Tick ) );
		}
		FileSystem.Data.WriteAllText( $"{folder}/samples.csv", csv.ToString() );
		double Percentile( Func<Sample, double> select, double percentile )
		{
			var ordered = rows.Select( select ).OrderBy( v => v ).ToArray();
			return ordered.Length == 0 ? 0 : ordered[Math.Max( 0, (int)Math.Ceiling( percentile * ordered.Length ) - 1 )];
		}
		double Mean( Func<Sample, double> select ) => rows.Length == 0 ? 0 : rows.Average( select );
		var summary = new
		{
			valid, reason, scenario = Scenario.ToString(), population = _subjects.Count, fakeConnections = _actualFakeConnections,
			samples = rows.Length, runTicks = _completedRunTicks, requestedTicks = RunTicks,
			actualSeconds = _measureEnded - _measureStarted, objectUpdates = _mutations, spawns = _spawns, destroys = _destroys, rpcs = _rpcs,
			meanMs = Mean( s => s.FrameMs ), medianMs = Percentile( s => s.FrameMs, 0.5 ),
			p95Ms = Percentile( s => s.FrameMs, 0.95 ), p99Ms = Percentile( s => s.FrameMs, 0.99 ),
			networkMeanMs = Mean( s => s.NetworkMs ), networkP99Ms = Percentile( s => s.NetworkMs, 0.99 ),
			updateMeanMs = Mean( s => s.UpdateMs ), renderMeanMs = Mean( s => s.RenderMs ), editorMeanMs = Mean( s => s.EditorMs ), idleMeanMs = Mean( s => s.IdleMs ),
			meanAllocationBytes = Mean( s => s.Bytes ), totalAllocationBytes = rows.Sum( s => s.Bytes ),
			peakWorkingSetBytes = rows.Length == 0 ? 0 : rows.Max( s => s.Memory ),
			gen0 = rows.Sum( s => s.Gen0 ), gen1 = rows.Sum( s => s.Gen1 ), gen2 = rows.Sum( s => s.Gen2 ),
			gcPauseMs = rows.Sum( s => s.PauseTicks ) / 10000.0, exceptions = rows.Sum( s => s.Exceptions ),
			estimator = "nearest-rank", mode = "editor host; record standalone separately", profiled = ProfiledRun,
			allocationScope = "engine frame counter; includes editor activity; worker coverage not guaranteed",
			timingScope = "preceding engine frame; Network is an elapsed engine scope, not whole server tick CPU; scopes may overlap"
		};
		var options = new JsonSerializerOptions { WriteIndented = true };
		FileSystem.Data.WriteAllText( $"{folder}/summary.json", JsonSerializer.Serialize( summary, options ) );
		FileSystem.Data.WriteAllText( $"{folder}/manifest.json", JsonSerializer.Serialize( new
		{
			RunLabel, Scenario, Population, FakeConnections, PropertyGroups, ActiveFraction, ChildrenPerObject, WarmupTicks, RunTicks,
			LifecycleIntervalTicks, LifecycleBurst, RpcsPerTick, RenderSubjects, MinimumClients, SettleSeconds, TimeoutSeconds,
			valid, reason, utc = DateTime.UtcNow.ToString( "O" ), replicationChecked = _replicationChecked, ProfiledRun,
			metricsVersion = 2, rawCountersLagOneFrame = true,
			actualFixedFrequency = ProjectSettings.Physics.FixedUpdateFrequency, actualNetworkFrequency = ProjectSettings.Networking.UpdateRate
		}, options ) );
		Log.Info( $"NETWORK METRICS {folder}: valid={valid}, samples={rows.Length}, mean={summary.meanMs:0.000}ms, network={summary.networkMeanMs:0.000}ms" );
	}
}
