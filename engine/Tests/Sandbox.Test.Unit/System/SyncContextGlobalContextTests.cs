using Sandbox.Engine;
using Sandbox.Internal;
using Sandbox.Tasks;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SystemTests;

[TestClass]
[DoNotParallelize]
public class SyncContextGlobalContextTests
{
	[TestMethod]
	[DataRow( false, false )]
	[DataRow( false, true )]
	[DataRow( true, false )]
	[DataRow( true, true )]
	public void PostRunsInPostersContext( bool fromMenu, bool suppressFlow )
	{
		var game = new GlobalContext { TypeLibrary = new TypeLibrary() };
		var menu = new GlobalContext { TypeLibrary = new TypeLibrary() };
		using var gameCancellation = game.CancellationTokenSource;
		using var menuCancellation = menu.CancellationTokenSource;

		var origin = fromMenu ? menu : game;
		var drainer = fromMenu ? game : menu;
		var sync = new ExpirableSynchronizationContext( false );
		GlobalContext observedContext = null;
		TypeLibrary observedLibrary = null;

		OnWorker( origin, suppressFlow, () =>
		{
			sync.Post( _ => throw new InvalidOperationException( "Expected sync context regression exception" ), null );
			sync.Post( _ =>
			{
				observedContext = GlobalContext.Current;
				observedLibrary = GlobalContext.Current.TypeLibrary;
			}, null );
		} );

		using var scope = new GlobalContext.GlobalContextScope( drainer );
		sync.ProcessQueue();

		Assert.AreSame( origin, observedContext );
		Assert.AreSame( origin.TypeLibrary, observedLibrary );
		Assert.AreSame( drainer, GlobalContext.Current );
	}

	[TestMethod]
	public void WatchdogDoesNotKeepCompletedClosureAlive()
	{
		var sync = new ExpirableSynchronizationContext( true );
		try
		{
			var closure = RunPostedClosure( sync );

			GC.Collect();

			Assert.IsFalse( closure.IsAlive, "The watchdog must not keep completed jobs' closures alive." );
		}
		finally
		{
			sync.Expire( new ExpirableSynchronizationContext( false ) );
		}
	}

	[MethodImpl( MethodImplOptions.NoInlining )]
	private static WeakReference RunPostedClosure( ExpirableSynchronizationContext sync )
	{
		var ran = false;
		Action closure = () => ran = true;
		var reference = new WeakReference( closure.Target );
		sync.Post( static state => ((Action)state)(), closure );
		sync.ProcessQueue();
		Assert.IsTrue( ran );
		return reference;
	}

	private static void OnWorker( GlobalContext context, bool suppressFlow, Action action )
	{
		Exception failure = null;
		var worker = new Thread( () =>
		{
			try
			{
				using var scope = new GlobalContext.GlobalContextScope( context );

				if ( suppressFlow )
				{
					using var flow = ExecutionContext.SuppressFlow();
					action();
				}
				else
				{
					action();
				}
			}
			catch ( Exception e )
			{
				failure = e;
			}
		} )
		{ IsBackground = true };

		worker.Start();
		Assert.IsTrue( worker.Join( TimeSpan.FromSeconds( 10 ) ), "The poster must complete." );
		Assert.IsNull( failure, failure?.ToString() );
	}
}
