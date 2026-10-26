using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BotSwarm;

/// <summary>
/// Keeps <see cref="Options.Bots"/> bots connected: joins them at the configured rate, ticks the active
/// ones from one thread, replaces any that leave and prints a status line every second.
/// </summary>
internal sealed partial class Swarm( Options options )
{
	readonly Stats stats = new();
	readonly Bot[] bots = new Bot[options.Bots];
	readonly SemaphoreSlim joinPermits = new( 0 );

	public Stats Stats => stats;

	public async Task RunAsync( CancellationToken cancel )
	{
		var permits = Task.Run( () => IssueJoinPermits( cancel ), cancel );
		var slots = Enumerable.Range( 0, bots.Length ).Select( i => Task.Run( () => SlotLoop( i, cancel ), cancel ) ).ToArray();

		var ticker = new Thread( () => TickLoop( cancel ) ) { IsBackground = true, Name = "Bot Tick", Priority = ThreadPriority.AboveNormal };
		ticker.Start();

		using var report = new PeriodicTimer( TimeSpan.FromSeconds( 1 ) );

		try
		{
			while ( await report.WaitForNextTickAsync( cancel ) )
			{
				Console.WriteLine( stats.Line( bots.Length, bots.Count( x => x?.IsMoving == true ) ) );
			}
		}
		catch ( OperationCanceledException )
		{
			// Stopping
		}

		await Task.WhenAll( slots.Append( permits ).Select( t => t.ContinueWith( _ => { } ) ) );
		ticker.Join();
	}

	/// <summary>
	/// Hand out join permits at <see cref="Options.JoinRate"/> per second, without banking a burst while
	/// every slot is busy.
	/// </summary>
	async Task IssueJoinPermits( CancellationToken cancel )
	{
		var clock = Stopwatch.StartNew();
		long issued = 0;
		var maxBanked = Math.Max( 1, (int)Math.Ceiling( options.JoinRate * 0.1f ) );

		while ( !cancel.IsCancellationRequested )
		{
			var owed = (long)(clock.Elapsed.TotalSeconds * options.JoinRate) - issued;

			for ( ; owed > 0; owed-- )
			{
				issued++;
				if ( joinPermits.CurrentCount < maxBanked ) joinPermits.Release();
			}

			await Task.Delay( 5, cancel );
		}
	}

	async Task SlotLoop( int slot, CancellationToken cancel )
	{
		var failuresInARow = 0;

		while ( !cancel.IsCancellationRequested )
		{
			await joinPermits.WaitAsync( cancel );

			var bot = new Bot( slot, options, stats );
			bots[slot] = bot;

			try
			{
				await bot.RunAsync( cancel );
			}
			catch ( Exception e )
			{
				bot.Close( $"{e.GetType().Name}: {e.Message}" );
			}

			if ( cancel.IsCancellationRequested )
				return;

			var reason = bot.CloseReason ?? "unknown";

			if ( reason.StartsWith( "Kicked", StringComparison.Ordinal ) )
			{
				// Counted by the bot
				failuresInARow++;
			}
			else if ( bot.Joined && reason == "Churn" )
			{
				Interlocked.Increment( ref stats.Churned );
				failuresInARow = 0;
			}
			else if ( bot.Joined )
			{
				Interlocked.Increment( ref stats.Disconnects );
				stats.Reason( "drop", reason );
				failuresInARow = 0;
			}
			else
			{
				Interlocked.Increment( ref stats.Failures );
				stats.Reason( "fail", reason );
				failuresInARow++;
			}

			// Don't hammer a server that keeps turning us away
			if ( failuresInARow > 0 )
				await Task.Delay( TimeSpan.FromSeconds( Math.Min( 10, 0.5 * Math.Pow( 2, failuresInARow - 1 ) ) ), cancel );
		}
	}

	void TickLoop( CancellationToken cancel )
	{
		if ( OperatingSystem.IsWindows() ) TimeBeginPeriod( 1 );

		try
		{
			var clock = Stopwatch.StartNew();
			var interval = 1.0 / options.TickRate;
			var next = 0.0;
			var last = 0.0;
			var parallel = new ParallelOptions { CancellationToken = cancel };

			while ( !cancel.IsCancellationRequested )
			{
				var now = clock.Elapsed.TotalSeconds;

				if ( now < next )
				{
					Thread.Sleep( Math.Max( 1, (int)((next - now) * 1000) ) );
					continue;
				}

				var delta = (float)Math.Min( now - last, 0.25 );
				last = now;

				// Fall behind rather than spiral if a tick takes too long
				next = Math.Max( next + interval, now - interval );

				try
				{
					Parallel.ForEach( bots, parallel, bot => bot?.Tick( now, delta ) );
				}
				catch ( OperationCanceledException )
				{
					return;
				}
			}
		}
		finally
		{
			if ( OperatingSystem.IsWindows() ) TimeEndPeriod( 1 );
		}
	}

	[LibraryImport( "winmm.dll", EntryPoint = "timeBeginPeriod" )]
	private static partial uint TimeBeginPeriod( uint period );

	[LibraryImport( "winmm.dll", EntryPoint = "timeEndPeriod" )]
	private static partial uint TimeEndPeriod( uint period );
}
