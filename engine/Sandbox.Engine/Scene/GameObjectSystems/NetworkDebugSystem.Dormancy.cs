namespace Sandbox;

sealed partial class NetworkDebugSystem
{
	[ConVar( "net_debug_dormancy", ConVarFlags.Protected | ConVarFlags.Cheat, Help = "Show network object names, dormancy states and observed state ages. Ages start when enabled, not at the historical state change." )]
	private static bool DebugDormancy { get; set; }

	[ConVar( "net_debug_dormancy_distance", ConVarFlags.Protected | ConVarFlags.Cheat, Help = "Maximum distance for network dormancy labels, in world units." )]
	private static float DebugDormancyDistance { get; set; } = 1024f;

	[ConVar( "net_debug_dormancy_max_labels", ConVarFlags.Protected | ConVarFlags.Cheat, Help = "Maximum number of network dormancy labels, nearest visible objects first." )]
	private static int DebugDormancyMaxLabels { get; set; } = 64;

	private enum DormancyState
	{
		RemoteUnknown,
		AwakeDirty,
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

	private static DormancyState GetDormancyState( NetworkObject obj )
	{
		// A proxy's local flags cannot tell us whether its remote sender is asleep.
		if ( obj.IsProxy ) return DormancyState.RemoteUnknown;
		if ( obj.IsDeltaDormant ) return DormancyState.VisibilitySleep;
		if ( !obj.IsDirty ) return DormancyState.AckClean;
		return obj.IsFullyUpdated ? DormancyState.AwakeDirty : DormancyState.PendingAck;
	}

	private void DrawDormancy()
	{
		if ( !DebugDormancy )
		{
			// Release all records once on disable; the normal disabled path does no work.
			if ( _dormancyTracking is not null ) _dormancyTracking = null;
			return;
		}

		var tracking = _dormancyTracking ??= new();
		var frame = ++tracking.Frame;
		var now = RealTime.NowDouble;
		using var scope = Gizmo.Scope();
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
