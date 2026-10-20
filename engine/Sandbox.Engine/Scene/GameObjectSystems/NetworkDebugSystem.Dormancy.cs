namespace Sandbox;

sealed partial class NetworkDebugSystem
{
	[ConVar( "net_debug_dormancy", ConVarFlags.Protected | ConVarFlags.Cheat, Help = "Show network object names, dormancy states and observed state ages. Ages start when enabled, not at the historical state change." )]
	private static bool DebugDormancy { get; set; }

	[ConVar( "net_debug_dormancy_distance", ConVarFlags.Protected, Help = "Maximum distance for network dormancy labels, in world units." )]
	private static float DebugDormancyDistance { get; set; } = 1024f;

	[ConVar( "net_debug_dormancy_max_labels", ConVarFlags.Protected, Help = "Maximum number of network dormancy labels, nearest visible objects first." )]
	private static int DebugDormancyMaxLabels { get; set; } = 64;

	private enum DormancyState
	{
		RemoteUnknown,
		AwakeGrace,
		PendingAck,
		AckClean,
		VisibilitySleep
	}

	private struct DormancyObservation
	{
		public DormancyState State;
		public double StateSince;
		public ulong Seen;
	}

	private struct DormancyLabel
	{
		public NetworkObject Object;
		public Vector3 Position;
		public float DistanceSquared;
	}

	private sealed class DormancyTracking
	{
		public readonly Dictionary<Guid, DormancyObservation> Observations = new();
		public readonly List<Guid> Removed = new();
		public readonly List<DormancyLabel> Labels = new();
		public ulong Frame;
	}

	private DormancyTracking _dormancyTracking;
	private Scene.NetworkPollStats _pollStatsSample;
	private RealTimeSince _timeSincePollStatsSample;
	private string _pollStatsText;

	private static Scene.NetworkPollStats _pollStatsCommandSample;

	/// <summary>
	/// Average poll work per network tick between two cumulative samples.
	/// </summary>
	private static string FormatPollStats( in Scene.NetworkPollStats from, in Scene.NetworkPollStats to )
	{
		var ticks = to.Ticks - from.Ticks;
		if ( ticks <= 0 ) return "poll: no network ticks";

		double PerTick( long a, long b ) => (double)(b - a) / ticks;

		return $"poll/tick ({ticks} ticks): objects {PerTick( from.ObjectsVisited, to.ObjectsVisited ):0.#}"
			+ $" | entries collection {PerTick( from.CollectionEntries, to.CollectionEntries ):0.#}"
			+ $" query {PerTick( from.QueryEntries, to.QueryEntries ):0.#}"
			+ $" | probes {PerTick( from.VisibilityProbes, to.VisibilityProbes ):0.#}"
			+ $" | wakes poll {PerTick( from.PollWakes, to.PollWakes ):0.##} probe {PerTick( from.ProbeWakes, to.ProbeWakes ):0.##}";
	}

	[ConCmd( "net_poll_stats", ConVarFlags.Protected, Help = "Print average dormancy poll work per network tick since the previous call." )]
	internal static void PrintPollStats()
	{
		if ( Game.ActiveScene is not { } scene ) return;

		var current = scene.NetworkPollTotals;
		Log.Info( FormatPollStats( _pollStatsCommandSample, current ) );
		_pollStatsCommandSample = current;
	}

	private void DrawPollStats()
	{
		if ( _pollStatsText is null || _timeSincePollStatsSample >= 1f )
		{
			var current = Scene.NetworkPollTotals;
			_pollStatsText = FormatPollStats( _pollStatsSample, current );
			_pollStatsSample = current;
			_timeSincePollStatsSample = 0f;
		}

		Gizmo.Draw.Color = Color.White;
		Gizmo.Draw.ScreenText( _pollStatsText, new Vector2( 10, 30 ), flags: TextFlag.LeftTop );
	}

	private static DormancyState GetDormancyState( NetworkObject obj )
	{
		// A proxy's local flags cannot tell us whether its remote sender is asleep.
		if ( obj.IsProxy ) return DormancyState.RemoteUnknown;
		if ( obj.IsDeltaDormant ) return DormancyState.VisibilitySleep;
		if ( !obj.IsDirty ) return DormancyState.AckClean;
		return obj.IsFullyUpdated ? DormancyState.AwakeGrace : DormancyState.PendingAck;
	}

	private void DrawDormancy()
	{
		if ( !DebugDormancy )
		{
			_dormancyTracking = null;
			_pollStatsText = null;
			return;
		}

		var tracking = _dormancyTracking ??= new();
		var frame = ++tracking.Frame;
		var now = RealTime.NowDouble;
		using var scope = Gizmo.Scope();
		DrawPollStats();
		var camera = Gizmo.Camera;
		var distance = MathF.Max( 0f, DebugDormancyDistance );
		var distanceSquared = distance * distance;
		var maxLabels = Math.Max( 0, DebugDormancyMaxLabels );

		foreach ( var obj in Scene.networkedObjects )
		{
			if ( !obj.IsValid ) continue;

			var id = obj.Id;
			var state = GetDormancyState( obj );
			if ( !tracking.Observations.TryGetValue( id, out var observation ) )
			{
				observation = new DormancyObservation
				{
					State = state,
					StateSince = now
				};
			}

			if ( observation.State != state )
			{
				observation.State = state;
				observation.StateSince = now;
			}

			observation.Seen = frame;
			tracking.Observations[id] = observation;

			// Observe every object, even without a camera or outside the label range.
			if ( camera is null || maxLabels == 0 ) continue;
			var position = obj.GameObject.WorldPosition + Vector3.Up * 24f;
			var squared = (position - camera.Position).LengthSquared;
			if ( squared > distanceSquared ) continue;
			if ( !camera.ToScreen( position, out var screen ) ) continue;
			if ( screen.x < 0f || screen.y < 0f || screen.x > camera.Size.x || screen.y > camera.Size.y ) continue;

			tracking.Labels.Add( new DormancyLabel { Object = obj, Position = position, DistanceSquared = squared } );
		}

		// Records contain no object references, and removed objects lose their ages.
		foreach ( var (id, observation) in tracking.Observations )
		{
			if ( observation.Seen != frame ) tracking.Removed.Add( id );
		}
		foreach ( var id in tracking.Removed ) tracking.Observations.Remove( id );
		tracking.Removed.Clear();

		tracking.Labels.Sort( static ( a, b ) => a.DistanceSquared.CompareTo( b.DistanceSquared ) );
		Gizmo.Draw.IgnoreDepth = true;
		try
		{
			for ( var i = 0; i < Math.Min( maxLabels, tracking.Labels.Count ); i++ )
			{
				var label = tracking.Labels[i];
				var obj = label.Object;
				var observation = tracking.Observations[obj.Id];
				var (text, color) = observation.State switch
				{
					DormancyState.RemoteUnknown => ("proxy (remote unknown)", Color.Gray),
					DormancyState.VisibilitySleep => ("visibility sleep", Color.Orange),
					DormancyState.AckClean => ("ACK clean", Color.Green),
					DormancyState.AwakeGrace => ("awake grace", Color.Yellow),
					DormancyState.PendingAck => ("pending ACK", Color.Yellow),
					_ => ("dirty", Color.Yellow)
				};
				var message = $"{obj.GameObject.Name}\n{text} | observed {now - observation.StateSince:0.0}s";

				using ( Gizmo.ObjectScope( obj, new Transform( label.Position ) ) )
				{
					Gizmo.Draw.Color = color;
					Gizmo.Draw.Text( message, Transform.Zero, size: 12f );
				}
			}
		}
		finally
		{
			// Reuse the buffer without retaining references to destroyed objects.
			tracking.Labels.Clear();
		}
	}
}
