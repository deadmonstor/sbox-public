namespace Sandbox;

/// <summary>
/// A network parent that can be woken when one of its synced values changes.
/// </summary>
internal interface INetworkWakeable
{
	void MarkDirty();
}