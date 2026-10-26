using BotSwarm;

if ( args.Contains( "--help" ) || args.Contains( "-h" ) )
{
	Console.WriteLine( Options.Usage );
	return 0;
}

Options options;

try
{
	options = Options.Parse( args );
}
catch ( Exception e ) when ( e is ArgumentException or FormatException or OverflowException )
{
	Console.Error.WriteLine( e.Message );
	Console.Error.WriteLine();
	Console.Error.WriteLine( Options.Usage );
	return 1;
}

Protocol.Init();

// Bots are async, but ticking them in parallel and a burst of joins shouldn't wait on thread injection
ThreadPool.GetMinThreads( out var workers, out var io );
ThreadPool.SetMinThreads( Math.Max( workers, Environment.ProcessorCount * 4 ), io );

using var cancel = new CancellationTokenSource();

Console.CancelKeyPress += ( _, e ) =>
{
	e.Cancel = true;
	cancel.Cancel();
};

if ( options.Duration > 0 )
	cancel.CancelAfter( TimeSpan.FromSeconds( options.Duration ) );

Console.WriteLine( $"botswarm: {options.Bots} bots -> {options.Host}:{options.Port}, {options.JoinRate}/s joins, {options.TickRate}Hz ticks" +
	$"{(options.ChurnMax > 0 ? $", churn {options.ChurnMin}-{options.ChurnMax}s" : "")}. Ctrl+C to stop." );

var swarm = new Swarm( options );
await swarm.RunAsync( cancel.Token );

Console.WriteLine();
Console.WriteLine( swarm.Stats.Summary() );
return 0;
