namespace Sandbox;

/// <summary>
/// Stores poses and query shapes for one topology generation.
/// </summary>
internal sealed class LagCompensationHistory : IDisposable
{
	internal const int MaximumShapes = 128;

	internal const int MaximumHullVertices = 1024;

	internal readonly LagCompensation Participant;
	internal readonly List<HistoricalShape> Shapes = new();
	readonly List<SourceShape> sources = new( MaximumShapes );
	double[] samples = Array.Empty<double>();
	Transform[] poses = Array.Empty<Transform>();
	BBox[] bounds = Array.Empty<BBox>();
	BBox missingBounds;
	bool hasMissingBounds;

	internal uint Generation { get; private set; }

	internal IEnumerable<SourceShape> Sources => sources;

	int next, count, capacity;
	Guid owner;
	NetworkObject lifetime;
	bool initialized;
	int visited;
	int availableShapes;

	internal LagCompensationStatus Status { get; private set; } = LagCompensationStatus.MissingHistory;

	internal LagCompensationHistory( LagCompensation participant ) => Participant = participant;

	internal void Refresh( Func<PhysicsWorld> getWorld, int recordCapacity, int shapeBudget )
	{
		sources.Clear();
		availableShapes = Math.Min( MaximumShapes, shapeBudget );
		visited = 0;
		Status = LagCompensationStatus.Success;
		Collect( Participant.GameObject, 0 );

		if ( Status != LagCompensationStatus.Success )
		{
			Clear();
			return;
		}

		var changed = !initialized || owner != Participant.GameObject.Network.OwnerId
			|| lifetime != Participant.GameObject.Network.RootGameObject._net
			|| capacity != recordCapacity || Shapes.Count != sources.Count;

		if ( !changed )
		{
			for ( int i = 0; i < sources.Count; i++ )
			{
				if ( !Shapes[i].Matches( sources[i] ) )
				{
					changed = true;
					break;
				}

				// Detect discontinuous bone/body motion as well as movement of the enrollment root.
				if ( count > 0 && Participant.TeleportDistance > 0
					&& Vector3.DistanceBetween( poses[((next + capacity - 1) % capacity) * Shapes.Count + i].Position, sources[i].Shape.Body.Position ) > Participant.TeleportDistance )
				{
					changed = true;
					break;
				}
			}
		}

		if ( changed )
		{
			Clear();
			capacity = recordCapacity;

			foreach ( var source in sources )
			{
#pragma warning disable CA2000 // Dispose objects before losing scope
				// Shapes owns the historical shapes; Clear() disposes them.
				var shape = HistoricalShape.Create( getWorld(), source );
#pragma warning restore CA2000 // Dispose objects before losing scope

				if ( shape is null )
				{
					Status = LagCompensationStatus.UnsupportedGeometry;
					Clear();
					return;
				}

				Shapes.Add( shape );
			}

			samples = new double[capacity];
			bounds = new BBox[capacity];
			poses = new Transform[capacity * Shapes.Count];
			owner = Participant.GameObject.Network.OwnerId;
			lifetime = Participant.GameObject.Network.RootGameObject._net;
			initialized = true;
		}
	}

	void Collect( GameObject gameObject, int depth )
	{
		if ( !gameObject.IsValid() || !gameObject.Enabled )
			return;

		if ( ++visited > 1024 || depth > 128 )
		{
			Status = LagCompensationStatus.CapacityExceeded;
			return;
		}

		var components = gameObject.Components.GetAll<Component>( FindMode.EnabledInSelf );

		// Nested enrollments own their subtree, preventing duplicate historical geometry.
		foreach ( var component in components )
		{
			if ( component is LagCompensation other && other != Participant && other.Active )
				return;
		}

		foreach ( var component in components )
		{
			if ( !component.Active )
				continue;

			if ( Participant.IncludeColliders && component is Collider collider )
			{
				foreach ( var shape in collider.Shapes )
				{
					Add( shape, null );
				}
			}

			if ( !Participant.IncludeHitboxes )
				continue;

			if ( component is ModelHitboxes model )
			{
				model.UpdatePositions();

				foreach ( var hitbox in model.Hitboxes )
				{
					AddHitboxShapes( hitbox );
				}
			}

			if ( component is ManualHitbox manual )
			{
				manual.UpdatePositions();
				AddHitboxShapes( manual.Hitbox );
			}
		}

		foreach ( var child in gameObject.Children )
		{
			Collect( child, depth + 1 );
		}
	}

	void AddHitboxShapes( Hitbox hitbox )
	{
		if ( hitbox?.Body?._body is not PhysicsBody3d body || !body.IsValid )
			return;

		// The public Shapes enumerable is a yield iterator; use indexed access during capture.
		for ( int i = 0; i < body.ShapeCount; i++ )
		{
			Add( body.native.GetShape( i ).Owner, hitbox );
		}
	}

	void Add( PhysicsShape shape, Hitbox hitbox )
	{
		if ( !shape.IsValid() || !shape.Body.IsValid() )
			return;

		AddMissingBounds( shape.BuildBounds() );

		if ( sources.Count >= availableShapes )
			Status = LagCompensationStatus.CapacityExceeded;

		if ( sources.Count < MaximumShapes )
			sources.Add( new( shape, hitbox ) );

		if ( !shape.IsSphereShape && !shape.IsCapsuleShape && !shape.IsHullShape )
			Status = LagCompensationStatus.UnsupportedGeometry;
	}

	internal void Record( double time, double maximumGap )
	{
		if ( Status != LagCompensationStatus.Success || !initialized )
			return;

		if ( count > 0 )
		{
			var previous = samples[(next + capacity - 1) % capacity];

			if ( time <= previous )
				return;

			if ( time - previous > maximumGap )
				count = next = 0;
		}

		samples[next] = time;
		var sampleBounds = new BBox( Participant.WorldPosition, Participant.WorldPosition );

		for ( int i = 0; i < Shapes.Count; i++ )
		{
			var shape = Shapes[i];
			var pose = shape.Source.Shape.Body.Transform;
			poses[next * Shapes.Count + i] = pose;

			// A sphere around the body origin covers intermediate rotations as well as both endpoints.
			var local = shape.Source.Shape.LocalBounds;
			var scale = pose.Scale.Abs();
			var radius = Vector3.Max( local.Mins.Abs(), local.Maxs.Abs() ).Length * MathF.Max( scale.x, MathF.Max( scale.y, scale.z ) );
			var shapeBounds = new BBox( pose.Position - radius, pose.Position + radius );
			sampleBounds = i == 0 ? shapeBounds : Union( sampleBounds, shapeBounds );
		}

		bounds[next] = sampleBounds;
		next = (next + 1) % capacity;
		count = Math.Min( count + 1, capacity );
	}

	bool FindBracket( double time, out int before, out int after )
	{
		before = after = 0;

		if ( count == 0 )
			return false;

		var oldest = (next + capacity - count) % capacity;
		var newest = (next + capacity - 1) % capacity;

		if ( time < samples[oldest] || time > samples[newest] )
			return false;

		before = after = oldest;

		for ( int i = 1; i < count && samples[after] < time; i++ )
		{
			before = after;
			after = (oldest + i) % capacity;
		}

		return true;
	}

	internal bool CouldIntersect( double time, in BBox sweptBounds )
	{
		if ( FindBracket( time, out var before, out var after ) )
			return Union( bounds[before], bounds[after] ).Overlaps( sweptBounds );

		return hasMissingBounds && missingBounds.Overlaps( sweptBounds );
	}

	void AddMissingBounds( BBox value )
	{
		missingBounds = hasMissingBounds ? Union( missingBounds, value ) : value;
		hasMissingBounds = true;
	}

	static BBox Union( BBox a, BBox b ) => new( Vector3.Min( a.Mins, b.Mins ), Vector3.Max( a.Maxs, b.Maxs ) );

	internal bool Accepts( in SceneTrace trace )
	{
		if ( Shapes.Count > 0 )
		{
			foreach ( var shape in Shapes )
			{
				if ( shape.Accepts( trace ) )
					return true;
			}

			return false;
		}

		foreach ( var source in sources )
		{
			var live = source.Shape;
			if ( live.IsValid() && HistoricalShape.AcceptsSource( trace, source, live.Body.Enabled,
				live.TracesIgnored, live.IsTrigger, live.Body.BodyType, live.Tags ) )
			{
				return true;
			}
		}

		// The collection limit prevents proving that every remaining shape was filtered out.
		return sources.Count == MaximumShapes;
	}

	internal void DisableQueryShapes()
	{
		foreach ( var shape in Shapes )
		{
			shape.QueryBody.Enabled = false;
		}
	}

	internal bool PositionQueryShapes( double time, bool physics, bool hitboxes )
	{
		if ( !FindBracket( time, out var before, out var after ) )
			return false;

		var delta = samples[after] - samples[before];
		var fraction = delta > 0 ? (float)((time - samples[before]) / delta) : 0;

		for ( int i = 0; i < Shapes.Count; i++ )
		{
			var shape = Shapes[i];
			shape.QueryBody.Enabled = shape.Enabled && (shape.Source.Hitbox is null ? physics : hitboxes);
			shape.QueryBody.Transform = Transform.Lerp( poses[before * Shapes.Count + i], poses[after * Shapes.Count + i], fraction, true );
		}

		return true;
	}

	internal void Clear()
	{
		foreach ( var shape in Shapes )
		{
			shape.Dispose();
		}

		Generation++;
		Shapes.Clear();
		count = next = 0;
		initialized = false;
		samples = Array.Empty<double>();
		bounds = Array.Empty<BBox>();
		poses = Array.Empty<Transform>();
	}

	public void Dispose() => Clear();

	internal readonly record struct SourceShape( PhysicsShape Shape, Hitbox Hitbox );

	internal sealed class HistoricalShape : IDisposable
	{
		internal readonly SourceShape Source;
		internal readonly PhysicsBody QueryBody;
		internal readonly PhysicsShape QueryShape;
		internal readonly bool Enabled;
		readonly uint revision;
		readonly bool trigger, solid, ignored;
		readonly PhysicsBodyType bodyType;
		readonly Component component;
		readonly GameObject gameObject;
		readonly Collider collider;
		readonly Surface surface;
		readonly TagSet tags = new();
		readonly TagSet hitgroupTags = new();
		internal readonly Hitbox HistoricalHitbox;

		HistoricalShape( SourceShape source, PhysicsBody body, PhysicsShape shape )
		{
			Source = source;
			QueryBody = body;
			QueryShape = shape;
			var live = source.Shape;

			revision = live.GeometryRevision;
			Enabled = live.Body.Enabled;
			trigger = live.IsTrigger;
			solid = live.EnableSolidCollisions;
			ignored = live.TracesIgnored;
			bodyType = live.Body.BodyType;
			component = live.Body.Component;
			gameObject = source.Hitbox?.GameObject ?? live.Body.GameObject;
			collider = live.Collider;
			surface = live.Surface;
			tags.SetFrom( live.Tags );

			body.BodyType = bodyType;
			// Query copies do not receive gameplay contact or dirty callbacks.
			shape.IsTrigger = trigger;
			shape.EnableSolidCollisions = solid;
			shape.EnableTouch = false;
			shape.IgnoreTraces = ignored;
			shape.Surface = surface;
			shape.BoneIndex = source.Hitbox?.Bone?.Index ?? live.BoneIndex;
			shape.Tags.SetFrom( tags );

			if ( source.Hitbox is not null )
			{
				hitgroupTags.SetFrom( source.Hitbox.Tags );

				// Snapshot hitgroup/bone identity, not the disposable live hitbox body.
				HistoricalHitbox = new Hitbox( gameObject, source.Hitbox.Bone, hitgroupTags );
			}

			body.Enabled = false;
		}

		internal static HistoricalShape Create( PhysicsWorld world, SourceShape source )
		{
			var live = source.Shape;
			Vector3[] vertices = null;

			if ( live.IsHullShape )
			{
				live.Triangulate( out vertices, out _ );

				if ( vertices is null || vertices.Length < 4 || vertices.Length > MaximumHullVertices )
					return null;
			}

			var body = world.CreateBody();
			var shape = live.IsSphereShape ? body.AddSphereShape( live.Sphere, false )
				: live.IsCapsuleShape ? body.AddCapsuleShape( live.Capsule.CenterA, live.Capsule.CenterB, live.Capsule.Radius, false )
				: body.AddHullShape( Vector3.Zero, Rotation.Identity, vertices.AsSpan(), false );

			if ( !shape.IsValid() || shape.ShapeType != live.ShapeType )
			{
				body.Remove();
				return null;
			}

			return new HistoricalShape( source, body, shape );
		}

		internal bool Matches( SourceShape source )
		{
			var live = source.Shape;
			return Source == source && live.IsValid() && revision == live.GeometryRevision && Enabled == live.Body.Enabled
				&& trigger == live.IsTrigger && solid == live.EnableSolidCollisions && ignored == live.TracesIgnored
				&& bodyType == live.Body.BodyType && collider == live.Collider && component == live.Body.Component
				&& gameObject == (source.Hitbox?.GameObject ?? live.Body.GameObject) && surface == live.Surface
				&& EqualTags( tags, live.Tags ) && (source.Hitbox is null || EqualTags( hitgroupTags, source.Hitbox.Tags ));
		}

		static bool EqualTags( ITagSet left, ITagSet right )
		{
			// Engine shape and hitgroup tags expose their backing sets; avoid boxed enumerators per frame.
			if ( left.TryGetAll() is HashSet<string> a && right.TryGetAll() is HashSet<string> b )
				return a.SetEquals( b );

			return left.HasAll( right ) && right.HasAll( left );
		}

		internal bool Accepts( in SceneTrace trace )
			=> AcceptsSource( trace, Source, Enabled, ignored, trigger, bodyType, tags );

		internal static unsafe bool AcceptsSource( in SceneTrace trace, SourceShape source, bool enabled,
			bool ignored, bool trigger, PhysicsBodyType bodyType, ITagSet tags )
		{
			if ( !enabled || ignored )
				return false;

			if ( source.Hitbox is null ? !trace.IncludePhysicsWorld : !trace.IncludeHitboxes )
				return false;

			var request = trace.PhysicsTrace.request;
			if ( trigger ? request.TriggerFilter == 0 : request.TriggerFilter == 2 )
				return false;

			var bodyMask = bodyType switch
			{
				PhysicsBodyType.Static => 1 << 0,
				PhysicsBodyType.Dynamic => 1 << 1,
				PhysicsBodyType.Keyframed => 1 << 2,
				_ => 0
			};

			if ( (request.ObjectSetMask & bodyMask) != 0 )
				return false;

			var tokens = tags.GetTokens();
			bool anyRequired = false, anyMatched = false;

			for ( int i = 0; i < PhysicsTrace.Request.NumTagFields; i++ )
			{
				if ( request.TagRequire[i] != 0 && !tokens.Contains( request.TagRequire[i] ) )
					return false;

				if ( request.TagExclude[i] != 0 && tokens.Contains( request.TagExclude[i] ) )
					return false;

				if ( request.TagAny[i] == 0 )
					continue;

				anyRequired = true;
				anyMatched |= tokens.Contains( request.TagAny[i] );
			}

			if ( anyRequired && !anyMatched )
				return false;

			return !Excluded( trace, source.Hitbox?.GameObject ?? source.Shape.Body.GameObject )
				&& !Excluded( trace, source.Shape.Collider?.GameObject )
				&& !Excluded( trace, source.Shape.Body.GameObject );
		}

		internal bool AcceptsExclusions( in SceneTrace trace )
		{
			return Enabled && !ignored && !Excluded( trace, gameObject )
				&& !Excluded( trace, collider?.GameObject ) && !Excluded( trace, Source.Shape.Body.GameObject );
		}

		static bool Excluded( in SceneTrace trace, GameObject gameObject )
		{
			if ( gameObject is null )
				return false;

			if ( trace.IgnoreSingleObject.Contains( gameObject ) )
				return true;

			foreach ( var root in trace.IgnoreHierarchy )
			{
				if ( gameObject.IsAncestor( root ) )
					return true;
			}

			return false;
		}

		internal SceneTraceResult Resolve( Scene scene, in PhysicsTraceResult result )
		{
			var resolved = SceneTraceResult.From( scene, result );
			resolved.GameObject = gameObject;
			resolved.Component = component;
			resolved.Collider = collider;
			resolved.Body = Source.Hitbox is null ? Source.Shape.Body : null;
			resolved.Shape = Source.Hitbox is null ? Source.Shape : null;
			resolved.Hitbox = HistoricalHitbox;

			return resolved;
		}

		public void Dispose()
		{
			QueryBody.Remove();
		}
	}
}
