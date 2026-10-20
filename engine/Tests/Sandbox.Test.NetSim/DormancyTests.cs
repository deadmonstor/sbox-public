namespace NetSim;

/// <summary>
/// Scalar-only state keeps these probes independent of collection polling and gameplay updates.
/// </summary>
public sealed class DormancyProbe : Component
{
	[Sync( SyncFlags.FromHost )] public int HostValue { get; set; } = 23;
	[Sync] public int OwnerValue { get; set; } = 11;
}

/// <summary>
/// One synced value of each kind that can change without going through a sync setter.
/// </summary>
public sealed class DormancyCollections : Component
{
	[Sync] public NetList<int> List { get; set; } = new();
	[Sync] public NetDictionary<string, int> Dictionary { get; set; } = new();
	[Sync] public List<int> Plain { get; set; } = new();

	public int QueriedField;
	[Sync( SyncFlags.Query )] public int Queried { get => QueriedField; set => QueriedField = value; }
}

[TestClass]
public class DormancyTests
{
	[TestInitialize]
	public void Init() => ThreadSafe.MarkMainThread();

	static readonly Vector3 InitialPosition = new( 10, 20, 30 );

	static GameObject Find( NetSimulation sim, SimPeer peer, Guid id ) =>
		sim.As( peer, () => Game.ActiveScene.Directory.FindByGuid( id ) );

	static Guid Spawn( NetSimulation sim, SimPeer owner, bool collections = false )
	{
		return sim.As( sim.Host, () =>
		{
			var go = new GameObject( true, "dormancy-probe" );
			go.Components.Create<DormancyProbe>();
			if ( collections ) go.Components.Create<DormancyCollections>();
			go.WorldPosition = InitialPosition;
			go.Network.Interpolation = false;
			go.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
			if ( owner == sim.Host )
				go.NetworkSpawn();
			else
				go.NetworkSpawn( Networking.System.FindConnection( owner.Local.Id ) );
			return go.Id;
		} );
	}

	static void AssertReplicas( NetSimulation sim, Guid id, SimPeer owner, int hostValue, int ownerValue, Vector3 position )
	{
		foreach ( var peer in sim.Peers )
		{
			sim.As( peer, () =>
			{
				var go = Find( sim, peer, id );
				Assert.IsNotNull( go, $"{peer} doesn't have the object" );
				var probe = go.Components.Get<DormancyProbe>();
				Assert.IsNotNull( probe, $"{peer} doesn't have the probe" );
				Assert.AreEqual( owner.Local.Id, go.Network.OwnerId, $"{peer} has a stale owner" );
				Assert.AreEqual( hostValue, probe.HostValue, $"{peer} has a stale FromHost value" );
				Assert.AreEqual( ownerValue, probe.OwnerValue, $"{peer} has a stale owner-authored value" );
				Assert.AreEqual( position, go.WorldPosition, $"{peer} has a stale position" );
			} );
		}
	}

	[TestMethod]
	[DataRow( false, 0f )]
	[DataRow( true, 0.15f )]
	public void HostScalarWakesIdleClientOwnedObject( bool ownerMoves, float loss )
	{
		using var sim = new NetSimulation( new NetSimSettings
		{
			Seed = 7,
			Link = new SimLinkSettings { LatencySeconds = 0.05f, Loss = loss }
		} );
		var owner = sim.Join();
		sim.Join();
		var id = Spawn( sim, owner );

		// Let creation and all acknowledgements settle without any gameplay writes.
		sim.RunSeconds( 2 );
		AssertReplicas( sim, id, owner, 23, 11, InitialPosition );

		// The host is a proxy, but remains authoritative for this one scalar.
		sim.As( sim.Host, () => Find( sim, sim.Host, id ).Components.Get<DormancyProbe>().HostValue = 91 );

		var finalPosition = InitialPosition;
		if ( ownerMoves )
		{
			sim.Run( 60, frame =>
			{
				finalPosition = InitialPosition + new Vector3( frame + 1, 0, 0 );
				sim.As( owner, () => Find( sim, owner, id ).WorldPosition = finalPosition );
			} );
		}

		sim.RunSeconds( 2 );
		AssertReplicas( sim, id, owner, 91, 11, finalPosition );
	}

	[TestMethod]
	public void IdleProxyCanTakeOwnershipAndAuthorState()
	{
		using var sim = new NetSimulation();
		var previousOwner = sim.Join();
		var newOwner = sim.Join();
		var id = Spawn( sim, previousOwner );

		sim.RunSeconds( 2 );
		AssertReplicas( sim, id, previousOwner, 23, 11, InitialPosition );

		var firstPosition = new Vector3( 100, 200, 300 );
		sim.As( newOwner, () =>
		{
			var go = Find( sim, newOwner, id );
			Assert.IsTrue( go.Network.TakeOwnership() );
			go.WorldPosition = firstPosition;
			go.Components.Get<DormancyProbe>().OwnerValue = 47;
		} );

		sim.RunSeconds( 2 );
		AssertReplicas( sim, id, newOwner, 23, 47, firstPosition );

		// Author again after the ownership transition itself has gone idle.
		var secondPosition = new Vector3( -40, 50, 60 );
		sim.As( newOwner, () =>
		{
			var go = Find( sim, newOwner, id );
			go.WorldPosition = secondPosition;
			go.Components.Get<DormancyProbe>().OwnerValue = 83;
		} );
		sim.RunSeconds( 2 );
		AssertReplicas( sim, id, newOwner, 23, 83, secondPosition );
	}

	[TestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void LocallyAuthoredFastOrExactTransformWakesIdleObject( bool exact )
	{
		using var sim = new NetSimulation();
		sim.Join();
		sim.Join();
		var id = Spawn( sim, sim.Host );

		sim.RunSeconds( 2 );
		AssertReplicas( sim, id, sim.Host, 23, 11, InitialPosition );

		var position = new Vector3( 120, -240, 360 );
		sim.As( sim.Host, () =>
		{
			var go = Find( sim, sim.Host, id );
			var transform = new Transform( position );
			if ( exact )
			{
				go.Transform.SetLocalTransformExact( transform );
			}
			else
			{
				// Match animation's batched setter plus its final cache/callback update.
				go.Transform.SetLocalTransformFast( transform );
				go.Transform.TransformChanged( true );
			}
		} );

		sim.RunSeconds( 2 );
		AssertReplicas( sim, id, sim.Host, 23, 11, position );
	}

	static void Mutate( NetSimulation sim, SimPeer peer, Guid id, Action<DormancyCollections> mutate ) =>
		sim.As( peer, () => mutate( Find( sim, peer, id ).Components.Get<DormancyCollections>() ) );

	static void AssertCollections( NetSimulation sim, Guid id, int[] list, int? dictionaryValue, int[] plain, int queried )
	{
		foreach ( var peer in sim.Peers )
		{
			sim.As( peer, () =>
			{
				var c = Find( sim, peer, id )?.Components.Get<DormancyCollections>();
				Assert.IsNotNull( c, $"{peer} doesn't have the collections" );
				CollectionAssert.AreEqual( list, c.List.ToArray(), $"{peer} has a stale NetList" );
				Assert.AreEqual( dictionaryValue, c.Dictionary.TryGetValue( "key", out var v ) ? v : null, $"{peer} has a stale NetDictionary" );
				CollectionAssert.AreEqual( plain, c.Plain.ToArray(), $"{peer} has a stale plain list" );
				Assert.AreEqual( queried, c.Queried, $"{peer} has a stale query value" );
			} );
		}
	}

	/// <summary>
	/// Network containers wake their object instead of being polled, while plain collections and
	/// query properties are still polled. Each change happens after the object has gone to sleep.
	/// </summary>
	[TestMethod]
	[DataRow( true )]
	[DataRow( false )]
	public void IdleObjectReplicatesUninterceptedChanges( bool hostOwned )
	{
		using var sim = new NetSimulation();
		var client = sim.Join();
		sim.Join();
		var owner = hostOwned ? sim.Host : client;
		var id = Spawn( sim, owner, collections: true );

		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [], null, [], 0 );

		Mutate( sim, owner, id, c => c.List.Add( 5 ) );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [5], null, [], 0 );

		Mutate( sim, owner, id, c => c.Dictionary["key"] = 9 );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [5], 9, [], 0 );

		Mutate( sim, owner, id, c => c.Plain.Add( 3 ) );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [5], 9, [3], 0 );

		Mutate( sim, owner, id, c => c.QueriedField = 12 );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [5], 9, [3], 12 );

		Mutate( sim, owner, id, c =>
		{
			c.List.Clear();
			c.Dictionary.Remove( "key" );
		} );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [], null, [3], 12 );
	}

	[TestMethod]
	public void IdleObjectReplicatesReassignedNetList()
	{
		using var sim = new NetSimulation();
		sim.Join();
		var id = Spawn( sim, sim.Host, collections: true );

		sim.RunSeconds( 2 );
		Mutate( sim, sim.Host, id, c => c.List = new NetList<int> { 1 } );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [1], null, [], 0 );

		// The replacement list must wake the sleeping object by itself.
		Mutate( sim, sim.Host, id, c => c.List.Add( 2 ) );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [1, 2], null, [], 0 );
	}

	/// <summary>
	/// A component added after spawn registers entries that need polling.
	/// </summary>
	[TestMethod]
	public void IdleObjectPollsComponentAddedAfterSpawn()
	{
		using var sim = new NetSimulation();
		sim.Join();
		var id = Spawn( sim, sim.Host );

		sim.RunSeconds( 2 );
		sim.As( sim.Host, () =>
		{
			var go = Find( sim, sim.Host, id );
			go.Components.Create<DormancyCollections>();
			go.Network.Refresh();
		} );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [], null, [], 0 );

		Mutate( sim, sim.Host, id, c => c.Plain.Add( 4 ) );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [], null, [4], 0 );
	}

	/// <summary>
	/// An idle proxy that takes ownership becomes responsible for polling and notifying its own changes.
	/// </summary>
	[TestMethod]
	public void IdleProxyPollsAfterTakingOwnership()
	{
		using var sim = new NetSimulation();
		var previousOwner = sim.Join();
		var newOwner = sim.Join();
		var id = Spawn( sim, previousOwner, collections: true );

		sim.RunSeconds( 2 );
		sim.As( newOwner, () => Assert.IsTrue( Find( sim, newOwner, id ).Network.TakeOwnership() ) );
		sim.RunSeconds( 2 );

		Mutate( sim, newOwner, id, c => c.Plain.Add( 6 ) );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [], null, [6], 0 );

		Mutate( sim, newOwner, id, c => c.List.Add( 8 ) );
		sim.RunSeconds( 2 );
		AssertCollections( sim, id, [8], null, [6], 0 );
	}
}
