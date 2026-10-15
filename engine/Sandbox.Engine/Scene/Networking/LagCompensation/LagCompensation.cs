namespace Sandbox;

/// <summary>
/// Enrolls this object's collider and hitbox descendants in host-only historical queries.
/// Nested enrollments own their subtrees. Sphere, capsule and convex hull geometry is supported;
/// meshes, terrain and 2D geometry are not. Enroll moving blockers too: unenrolled dynamic
/// objects obstruct at their current position. Ownership, geometry, enabled-state changes,
/// teleports and recording gaps invalidate history. New generations require history warmup.
/// Call <see cref="ResetHistory"/> when respawning or making an explicit discontinuous move.
/// </summary>
[Expose, Title( "Lag Compensation" ), Category( "Networking" ), Icon( "history" )]
public sealed class LagCompensation : Component
{
	/// <summary>
	/// Include colliders in historical queries.
	/// </summary>
	[Property] public bool IncludeColliders { get; set; } = true;

	/// <summary>
	/// Include hitboxes in historical queries.
	/// </summary>
	[Property] public bool IncludeHitboxes { get; set; } = true;

	/// <summary>
	/// Movement beyond this distance between records invalidates the entire history. Zero disables automatic teleport detection.
	/// </summary>
	[Property, Range( 0, 4096 )] public float TeleportDistance { get; set; } = 128;

	LagCompensationSystem system;

	protected override void OnEnabled()
	{
		system = Scene.GetSystem<LagCompensationSystem>();
		system.Register( this );
	}

	protected override void OnDisabled() => system?.Unregister( this );

	/// <summary>
	/// Invalidates previous poses immediately. Required for respawn and discontinuous moves below the teleport threshold.
	/// </summary>
	public void ResetHistory()
	{
		system?.Reset( this );
	}
}

/// <summary>
/// Historical queries never replace unavailable history with current poses.
/// </summary>
[Expose]
public enum LagCompensationStatus
{
	Success,
	NotHost,
	InvalidCommand,
	Future,
	Expired,
	MissingHistory,
	UnsupportedGeometry,
	CapacityExceeded,
	InvalidTrace
}

/// <summary>
/// A compensated hit or an explicit rejection. Trace is meaningful only when Success is true.
/// </summary>
[Expose]
public readonly struct LagCompensationResult
{
	/// <summary>
	/// The query outcome.
	/// </summary>
	public LagCompensationStatus Status { get; }

	/// <summary>
	/// Whether the query completed successfully.
	/// </summary>
	public bool Success => Status == LagCompensationStatus.Success;

	/// <summary>
	/// The trace result, available only for successful queries.
	/// </summary>
	public SceneTraceResult Trace { get; }

	/// <summary>
	/// The displayed-world time used by the query.
	/// </summary>
	public double Time { get; }

	internal LagCompensationResult( LagCompensationStatus status, double time, SceneTraceResult trace = default )
	{
		Status = status;
		Time = time;
		Trace = trace;
	}
}
