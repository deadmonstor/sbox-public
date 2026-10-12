using Sandbox.Network;

namespace Sandbox;

sealed partial class NetworkDebugSystem
{
	private SnapshotProfileCapture _snapshotCapture;
	internal static SnapshotProfileCapture SnapshotProfiler => Current?._snapshotCapture is { Active: true } capture ? capture : null;

	[ConCmd( "net_snapshot_profile_start", ConVarFlags.Protected )]
	internal static void StartSnapshotProfile()
	{
		if ( Current is not { } system )
		{
			Log.Warning( "NetworkDebugSystem is not active." );
			return;
		}

		system._snapshotCapture = new();
		Log.Info( "Snapshot profiling started; previous capture replaced. Process-local outbound protocol accounting only." );
	}

	[ConCmd( "net_snapshot_profile_stop", ConVarFlags.Protected )]
	internal static void StopSnapshotProfile()
	{
		Current?._snapshotCapture?.Stop();
		DumpSnapshotProfile();
	}

	[ConCmd( "net_snapshot_profile_dump", ConVarFlags.Protected )]
	internal static void DumpSnapshotProfile()
	{
		if ( Current?._snapshotCapture is not { } capture )
		{
			Log.Info( "No snapshot capture. Run net_snapshot_profile_start first." );
			return;
		}

		const string filename = "snapshot-profile.csv";
		FileSystem.Data.WriteAllText( filename, capture.Export() );
		Log.Info( $"Written to {filename}. Dropped detail samples: {capture.DroppedSamples}; totals remain complete." );
	}
}
