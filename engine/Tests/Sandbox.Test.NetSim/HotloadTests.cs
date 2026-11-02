namespace NetSim;

/// <summary>
/// A hotload on one peer must not restart the snapshot sequence seen by peers that did not hotload.
/// </summary>
[TestClass]
public class HotloadTests
{
	[TestInitialize]
	public void Init() => ThreadSafe.MarkMainThread();

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void HostHotloadKeepsOneOffChangesAndRejectsOlderSnapshots( bool mesh )
	{
		using var sim = new NetSimulation( new NetSimSettings { Mesh = mesh } );
		var client = sim.Join();

		var id = sim.As( sim.Host, () =>
		{
			var go = new GameObject( true, "hotloaded prop" );
			go.WorldPosition = new Vector3( 239, 0, 0 );
			go.Components.Create<SimProp>().Health = 239;
			go.Network.Interpolation = false;
			go.NetworkSpawn( null );
			return go.Id;
		} );

		// Build a sequence that is well ahead of a restarted sender, and let the initial state settle.
		sim.RunSeconds( 4 );
		AssertClientState( 239 );

		// Real pre-hotload snapshots stay in flight until after the post-hotload state arrives.
		var link = sim.Transport.Settings;
		sim.Transport.Settings = link with { LatencySeconds = 2 };
		sim.As( sim.Host, () =>
		{
			var go = Game.ActiveScene.Directory.FindByGuid( id );
			go.WorldPosition = new Vector3( 500, 0, 0 );
			go.Components.Get<SimProp>().Health = 500;
		} );
		sim.RunSeconds( 0.2f );
		AssertClientState( 239 );
		sim.Transport.Settings = link;

		// Only the host hotloads. The client's stale-packet history must remain intact.
		sim.As( sim.Host, () =>
		{
			Game.ActiveScene.OnHotload();
			var go = Game.ActiveScene.Directory.FindByGuid( id );
			go.WorldPosition = new Vector3( 999, 0, 0 );
			go.Components.Get<SimProp>().Health = 999;
		} );

		// Neither value changes again: accepting and acknowledging a restarted id would strand both forever.
		sim.RunSeconds( 1 );
		AssertClientState( 999 );

		// Check every frame as the delayed older snapshots arrive, not just after a newer retry can repair them.
		for ( var frame = 0; frame < 3 * sim.Settings.FrameRate; frame++ )
		{
			sim.Step();
			AssertClientState( 999 );
		}

		void AssertClientState( int expected )
		{
			sim.As( client, () =>
			{
				var go = Game.ActiveScene.Directory.FindByGuid( id );
				Assert.IsTrue( go.IsValid(), "The client must retain the networked object." );
				Assert.AreEqual( new Vector3( expected, 0, 0 ), go.WorldPosition, $"Transform at frame {sim.Frame}" );
				Assert.AreEqual( expected, go.Components.Get<SimProp>().Health, $"[Sync] value at frame {sim.Frame}" );
			} );
		}
	}
}
