namespace Sandbox;

/// <summary>
/// A delta property that reports every change through its parent instead of requiring polling.
/// Implementations must also report changes applied by deserialization.
/// </summary>
internal interface INetworkSnapshotChanges : INetworkProperty
{
}
