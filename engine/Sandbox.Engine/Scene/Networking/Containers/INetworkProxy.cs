namespace Sandbox;

/// <summary>
/// An interface to implement to determine whether this object is a proxy.
/// </summary>
internal interface INetworkProxy
{
	public bool IsProxy { get; }

	/// <summary>
	/// Notify the parent that a property changed internally, without replacing its value.
	/// </summary>
	void MarkChanged( INetworkProperty property ) { }
}
