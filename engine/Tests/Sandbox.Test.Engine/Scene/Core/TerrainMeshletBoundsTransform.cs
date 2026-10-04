namespace SceneTests.Core;

[TestClass]
public class TerrainMeshletBoundsTransformTest
{
	private static Transform[] Transforms =>
	[
		Transform.Zero,
		new( new Vector3( 100, -200, 300 ), Rotation.From( 45, 30, 60 ), new Vector3( 2, 3, 4 ) ),
		new( new Vector3( -500, 250, 10 ), Rotation.From( -70, 140, 15 ), new Vector3( -2, 0.5f, -4 ) ),
		new( new Vector3( 1000000, -2000000, 3000000 ), Rotation.From( 5, 75, 130 ), new Vector3( 0.01f, -10, 3 ) ),
		new( new Vector3( 4, 8, 16 ), Rotation.From( 90, 180, -90 ), new Vector3( 0, -1, 0.00001f ) )
	];

	private static BBox[] Boxes =>
	[
		new( new Vector3( -2, -2, -2 ), new Vector3( 6, 6, 1026 ) ),
		new( new Vector3( -100, 20, -64 ), new Vector3( -10, 64, 1088 ) ),
		new( new Vector3( 1048576, -2097152, -256 ), new Vector3( 1049600, -2096128, 1280 ) ),
		new( new Vector3( -3, 7, 11 ), new Vector3( -3, 7, 11 ) ),
		new( new Vector3( 50, 40, 30 ), new Vector3( -10, -20, -30 ) )
	];

	[TestMethod]
	public void ReusedCoefficientsMatchBBoxTransformExactly()
	{
		foreach ( var transform in Transforms )
		{
			var coefficients = new TerrainClipmapSceneObject.MeshletBoundsTransform( transform );
			foreach ( var box in Boxes )
			{
				var expected = box.Transform( transform );
				var actual = coefficients.Apply( box );
				Assert.AreEqual( expected.Mins, actual.Mins );
				Assert.AreEqual( expected.Maxs, actual.Maxs );
			}
		}
	}

	[TestMethod]
	public void TransformedBoundsContainEveryCorner()
	{
		foreach ( var transform in Transforms )
		{
			var coefficients = new TerrainClipmapSceneObject.MeshletBoundsTransform( transform );
			foreach ( var box in Boxes )
			{
				var actual = coefficients.Apply( box );
				foreach ( var corner in box.Corners )
				{
					var world = transform.PointToWorld( corner );
					// Rotation and center/extents use different floating-point operation orders.
					float epsilon = System.MathF.Max( 0.0001f, System.MathF.Max( System.MathF.Abs( world.x ), System.MathF.Max( System.MathF.Abs( world.y ), System.MathF.Abs( world.z ) ) ) * 0.000002f );
					Assert.IsTrue( actual.Contains( world, epsilon ) );
				}
			}
		}
	}

	[TestMethod]
	public void FrustumDecisionsMatchAtPlaneBoundaries()
	{
		foreach ( var transform in Transforms )
		{
			var coefficients = new TerrainClipmapSceneObject.MeshletBoundsTransform( transform );
			foreach ( var box in Boxes )
			{
				var expected = box.Transform( transform );
				var actual = coefficients.Apply( box );
				foreach ( float offset in new[] { -0.01f, 0.0f, 0.01f } )
				{
					var frustum = new Frustum(
						new Plane( new Vector3( -1, 0, 0 ), -expected.Maxs.x + offset ),
						new Plane( new Vector3( 1, 0, 0 ), expected.Mins.x + offset ),
						new Plane( new Vector3( 0, -1, 0 ), -expected.Maxs.y + offset ),
						new Plane( new Vector3( 0, 1, 0 ), expected.Mins.y + offset ),
						new Plane( new Vector3( 0, 0, 1 ), expected.Mins.z + offset ),
						new Plane( new Vector3( 0, 0, -1 ), -expected.Maxs.z + offset ) );
					Assert.AreEqual( frustum.IsInside( expected, partially: true ), frustum.IsInside( actual, partially: true ) );
					Assert.AreEqual( frustum.IsInside( expected ), frustum.IsInside( actual ) );
				}
			}
		}
	}
}
