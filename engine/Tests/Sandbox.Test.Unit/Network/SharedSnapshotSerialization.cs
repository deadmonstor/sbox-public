using System.Collections.Generic;
using Sandbox.Network;

namespace NetworkTests;

[TestClass]
public class SharedSnapshotSerializationTest
{
	[TestMethod]
	public void ReuseRequiresIdenticalSnapshotsSlotsAndImmutableValues()
	{
		var snapshot = new DeltaSnapshot();
		var value = new byte[] { 1, 2, 3 };
		var left = new Dictionary<DeltaSnapshot, SnapshotData> { [snapshot] = new() { [1] = value } };
		var right = new Dictionary<DeltaSnapshot, SnapshotData> { [snapshot] = new() { [1] = value } };
		Assert.IsTrue( DeltaSnapshotSystem.HasSameClusterData( left, right ) );
		right[snapshot][1] = new byte[] { 1, 2, 3 };
		Assert.IsFalse( DeltaSnapshotSystem.HasSameClusterData( left, right ), "Separate value storage must use its own serialization" );
		right[snapshot].Clear();
		right[snapshot][2] = value;
		Assert.IsFalse( DeltaSnapshotSystem.HasSameClusterData( left, right ) );
		right.Clear();
		right[new DeltaSnapshot()] = left[snapshot];
		Assert.IsFalse( DeltaSnapshotSystem.HasSameClusterData( left, right ) );
		right.Clear();
		Assert.IsFalse( DeltaSnapshotSystem.HasSameClusterData( left, right ) );
	}
}
