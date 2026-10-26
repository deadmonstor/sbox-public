using System.Collections.Concurrent;
using System.Diagnostics;

namespace BotSwarm;

/// <summary>
/// Counters shared by every bot. Updated with Interlocked from many threads and printed once a second.
/// </summary>
internal sealed class Stats
{
	public long Connecting;
	public long Handshaking;
	public long Active;

	public long Joins;
	public long Kicks;
	public long Failures;
	public long Disconnects;
	public long Churned;

	public long BytesIn;
	public long BytesOut;
	public long MessagesIn;
	public long MessagesOut;

	public long ChatSent;
	public long MovesAcked;
	public long MovesRejected;
	public long SendsDropped;
	public long UnknownMessages;
	public long DecodeErrors;

	// Host ClientTick messages received, summed over bots. The host sends one per network tick, so divided by
	// active bots this is the server's network tick rate, which drops below its update rate once frames run long.
	public long ServerTicks;

	// Longest gap between two server heartbeats seen by any bot this interval, in ticks.
	// The server sends one a second from its network tick, so this tracks server hitches.
	long maxHeartbeatGap;

	readonly ConcurrentQueue<double> joinTimes = new();
	readonly List<double> allJoinTimes = new();
	readonly ConcurrentDictionary<string, int> reasons = new();
	readonly Stopwatch clock = Stopwatch.StartNew();

	long lastBytesIn, lastBytesOut, lastMessagesIn, lastMessagesOut, lastJoins, lastServerTicks;
	double lastTime;

	public void JoinTime( double seconds ) => joinTimes.Enqueue( seconds );

	public void Reason( string kind, string reason ) => reasons.AddOrUpdate( $"{kind}: {reason}", 1, ( _, n ) => n + 1 );

	public void HeartbeatGap( long ticks )
	{
		var current = Interlocked.Read( ref maxHeartbeatGap );
		while ( ticks > current )
		{
			var seen = Interlocked.CompareExchange( ref maxHeartbeatGap, ticks, current );
			if ( seen == current ) return;
			current = seen;
		}
	}

	public string Line( int target, int moving )
	{
		var now = clock.Elapsed.TotalSeconds;
		var dt = Math.Max( now - lastTime, 0.001 );
		lastTime = now;

		var interval = new List<double>();
		while ( joinTimes.TryDequeue( out var t ) ) interval.Add( t );
		allJoinTimes.AddRange( interval );
		interval.Sort();

		static long Delta( long value, ref long last )
		{
			var d = value - last;
			last = value;
			return d;
		}

		var joins = Delta( Interlocked.Read( ref Joins ), ref lastJoins );
		var bytesIn = Delta( Interlocked.Read( ref BytesIn ), ref lastBytesIn ) / dt;
		var bytesOut = Delta( Interlocked.Read( ref BytesOut ), ref lastBytesOut ) / dt;
		var msgIn = Delta( Interlocked.Read( ref MessagesIn ), ref lastMessagesIn ) / dt;
		var msgOut = Delta( Interlocked.Read( ref MessagesOut ), ref lastMessagesOut ) / dt;
		var gap = Interlocked.Exchange( ref maxHeartbeatGap, 0 ) / (double)Stopwatch.Frequency;
		var active = Interlocked.Read( ref Active );
		var serverTicks = Delta( Interlocked.Read( ref ServerTicks ), ref lastServerTicks ) / dt / Math.Max( active, 1 );

		var joinText = interval.Count > 0
			? $"join p50 {Percentile( interval, 0.5 ):0.00}s p95 {Percentile( interval, 0.95 ):0.00}s"
			: "join -";

		return $"[{now,6:0}s] active {Active,4}/{target} joining {Handshaking + Connecting,3} | +{joins} joins, {joinText} | " +
			$"kick {Kicks} fail {Failures} drop {Disconnects} churn {Churned} | " +
			$"in {Size( bytesIn )}/s {msgIn:0}msg/s out {Size( bytesOut )}/s {msgOut:0}msg/s | " +
			$"moving {moving} (rejected {MovesRejected}) chat {ChatSent} | send drops {SendsDropped} | hb gap {(gap > 0 ? $"{gap:0.00}s" : "-")} | " +
			$"server {serverTicks:0.0} ticks/s";
	}

	public string Summary()
	{
		allJoinTimes.Sort();

		var lines = new List<string>
		{
			$"Joins {Joins}, kicks {Kicks}, failures {Failures}, drops {Disconnects}, churned {Churned}",
			$"Received {Size( BytesIn )} in {MessagesIn} messages, sent {Size( BytesOut )} in {MessagesOut} messages",
			$"Movement clusters acked {MovesAcked}, object updates rejected {MovesRejected}; chat messages {ChatSent}; sends dropped (server not reading) {SendsDropped}",
		};

		if ( allJoinTimes.Count > 0 )
			lines.Add( $"Join time p50 {Percentile( allJoinTimes, 0.5 ):0.00}s p95 {Percentile( allJoinTimes, 0.95 ):0.00}s max {allJoinTimes[^1]:0.00}s" );

		if ( UnknownMessages > 0 || DecodeErrors > 0 )
			lines.Add( $"Unparsed messages {UnknownMessages}, decode errors {DecodeErrors}" );

		foreach ( var (reason, count) in reasons.OrderByDescending( x => x.Value ) )
			lines.Add( $"  {count,5} x {reason}" );

		return string.Join( Environment.NewLine, lines );
	}

	static double Percentile( List<double> sorted, double p ) => sorted[Math.Clamp( (int)Math.Ceiling( p * sorted.Count ) - 1, 0, sorted.Count - 1 )];

	static string Size( double bytes ) => bytes switch
	{
		>= 1024 * 1024 => $"{bytes / (1024 * 1024):0.0}MB",
		>= 1024 => $"{bytes / 1024:0.0}KB",
		_ => $"{bytes:0}B"
	};
}
