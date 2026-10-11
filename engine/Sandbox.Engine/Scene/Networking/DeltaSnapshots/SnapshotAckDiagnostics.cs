using System.Text;

namespace Sandbox.Network;

internal static class SnapshotAckDiagnostics
{
	internal static bool Enabled;
	internal static long ClusterCalls, LookupProbes, SnapshotCallbacks, RemoteSlotUpdates;
	internal static long LocalSlotsVisited, ReceiptAdds, ReceiptRemoves, CompletionSlots, ExpiredResends;

	[ConCmd( "net_snapshot_ack_start", ConVarFlags.Protected )]
	internal static void Start()
	{
		ClusterCalls = LookupProbes = SnapshotCallbacks = RemoteSlotUpdates = 0;
		LocalSlotsVisited = ReceiptAdds = ReceiptRemoves = CompletionSlots = ExpiredResends = 0;
		Enabled = true;
		Log.Info( "Snapshot ACK counting started. Counts are process-local; diagnostic runs only." );
	}

	[ConCmd( "net_snapshot_ack_stop", ConVarFlags.Protected )]
	internal static void Stop()
	{
		Enabled = false;
		Report();
	}

	[ConCmd( "net_snapshot_ack_report", ConVarFlags.Protected )]
	internal static void Report()
	{
		var report = new StringBuilder();
		report.AppendLine( $"active={Enabled}" );
		report.AppendLine( $"Cluster ACK calls: {ClusterCalls}" );
		report.AppendLine( $"Cluster lookup probes: {LookupProbes}" );
		report.AppendLine( $"Snapshot ACK callbacks: {SnapshotCallbacks}" );
		report.AppendLine( $"Remote slot update calls: {RemoteSlotUpdates}" );
		report.AppendLine( $"Local receipt slots visited: {LocalSlotsVisited}" );
		report.AppendLine( $"Receipt add attempts: {ReceiptAdds}" );
		report.AppendLine( $"Receipt remove attempts: {ReceiptRemoves}" );
		report.AppendLine( $"Completion mask slots visited: {CompletionSlots}" );
		report.AppendLine( $"Expired prediction resend attempts: {ExpiredResends}" );
		FileSystem.Data.WriteAllText( "snapshot-acks.txt", report.ToString() );
		foreach ( var line in report.ToString().Split( '\n' ) ) Log.Info( line );
	}
}
