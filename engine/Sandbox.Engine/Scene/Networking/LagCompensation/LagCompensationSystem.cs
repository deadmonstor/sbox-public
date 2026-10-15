namespace Sandbox;

/// <summary>
/// Host-only historical ray, box and capsule queries using authority-issued
/// <see cref="PredictionContext.CommandTime"/> or <see cref="Prediction.TryGetCommandTime"/> tokens.
/// Interpolation delay is subtracted exactly once. Enroll targets and moving blockers with
/// <see cref="LagCompensation"/>; static and unenrolled geometry obstruct at current time.
/// A potentially intersected participant without continuous history rejects the query rather
/// than substituting live poses. Check <see cref="LagCompensationResult.Success"/> before using a hit.
/// Requests at or after the newest record, up to current host time, trace the live world.
/// Sphere sweeps, cylinders, render meshes and target-body queries are unsupported.
/// The default rewind window is 0.5 seconds, capped at one second. History is bounded to
/// 128 participants, 128 shapes per participant, 4096 scene shapes, 256 records per participant
/// and 1024 vertices per convex hull. Changing rewind duration or fixed frequency clears history.
/// Result statuses distinguish non-host or invalid commands, future/expired requests, unavailable
/// history, unsupported geometry, exceeded capacity and unsupported trace modes.
/// Historical queries never reposition live objects or step physics.
/// </summary>
[Expose]
public sealed class LagCompensationSystem : GameObjectSystem<LagCompensationSystem>
{
	internal const int MaximumParticipants = 128;
	internal const int MaximumSceneShapes = 4096;
	internal const int MaximumRecords = 256;

	readonly Dictionary<LagCompensation, LagCompensationHistory> participants = new();
	readonly HashSet<PhysicsShape> currentShapes = new();
	readonly Dictionary<PhysicsShape, LagCompensationHistory.HistoricalShape> historicalShapes = new();
	readonly List<LagCompensation> removed = new();
	readonly Func<PhysicsShape, bool> historicalFilter;
	readonly Func<PhysicsWorld> getQueryWorld;
	SceneTrace queryTrace;
	PhysicsWorld queryWorld;
	bool wasHost;
	bool refreshNeeded = true;
	bool lookupDirty = true;
	float maximumRewind = 0.5f;
	float frequency;
	double newestRecordTime = double.NegativeInfinity;
	LagCompensationStatus refreshStatus;

	/// <summary>
	/// Maximum historical age in seconds, capped at one second. Changing this invalidates all history.
	/// </summary>
	public float MaximumRewind
	{
		get => maximumRewind;
		set
		{
			if ( !float.IsFinite( value ) || value <= 0 || value > 1 )
				throw new ArgumentOutOfRangeException( nameof( value ) );

			lock ( this )
			{
				if ( maximumRewind == value )
					return;

				maximumRewind = value;
				ClearHistories();
			}
		}
	}

	/// <summary>
	/// Creates the scene's historical query system.
	/// </summary>
	public LagCompensationSystem( Scene scene ) : base( scene )
	{
		historicalFilter = FilterHistoricalShape;
		getQueryWorld = () => queryWorld ??= new PhysicsWorld();
		Listen( Stage.FinishFixedUpdate, 200, AfterPhysics, "HistoricalPhysics" );
	}

	void AfterPhysics()
	{
		lock ( this )
		{
			if ( participants.Count == 0 || !EnsureHost() )
				return;

			refreshNeeded = true;

			if ( Refresh() != LagCompensationStatus.Success )
			{
				ClearHistories();
				return;
			}

			var maximumGap = 2.0 / frequency + 0.000001;
			foreach ( var history in participants.Values )
			{
				history.Record( Time.NowDouble, maximumGap );
			}

			newestRecordTime = Time.NowDouble;
		}
	}

	internal void Register( LagCompensation participant )
	{
		lock ( this )
		{
			if ( participants.ContainsKey( participant ) )
				return;

			participants.Add( participant, new LagCompensationHistory( participant ) );
			refreshNeeded = lookupDirty = true;
		}
	}

	internal void Unregister( LagCompensation participant )
	{
		lock ( this )
		{
			if ( participants.Remove( participant, out var history ) )
			{
				history.Dispose();
				refreshNeeded = lookupDirty = true;
			}
		}
	}

	internal void Reset( LagCompensation participant )
	{
		lock ( this )
		{
			if ( participants.TryGetValue( participant, out var history ) )
			{
				history.Clear();
				refreshNeeded = lookupDirty = true;
			}
		}
	}

	void ClearHistories()
	{
		foreach ( var history in participants.Values )
		{
			history.Clear();
		}

		newestRecordTime = double.NegativeInfinity;
		refreshNeeded = lookupDirty = true;
	}

	bool EnsureHost()
	{
		var host = Networking.IsHost;

		if ( host != wasHost )
		{
			ClearHistories();
			wasHost = host;
		}

		return host;
	}

	LagCompensationStatus Refresh()
	{
		if ( !refreshNeeded )
			return refreshStatus;

		refreshNeeded = false;
		removed.Clear();

		foreach ( var pair in participants )
		{
			if ( !pair.Key.IsValid() || !pair.Key.Active )
				removed.Add( pair.Key );
		}

		foreach ( var participant in removed )
		{
			Unregister( participant );
		}

		removed.Clear();

		var currentFrequency = ProjectSettings.Physics.FixedUpdateFrequency;
		if ( frequency != currentFrequency )
		{
			ClearHistories();
			frequency = currentFrequency;
		}

		refreshNeeded = false;

		var recordCount = Math.Ceiling( maximumRewind * frequency ) + 2;
		refreshStatus = Scene.Is2D ? LagCompensationStatus.UnsupportedGeometry
			: !float.IsFinite( frequency ) || frequency <= 0 || recordCount > MaximumRecords
				? LagCompensationStatus.CapacityExceeded : LagCompensationStatus.Success;

		if ( refreshStatus != LagCompensationStatus.Success )
			return refreshStatus;

		var capacity = Math.Max( (int)recordCount, 3 );
		int shapeCount = 0;
		int participantCount = 0;

		foreach ( var history in participants.Values )
		{
			var generation = history.Generation;
			var budget = ++participantCount <= MaximumParticipants ? MaximumSceneShapes - shapeCount : 0;
			history.Refresh( getQueryWorld, capacity, budget );
			shapeCount += history.Shapes.Count;
			lookupDirty |= generation != history.Generation;
		}

		if ( lookupDirty )
		{
			currentShapes.Clear();
			historicalShapes.Clear();

			foreach ( var history in participants.Values )
			{
				foreach ( var source in history.Sources )
				{
					currentShapes.Add( source.Shape );
				}

				foreach ( var shape in history.Shapes )
				{
					historicalShapes.Add( shape.QueryShape, shape );
				}
			}

			lookupDirty = false;
		}

		return refreshStatus;
	}

	/// <summary>
	/// Resolves an accepted command against its displayed-world time. Supports standard scene
	/// tags, collision rules, trigger/body-type filters and object exclusions. Missing or invalid
	/// history is an explicit rejection, not a miss; gameplay authority remains with the caller.
	/// </summary>
	public LagCompensationResult Trace( PredictionCommandTime commandTime, in SceneTrace trace )
		=> TraceAt( commandTime, trace, Time.NowDouble );

	internal LagCompensationResult TraceAt( PredictionCommandTime commandTime, in SceneTrace trace, double now )
	{
		lock ( this )
		{
			var time = commandTime.DisplayTime;

			if ( !EnsureHost() )
				return new( LagCompensationStatus.NotHost, time );

			if ( !commandTime.IsValidFor( Scene ) || !double.IsFinite( time ) || !double.IsFinite( commandTime.SimulationTime )
				|| !float.IsFinite( commandTime.InterpolationDelay ) || commandTime.InterpolationDelay < 0
				|| commandTime.InterpolationDelay > Prediction.MaximumInterpolationDelay )
			{
				return new( LagCompensationStatus.InvalidCommand, time );
			}

			if ( commandTime.SimulationTime > now || time > now )
				return new( LagCompensationStatus.Future, time );

			if ( time < now - maximumRewind )
				return new( LagCompensationStatus.Expired, time );

			var request = trace.PhysicsTrace.request;
			if ( trace.scene != Scene || trace.IncludeRenderMeshes || trace.PhysicsTrace.targetBody is not null
				|| request.StartShape.Type == PhysicsTrace.Request.ShapeType.Cylinder
				// Native sphere/sphere sweeps report false start-solid hits.
				|| (request.StartShape.Type == PhysicsTrace.Request.ShapeType.Sphere && request.StartShape.Radius.LengthSquared > 0) )
			{
				return new( LagCompensationStatus.InvalidTrace, time );
			}

			if ( participants.Count == 0 || (time >= newestRecordTime
				&& (double.IsFinite( newestRecordTime ) || time == now)) )
			{
				return new( LagCompensationStatus.Success, time, trace.Run() );
			}

			var status = Refresh();
			if ( status != LagCompensationStatus.Success )
				return new( status, time );

			// A bounding sphere covers rotated sweep shapes conservatively along the entire path.
			var radius = Vector3.Max( request.StartShape.Mins.Abs(), request.StartShape.Maxs.Abs() ).Length
				+ MathF.Max( request.StartShape.Radius.x, request.StartShape.Radius.y ) + 0.001f;
			var sweptBounds = new BBox( Vector3.Min( request.StartPos, request.EndPos ) - radius,
				Vector3.Max( request.StartPos, request.EndPos ) + radius );

			foreach ( var history in participants.Values )
			{
				history.DisableQueryShapes();

				if ( !history.CouldIntersect( time, sweptBounds ) )
					continue;

				if ( !history.Accepts( trace ) )
					continue;

				if ( history.Status != LagCompensationStatus.Success )
					return new( history.Status, time );

				if ( !history.PositionQueryShapes( time, trace.IncludePhysicsWorld, trace.IncludeHitboxes ) )
					return new( LagCompensationStatus.MissingHistory, time );
			}

			var normal = trace;
			normal.IgnoreLagCompensatedShapes = currentShapes;
			var best = normal.Run();

			if ( queryWorld is null )
				return new( LagCompensationStatus.Success, time, best );

			queryTrace = trace;

			try
			{
				var historical = trace.PhysicsTrace;
				historical.filterCallback = historicalFilter;
				var hit = queryWorld.RunTrace( historical );

				if ( hit.Hit && (!best.Hit || hit.Fraction < best.Fraction) && historicalShapes.TryGetValue( hit.Shape, out var resolved ) )
					best = resolved.Resolve( Scene, hit );
			}
			finally
			{
				queryTrace = default;
			}

			return new( LagCompensationStatus.Success, time, best );
		}
	}

	bool FilterHistoricalShape( PhysicsShape shape )
		=> historicalShapes.TryGetValue( shape, out var history ) && history.AcceptsExclusions( queryTrace );

	/// <summary>
	/// Releases historical query shapes and their physics world.
	/// </summary>
	public override void Dispose()
	{
		lock ( this )
		{
			foreach ( var history in participants.Values )
			{
				history.Dispose();
			}

			participants.Clear();
			currentShapes.Clear();
			historicalShapes.Clear();
			queryWorld?.Delete();
			queryWorld = null;
		}

		base.Dispose();
	}
}
