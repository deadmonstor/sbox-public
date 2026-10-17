namespace NetSim;

/// <summary>
/// Ownership changes that race each other over a slow link must still leave every peer agreeing on the owner.
/// </summary>
[TestClass]
public class OwnershipTests
{
	[TestInitialize]
	public void Init() => ThreadSafe.MarkMainThread();

	static Guid SpawnProp( NetSimulation sim )
	{
		var id = sim.As( sim.Host, () =>
		{
			var go = new GameObject( true, "prop" );
			go.Components.Create<SimProp>();
			go.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
			go.NetworkSpawn( null );
			return go.Id;
		} );

		sim.RunSeconds( 1 );
		return id;
	}

	static GameObject Find( NetSimulation sim, SimPeer peer, Guid id ) => sim.As( peer, () => Game.ActiveScene.Directory.FindByGuid( id ) );

	static string Owners( NetSimulation sim, Guid id )
	{
		return string.Join( ", ", sim.Peers.Select( p => $"{p}: {sim.As( p, () => Find( sim, p, id ).Network.OwnerId )} v{sim.As( p, () => Find( sim, p, id ).Network.SnapshotVersion )}" ) );
	}

	static void AssertAgree( NetSimulation sim, Guid id )
	{
		var owners = sim.Peers.Select( p => sim.As( p, () => Find( sim, p, id ).Network.OwnerId ) ).Distinct().Count();
		var versions = sim.Peers.Select( p => sim.As( p, () => Find( sim, p, id ).Network.SnapshotVersion ) ).Distinct().Count();
		Assert.IsTrue( owners == 1 && versions == 1, Owners( sim, id ) );
	}

	static NetSimulation Slow() => new( new NetSimSettings { Link = new SimLinkSettings { LatencySeconds = 0.1f } } );

	[TestMethod]
	public void TwoClientsTakeAtOnce()
	{
		using var sim = Slow();
		var a = sim.Join();
		var b = sim.Join();
		var id = SpawnProp( sim );

		sim.As( a, () => Find( sim, a, id ).Network.TakeOwnership() );
		sim.As( b, () => Find( sim, b, id ).Network.TakeOwnership() );
		sim.RunSeconds( 1 );

		AssertAgree( sim, id );
	}

	[TestMethod]
	public void ClientAndHostTakeAtOnce()
	{
		using var sim = Slow();
		var a = sim.Join();
		sim.Join();
		var id = SpawnProp( sim );

		sim.As( a, () => Find( sim, a, id ).Network.TakeOwnership() );
		sim.As( sim.Host, () => Find( sim, sim.Host, id ).Network.TakeOwnership() );
		sim.RunSeconds( 1 );

		AssertAgree( sim, id );
	}

	[TestMethod]
	public void TakeWhileAnotherDrops()
	{
		using var sim = Slow();
		var a = sim.Join();
		var b = sim.Join();
		var id = SpawnProp( sim );

		sim.As( a, () => Find( sim, a, id ).Network.TakeOwnership() );
		sim.RunSeconds( 1 );

		sim.As( a, () => Find( sim, a, id ).Network.DropOwnership() );
		sim.As( b, () => Find( sim, b, id ).Network.TakeOwnership() );
		sim.RunSeconds( 1 );

		AssertAgree( sim, id );
	}

	[TestMethod]
	public void TakeWhileHostDrops()
	{
		using var sim = Slow();
		var a = sim.Join();
		var b = sim.Join();
		var id = SpawnProp( sim );

		sim.As( a, () => Find( sim, a, id ).Network.TakeOwnership() );
		sim.RunSeconds( 1 );

		// Same version on both sides, the take wins the tie over the host's drop
		sim.As( sim.Host, () => Find( sim, sim.Host, id ).Network.DropOwnership() );
		sim.As( b, () => Find( sim, b, id ).Network.TakeOwnership() );
		sim.RunSeconds( 1 );

		AssertAgree( sim, id );
		Assert.AreEqual( b.Local.Id, sim.As( b, () => Find( sim, b, id ).Network.OwnerId ), Owners( sim, id ) );
	}

	[TestMethod]
	[DataRow( false, 1, false )]
	[DataRow( false, 2, false )]
	[DataRow( true, 1, false )]
	[DataRow( true, 2, false )]
	[DataRow( false, 2, true )]
	[DataRow( true, 2, true )]
	public void RefreshDoesNotCompeteWithTakeover( bool mesh, int refreshes, bool wrap )
	{
		using var sim = new NetSimulation( new NetSimSettings { Mesh = mesh, Link = new SimLinkSettings { LatencySeconds = 0.1f } } );
		var clients = new[] { sim.Join(), sim.Join() }.OrderBy( x => x.Local.Id ).ToArray();
		var contender = clients[0];
		var owner = clients[1]; // The stale takeover must also work when it would lose an ownership tie.
		var id = sim.As( sim.Host, () =>
		{
			var go = new GameObject( true, "refreshed prop" );
			go.Components.Create<SimProp>();
			go.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
			go.Network.Interpolation = false;
			go.NetworkSpawn( Connection.Find( owner.Local.Id ) );
			return go.Id;
		} );
		sim.RunSeconds( 1 );

		if ( wrap )
		{
			foreach ( var peer in sim.Peers )
				sim.As( peer, () => Find( sim, peer, id )._net.LocalSnapshotState.Version = new( ushort.MaxValue, ushort.MaxValue ) );
		}

		sim.As( owner, () =>
		{
			for ( var i = 0; i < refreshes; i++ )
				Find( sim, owner, id ).Network.Refresh();
		} );
		// In a relay topology the host now has the refreshes, but the contender does not.
		sim.Run( 7 );
		sim.As( contender, () => Assert.IsTrue( Find( sim, contender, id ).Network.TakeOwnership() ) );
		sim.RunSeconds( 1 );

		AssertAgree( sim, id );
		foreach ( var peer in sim.Peers )
			Assert.AreEqual( contender.Local.Id, sim.As( peer, () => Find( sim, peer, id ).Network.OwnerId ), Owners( sim, id ) );

		// A converged owner id is not enough: snapshots from that owner must still update every peer.
		sim.As( contender, () =>
		{
			var go = Find( sim, contender, id );
			go.WorldPosition = new Vector3( 123, 456, 789 );
			go.Components.Get<SimProp>().Health = 321;
		} );
		sim.RunSeconds( 1 );
		foreach ( var peer in sim.Peers )
		{
			sim.As( peer, () =>
			{
				var go = Find( sim, peer, id );
				Assert.AreEqual( new Vector3( 123, 456, 789 ), go.WorldPosition );
				Assert.AreEqual( 321, go.Components.Get<SimProp>().Health );
			} );
		}
	}

	[TestMethod]
	[DataRow( 0 )]
	[DataRow( 1 )]
	[DataRow( 2 )]
	public void PreviousOwnershipRefreshCannotOverwriteNewOwner( int refreshKind )
	{
		using var sim = Slow();
		sim.Join();
		var owner = sim.Join();
		var id = sim.As( sim.Host, () =>
		{
			var go = new GameObject( true, "previous host ownership" );
			var child = new GameObject( go, true, "child" );
			child.Components.Create<OwnershipRefreshProbe>().Marker = 1;
			go.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
			go.NetworkSpawn( null );
			return go.Id;
		} );
		sim.RunSeconds( 1 );

		sim.As( sim.Host, () => Refresh( Find( sim, sim.Host, id ) ) );
		sim.As( owner, () =>
		{
			var go = Find( sim, owner, id );
			Assert.IsTrue( go.Network.TakeOwnership() );
			go.Components.GetInDescendants<OwnershipRefreshProbe>().Marker = 2;
		} );
		sim.RunSeconds( 1 );
		AssertAgree( sim, id );
		Assert.AreEqual( 2, sim.As( owner, () => Find( sim, owner, id ).Components.GetInDescendants<OwnershipRefreshProbe>().Marker ) );

		// Refreshes in the new ownership generation still apply, including to a late joiner.
		sim.As( owner, () => Refresh( Find( sim, owner, id ) ) );
		sim.RunSeconds( 1 );
		sim.Join();
		sim.RunSeconds( 1 );
		AssertAgree( sim, id );
		foreach ( var peer in sim.Peers )
		{
			Assert.AreEqual( owner.Local.Id, sim.As( peer, () => Find( sim, peer, id ).Network.OwnerId ) );
			Assert.AreEqual( 2, sim.As( peer, () => Find( sim, peer, id ).Components.GetInDescendants<OwnershipRefreshProbe>().Marker ) );
		}

		void Refresh( GameObject go )
		{
			var probe = go.Components.GetInDescendants<OwnershipRefreshProbe>();
			if ( refreshKind == 0 )
				go.Network.Refresh();
			else if ( refreshKind == 1 )
				go.Network.Refresh( probe );
			else
				go.Network.Refresh( probe.GameObject );
		}
	}

	[TestMethod]
	public void ManyClientsFightOverOneProp()
	{
		using var sim = Slow();
		var clients = Enumerable.Range( 0, 6 ).Select( _ => sim.Join() ).ToList();
		var id = SpawnProp( sim );
		var random = new Random( 3 );

		for ( var frame = 0; frame < 300; frame++ )
		{
			foreach ( var c in clients )
			{
				if ( random.NextDouble() < 0.02 )
					sim.As( c, () => Find( sim, c, id ).Network.TakeOwnership() );
				else if ( random.NextDouble() < 0.01 )
					sim.As( c, () => Find( sim, c, id ).Network.DropOwnership() );
			}

			sim.Step();
		}

		sim.RunSeconds( 1 );
		AssertAgree( sim, id );
	}
}

public sealed class OwnershipRefreshProbe : Component
{
	[Property] public int Marker { get; set; }
}
