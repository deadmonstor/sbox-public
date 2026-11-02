
namespace Sandbox;

public partial class Model
{
	/// <summary>
	/// Total bounds of all the meshes.
	/// </summary>
	public BBox Bounds => native.GetMeshBounds();

	BBox? _cachedBounds;

	/// <summary>
	/// <see cref="Bounds"/>, cached until the model reloads. Procedural models read it live, their mesh bounds can change.
	/// </summary>
	internal BBox CachedBounds => IsProcedural ? Bounds : (_cachedBounds ??= Bounds);

	/// <summary>
	/// Total bounds of all the physics shapes.
	/// </summary>
	public BBox PhysicsBounds => native.GetPhysicsBounds();

	/// <summary>
	/// Render view bounds.
	/// </summary>
	public BBox RenderBounds => native.GetModelRenderBounds();
}
