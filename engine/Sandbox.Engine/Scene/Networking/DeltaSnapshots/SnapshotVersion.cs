namespace Sandbox.Network;

/// <summary>
/// Ownership proposals and refreshes advance independently. A refresh must not win an ownership election,
/// and snapshots or refreshes from an earlier owner must not be mistaken for the new owner's state.
/// </summary>
[Expose]
internal readonly record struct SnapshotVersion( ushort Ownership, ushort Refresh )
{
	public SnapshotVersion NextOwnership() => new( (ushort)(Ownership + 1), 0 );
	public SnapshotVersion NextRefresh() => new( Ownership, (ushort)(Refresh + 1) );

	public override string ToString() => $"{Ownership}.{Refresh}";
}
