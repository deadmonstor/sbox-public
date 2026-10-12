using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Sandbox.Network;

/// <summary>
/// Process-local, aggregate snapshot capture. Values are protocol bytes, counts, or stopwatch ticks;
/// never property contents. Accessed on the replication thread only.
/// </summary>
internal sealed class SnapshotProfileCapture
{
	internal enum Metric
	{
		MessageBytes,
		SharedHeaderBytes,
		ObjectHeaderBytes,
		FieldBytes,
		RelevancyTicks,
		PreparationTicks,
		SendTicks,
		AckTicks,
		NotAuthoritative,
		NotRelevant,
		Filtered,
		Acknowledged,
		Empty,
		BaselineMatch,
		ReceiptPending,
		ReceiptExpired,
		AckUnmatched,
		SnapshotRejected,
		ClusterPruned,
		SnapshotPruned
	}

	internal readonly record struct Key( Metric Metric, Guid Connection, Guid Object, ushort Version, int Slot );
	internal readonly record struct Sample( long Count, long Value );
	private readonly Dictionary<Key, Sample> _samples = new();
	private readonly Sample[] _totals = new Sample[Enum.GetValues<Metric>().Length];
	private readonly int _capacity;
	private readonly long _started = Stopwatch.GetTimestamp();
	private long _stopped;
	internal bool Active { get; private set; } = true;
	internal long DroppedSamples { get; private set; }

	internal SnapshotProfileCapture( int capacity = 16384 )
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( capacity );
		_capacity = capacity;
	}

	internal void Stop()
	{
		if ( !Active ) return;
		_stopped = Stopwatch.GetTimestamp();
		Active = false;
	}

	internal void Record( Metric metric, Guid connection = default, Guid objectId = default,
		ushort version = 0, int slot = 0, long value = 1 )
	{
		if ( !Active ) return;
		ref var total = ref _totals[(int)metric];
		total = new( total.Count + 1, total.Value + value );
		var key = new Key( metric, connection, objectId, version, slot );
		if ( !_samples.TryGetValue( key, out var sample ) && _samples.Count >= _capacity )
		{
			DroppedSamples++;
			return;
		}
		_samples[key] = new( sample.Count + 1, sample.Value + value );
	}

	internal Timing Measure( Metric metric, Guid connection = default, Guid objectId = default )
		=> new( this, metric, connection, objectId );

	internal readonly struct Timing : IDisposable
	{
		private readonly SnapshotProfileCapture _capture;
		private readonly Metric _metric;
		private readonly Guid _connection;
		private readonly Guid _object;
		private readonly long _start;

		internal Timing( SnapshotProfileCapture capture, Metric metric, Guid connection, Guid objectId )
		{
			_capture = capture;
			_metric = metric;
			_connection = connection;
			_object = objectId;
			_start = Stopwatch.GetTimestamp();
		}

		public void Dispose()
		{
			_capture?.Record( _metric, _connection, _object, value: Stopwatch.GetTimestamp() - _start );
		}
	}

	internal string Export()
	{
		var elapsed = (Active ? Stopwatch.GetTimestamp() : _stopped) - _started;
		var output = new StringBuilder();
		output.AppendLine( "# Snapshot profiler: outbound snapshot protocol bytes, not transport wire bytes" );
		output.AppendLine( "# Timing scopes: SendTicks includes selection/serialization/transport submission; preparation and relevancy are separate; no property values captured" );
		output.AppendLine( $"# elapsed_seconds={((double)elapsed / Stopwatch.Frequency).ToString( "R", CultureInfo.InvariantCulture )},stopwatch_frequency={Stopwatch.Frequency},active={Active},dropped_detail_samples={DroppedSamples}" );
		output.AppendLine( "scope,metric,connection,object,version,slot,count,value,unit" );
		for ( var i = 0; i < _totals.Length; i++ )
		{
			if ( _totals[i].Count > 0 ) Append( "total", new( (Metric)i, default, default, 0, 0 ), _totals[i] );
		}
		foreach ( var (key, sample) in _samples.OrderBy( x => x.Key.Metric ).ThenBy( x => x.Key.Connection )
			.ThenBy( x => x.Key.Object ).ThenBy( x => x.Key.Version ).ThenBy( x => x.Key.Slot ) )
		{
			Append( "detail", key, sample );
		}
		return output.ToString();

		void Append( string scope, Key key, Sample sample )
		{
			var unit = key.Metric switch
			{
				Metric.MessageBytes or Metric.SharedHeaderBytes or Metric.ObjectHeaderBytes or Metric.FieldBytes => "bytes",
				Metric.RelevancyTicks or Metric.PreparationTicks or Metric.SendTicks or Metric.AckTicks => "stopwatch_ticks",
				_ => "count"
			};
			output.AppendLine( FormattableString.Invariant( $"{scope},{key.Metric},{key.Connection},{key.Object},{key.Version},{key.Slot},{sample.Count},{sample.Value},{unit}" ) );
		}
	}
}
