using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Internal;
using Sandbox.Network;
using SceneTests;

namespace SceneTests.GameObjects;

using static GlobalGameNamespace;

/// <summary>
/// Covers delta-snapshot dormancy: objects that nobody can see stop being evaluated every frame,
/// the scene only walks a dirty set instead of every networked object, and anything that changes
/// replicated state has to wake the object back up again.
/// </summary>
[TestClass]
public class NetworkDormancyTest
{
	private const float CullThreshold = 2f;
	private const float DormancyThreshold = 3f;
	private const float DormancyProbeInterval = 0.2f;
	private const int SnapshotPositionSlot = 1;
	private const int SnapshotRotationSlot = 2;

	/// <summary>
	/// Arbitrary non-zero start time. Time.Now is driven by hand throughout these tests, because
	/// dormancy is entirely a function of how long an object has been invisible.
	/// </summary>
	private const float StartTime = 1000f;

	private TypeLibrary _oldTypeLibrary;
	private float _oldTimeNow;
	private double _oldTimeNowDouble;

	[TestInitialize]
	public void TestInitialize()
	{
		_oldTypeLibrary = Game.TypeLibrary;

		Game.TypeLibrary = new TypeLibrary();
		Game.TypeLibrary.AddAssembly( typeof( PrefabFile ).Assembly, false );
		Game.TypeLibrary.AddAssembly( typeof( ModelRenderer ).Assembly, false );
		Game.TypeLibrary.AddAssembly( typeof( DormancyTestComponent ).Assembly, false );

		JsonUpgrader.UpdateUpgraders( Game.TypeLibrary );

		_oldTimeNow = Time.Now;
		_oldTimeNowDouble = Time.NowDouble;

		SetTime( StartTime );
	}

	[TestCleanup]
	public void TestCleanup()
	{
		Game.TypeLibrary = _oldTypeLibrary;

		Time.Now = _oldTimeNow;
		Time.NowDouble = _oldTimeNowDouble;
	}

	[TestMethod]
	public void NoConnectionsSkipsWorkWithoutGoingDormant()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );

		Assert.IsTrue( go._net.ShouldSkipDeltaSnapshotUpdate( Array.Empty<Connection>() ) );
		Assert.IsFalse( go._net.IsDeltaDormant );
		Assert.IsFalse( UpdateTransmit( go ) );
	}

	[TestMethod]
	public void VisibleObjectNeverGoesDormant()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: true );
		var targets = Targets( clientAndHost.Client );

		for ( var i = 0; i < 20; i++ )
		{
			SetTime( StartTime + i * 0.5f );

			Assert.IsTrue( UpdateTransmit( go, targets ), $"Should still transmit at t+{i * 0.5f}" );
			Assert.IsFalse( go._net.IsDeltaDormant, $"Went dormant while visible at t+{i * 0.5f}" );
		}
	}

	[TestMethod]
	public void AlwaysTransmitObjectNeverGoesDormant()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = new GameObject();
		go.Components.Create<VisibilityController>().Visible = false;
		go.NetworkSpawn();

		var targets = Targets( clientAndHost.Client );

		for ( var i = 0; i < 20; i++ )
		{
			SetTime( StartTime + i * 0.5f );

			Assert.IsTrue( UpdateTransmit( go, targets ), $"AlwaysTransmit stopped transmitting at t+{i * 0.5f}" );
			Assert.IsFalse( go._net.IsDeltaDormant, $"AlwaysTransmit went dormant at t+{i * 0.5f}" );
		}
	}

	[TestMethod]
	public void HiddenObjectIsCulledOnFirstEvaluation()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );

		Assert.IsTrue( UpdateTransmit( go, targets ), "First pass reports the pre-cull state" );
		Assert.IsFalse( UpdateTransmit( go, targets ), "Should already be culled on the second pass" );
		Assert.IsFalse( go._net.IsDeltaDormant, "Culling isn't dormancy - that needs the longer threshold" );
	}

	[TestMethod]
	public void CulledObjectDoesNotGoDormantBeforeThreshold()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );

		UpdateTransmit( go, targets );

		SetTime( StartTime + (DormancyThreshold - CullThreshold) - 0.1f );

		Assert.IsFalse( UpdateTransmit( go, targets ) );
		Assert.IsFalse( go._net.IsDeltaDormant, "Went dormant before DormancyThreshold elapsed" );
	}

	[TestMethod]
	public void CulledObjectGoesDormantAfterThreshold()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );

		UpdateTransmit( go, targets );

		SetTime( StartTime + DormancyThreshold + 0.5f );
		Assert.IsFalse( UpdateTransmit( go, targets ) );
		Assert.IsFalse( go._net.IsDeltaDormant, "Should re-arm the probe before going dormant" );

		SetTime( StartTime + DormancyThreshold + 0.6f );
		Assert.IsFalse( UpdateTransmit( go, targets ) );
		Assert.IsTrue( go._net.IsDeltaDormant, "Should be dormant once the threshold has passed" );
		Assert.IsTrue( go.Network.IsDeltaDormant, "Public accessor should agree" );
	}

	[TestMethod]
	public void DormantObjectSkipsUpdateUntilProbeFires()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );
		var dormantAt = ForceDormant( go, targets );

		go.Components.Get<VisibilityController>().Visible = true;

		SetTime( dormantAt + DormancyProbeInterval * 0.5f );

		Assert.IsTrue( go._net.ShouldSkipDeltaSnapshotUpdate( targets ), "Should still be skipping work" );
		Assert.IsTrue( go._net.IsDeltaDormant );
	}

	[TestMethod]
	public void DormantObjectWakesWhenVisibleAgain()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );
		var dormantAt = ForceDormant( go, targets );

		go.Components.Get<VisibilityController>().Visible = true;

		SetTime( dormantAt + DormancyProbeInterval + 0.01f );

		Assert.IsTrue( UpdateTransmit( go, targets ), "Should transmit again once visible" );
		Assert.IsFalse( go._net.IsDeltaDormant, "Should have woken up" );
	}

	[TestMethod]
	public void ProbeVisibilityMarksDirtyWhenObjectWakes()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );
		var dormantAt = ForceDormant( go, targets );

		go._net.IsDirty = false;

		SetTime( dormantAt + DormancyProbeInterval * 0.5f );
		Assert.IsFalse( go._net.ProbeVisibility( targets ), "Probe shouldn't fire yet" );
		Assert.IsFalse( go._net.IsDirty );

		go.Components.Get<VisibilityController>().Visible = true;

		SetTime( dormantAt + DormancyProbeInterval + 0.01f );
		Assert.IsTrue( go._net.ProbeVisibility( targets ), "Probe should have found it visible" );
		Assert.IsTrue( go._net.IsDirty, "Waking from a probe must put the object back in the dirty set" );
	}

	[TestMethod]
	public void NewConnectionWakesDormantObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );
		ForceDormant( go, targets );

		var latecomer = new MockConnection( Guid.NewGuid() );
		var withLatecomer = Targets( clientAndHost.Client, latecomer );

		Assert.IsFalse( go._net.ShouldSkipDeltaSnapshotUpdate( withLatecomer ),
			"An unevaluated connection must force a full visibility pass" );

		UpdateTransmit( go, withLatecomer );

		Assert.IsFalse( go._net.IsDeltaDormant, "Should have woken up for the new connection" );
	}

	[TestMethod]
	public void RemovingConnectionClearsDormancy()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );
		ForceDormant( go, targets );

		go._net.RemoveConnection( clientAndHost.Client.Id );

		Assert.IsFalse( go._net.IsDeltaDormant );
	}

	[TestMethod]
	public void HostChangeClearsDormancy()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );
		ForceDormant( go, targets );

		go._net.OnHostChanged( clientAndHost.Host, clientAndHost.Host );

		Assert.IsFalse( go._net.IsDeltaDormant, "A host change invalidates every ack, so nothing can stay dormant" );
	}

	[TestMethod]
	public void DormantObjectCountTracksDormancy()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var hidden = SpawnCullable( visible: false );
		var visible = SpawnCullable( visible: true );
		var targets = Targets( clientAndHost.Client );

		Assert.AreEqual( 2, scene.NetworkObjectCount );
		Assert.AreEqual( 0, scene.NetworkDormantObjectCount );

		ForceDormant( hidden, targets );
		UpdateTransmit( visible, targets );

		Assert.AreEqual( 1, scene.NetworkDormantObjectCount );
	}

	[TestMethod]
	public void MarkDirtyClearsDormancy()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		ForceDormant( go, Targets( clientAndHost.Client ) );

		go._net.IsDirty = false;
		go._net.MarkDirty();

		Assert.IsFalse( go._net.IsDeltaDormant );
		Assert.IsTrue( go._net.IsDirty );
	}

	[TestMethod]
	public void MarkDirtyOnDestroyedObjectDoesNotThrow()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var net = go._net;

		go.DestroyImmediate();

		net.MarkDirty();
	}

	[TestMethod]
	public void TransformChangeWakesObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		ForceDormant( go, Targets( clientAndHost.Client ) );
		go._net.IsDirty = false;

		go.WorldPosition = new Vector3( 0f, 0f, 128f );

		AssertAwake( go, "Moving an object must wake it" );
	}

	[TestMethod]
	public void TransformChangeBumpsRevision()
	{
		using var scope = new Scene().Push();

		var go = new GameObject();
		var revision = go.Transform.Revision;

		go.WorldPosition = new Vector3( 0f, 0f, 128f );
		Assert.AreNotEqual( revision, go.Transform.Revision, "Moving should bump the transform revision" );

		revision = go.Transform.Revision;
		go.WorldRotation = Rotation.From( 0f, 90f, 0f );
		Assert.AreNotEqual( revision, go.Transform.Revision, "Rotating should bump the transform revision" );

		revision = go.Transform.Revision;
		go.WorldScale = Vector3.One * 2f;
		Assert.AreNotEqual( revision, go.Transform.Revision, "Scaling should bump the transform revision" );
	}

	[TestMethod]
	public void EnabledChangeWakesObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		ForceDormant( go, Targets( clientAndHost.Client ) );
		go._net.IsDirty = false;

		go.Enabled = false;

		AssertAwake( go, "Toggling Enabled must wake the object" );
	}

	[TestMethod]
	public void ComponentEnabledChangeWakesObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var comp = go.Components.Create<DormancyTestComponent>();

		ForceDormant( go, Targets( clientAndHost.Client ) );
		go._net.IsDirty = false;

		comp.Enabled = false;

		AssertAwake( go, "Disabling a component must wake the object" );
	}

	[TestMethod]
	public void ParentChangeWakesObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var newParent = new GameObject();
		newParent.NetworkSpawn();

		var go = SpawnCullable( visible: false );
		ForceDormant( go, Targets( clientAndHost.Client ) );
		go._net.IsDirty = false;

		go.Parent = newParent;

		AssertAwake( go, "Reparenting must wake the object" );
	}

	[TestMethod]
	public void SyncPropertyChangeWakesObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = new GameObject();
		go.Components.Create<VisibilityController>().Visible = false;
		var controller = go.Components.Create<CharacterController>();

		go.NetworkSpawn();
		go.Network.AlwaysTransmit = false;

		Assert.IsTrue( go._net.dataTable.IsRegistered(
			NetworkObject.GetPropertySlot( TypeLibrary.GetType<CharacterController>().GetProperty( "Velocity" ).Identity, controller.Id ) ),
			"Velocity should be a registered sync property" );

		ForceDormant( go, Targets( clientAndHost.Client ) );
		go._net.IsDirty = false;

		controller.Velocity = new Vector3( 0f, 0f, 100f );

		AssertAwake( go, "Writing a sync property must wake the object" );
	}

	[TestMethod]
	public void NetListChangeWakesObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var comp = go.Components.Create<DormancyTestComponent>();
		comp.SyncList = new NetList<int>();

		go.Network.Refresh();

		ForceDormant( go, Targets( clientAndHost.Client ) );
		go._net.IsDirty = false;

		comp.SyncList.Add( 1 );

		AssertAwake( go, "Mutating a NetList must wake the owning object" );
	}

	[TestMethod]
	public void NetDictionaryChangeWakesObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var comp = go.Components.Create<DormancyTestComponent>();
		comp.SyncDictionary = new NetDictionary<int, int>();

		go.Network.Refresh();

		ForceDormant( go, Targets( clientAndHost.Client ) );
		go._net.IsDirty = false;

		comp.SyncDictionary[1] = 2;

		AssertAwake( go, "Mutating a NetDictionary must wake the owning object" );
	}

	[TestMethod]
	public void HotloadWakesObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		ForceDormant( go, Targets( clientAndHost.Client ) );
		go._net.IsDirty = false;

		go._net.OnHotload();

		AssertAwake( go, "A hotload rebuilds the data table, so everything has to be re-sent" );
	}

	[TestMethod]
	public void TransmitStateChangeWakesObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		ForceDormant( go, Targets( clientAndHost.Client ) );
		go._net.IsDirty = false;

		go._net.TransmitStateChanged();

		AssertAwake( go, "Changing transmit state drops every ack, so everything has to be re-sent" );
	}

	[TestMethod]
	public void SpawningRegistersObjectAsDirty()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		Assert.AreEqual( 0, scene.NetworkObjectCount );

		var go = SpawnCullable( visible: false );

		Assert.AreEqual( 1, scene.NetworkObjectCount );
		Assert.IsTrue( go._net.IsDirty, "A freshly spawned object has never been sent, so it starts dirty" );
	}

	[TestMethod]
	public void UnregisteringClearsDirtyFlag()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var net = go._net;

		Assert.IsTrue( net.IsDirty );

		go.DestroyImmediate();

		Assert.AreEqual( 0, scene.NetworkObjectCount );
		Assert.IsFalse( net.IsDirty, "A destroyed object must not be left flagged as dirty" );
	}

	[TestMethod]
	public void MarkNetworkObjectDirtyIsIdempotent()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );

		go._net.IsDirty = false;
		scene.MarkNetworkObjectDirty( go._net );
		scene.MarkNetworkObjectDirty( go._net );

		Assert.IsTrue( go._net.IsDirty );
	}

	[TestMethod]
	public void TransformSlotsAreSkippedWhenRevisionIsUnchanged()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = new GameObject();
		go.NetworkSpawn();

		IDeltaSnapshot net = go._net;
		var state = net.WriteSnapshotState();

		Assert.IsTrue( state.Lookup.ContainsKey( SnapshotPositionSlot ), "Position should be written the first time" );

		state.Remove( SnapshotPositionSlot );
		net.WriteSnapshotState();

		Assert.IsFalse( state.Lookup.ContainsKey( SnapshotPositionSlot ),
			"Transform slots shouldn't be re-evaluated when the transform hasn't changed" );

		go.WorldPosition = new Vector3( 0f, 0f, 64f );
		net.WriteSnapshotState();

		Assert.IsTrue( state.Lookup.ContainsKey( SnapshotPositionSlot ),
			"Moving the object should re-write the transform slots" );
	}

	[TestMethod]
	public void TransformSlotsAreRehashedWhenFlagsChange()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = new GameObject();
		go.NetworkSpawn();

		IDeltaSnapshot net = go._net;
		var state = net.WriteSnapshotState();

		var positionHash = state.Lookup[SnapshotPositionSlot].Hash;
		var rotationHash = state.Lookup[SnapshotRotationSlot].Hash;

		go.Network.Flags |= NetworkFlags.NoInterpolation;
		net.WriteSnapshotState();

		Assert.AreNotEqual( positionHash, state.Lookup[SnapshotPositionSlot].Hash,
			"Position hash should change when the flags salt changes" );
		Assert.AreNotEqual( rotationHash, state.Lookup[SnapshotRotationSlot].Hash,
			"Rotation hash should change when the flags salt changes" );
	}

	[TestMethod]
	public void SendSkipsDormantObjectEntirely()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );
		ForceDormant( go, targets );

		go._net.IsFullyUpdated = true;

		var before = clientAndHost.Client.Messages.Count;
		SceneNetworkSystem.Instance.DeltaSnapshots.Send( new IDeltaSnapshot[] { go._net }, targets );

		Assert.IsTrue( go._net.IsDeltaDormant, "Should still be dormant" );
		Assert.IsTrue( go._net.IsFullyUpdated, "Send should have skipped a dormant object without touching it" );
		Assert.AreEqual( before, clientAndHost.Client.Messages.Count, "Nothing should have been sent" );
	}

	[TestMethod]
	public void SendClearsFullyUpdatedForHiddenObject()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: false );
		var targets = Targets( clientAndHost.Client );

		UpdateTransmit( go, targets );
		Assert.IsFalse( go._net.IsDeltaDormant );

		go._net.IsFullyUpdated = true;

		SceneNetworkSystem.Instance.DeltaSnapshots.Send( new IDeltaSnapshot[] { go._net }, targets );

		Assert.IsFalse( go._net.IsFullyUpdated,
			"No snapshot was written, so the object must not look acknowledged and get dropped from the dirty set" );
	}

	[TestMethod]
	public void SendLeavesUnacknowledgedObjectDirty()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeHost();

		var go = SpawnCullable( visible: true );
		var targets = Targets( clientAndHost.Client );

		go._net.IsFullyUpdated = true;

		SceneNetworkSystem.Instance.DeltaSnapshots.Send( new IDeltaSnapshot[] { go._net }, targets );

		Assert.IsFalse( go._net.IsFullyUpdated,
			"The client hasn't acked anything yet, so the object isn't fully updated" );
	}

	[TestMethod]
	public void SendMarksProxyObjectsFullyUpdatedOnClients()
	{
		using var scope = new Scene().Push();
		using var clientAndHost = new ClientAndHost( TypeLibrary );
		clientAndHost.BecomeClient();

		var go = new GameObject();
		go.NetworkSpawn( clientAndHost.Host );
		Assert.IsTrue( go.IsProxy );

		go._net.IsFullyUpdated = false;

		SceneNetworkSystem.Instance.DeltaSnapshots.Send( new IDeltaSnapshot[] { go._net }, Targets( clientAndHost.Host ) );

		Assert.IsTrue( go._net.IsFullyUpdated,
			"A client never sends proxies, so they must not be kept in the dirty set forever" );
	}

	private static void SetTime( float now )
	{
		Time.Now = now;
		Time.NowDouble = now;
	}

	private static Connection[] Targets( params Connection[] connections ) => connections;

	private static bool UpdateTransmit( GameObject go, Connection[] targets = null )
		=> ((IDeltaSnapshot)go._net).UpdateTransmitState( targets ?? Array.Empty<Connection>() );

	/// <summary>
	/// Spawns a cullable (not AlwaysTransmit) networked object with controllable visibility.
	/// </summary>
	private static GameObject SpawnCullable( bool visible )
	{
		var go = new GameObject();
		go.Components.Create<VisibilityController>().Visible = visible;
		go.NetworkSpawn();
		go.Network.AlwaysTransmit = false;
		return go;
	}

	/// <summary>
	/// Drives an invisible object all the way into dormancy and returns the time it happened at.
	/// Leaves <see cref="Time.Now"/> at that time.
	/// </summary>
	private static float ForceDormant( GameObject go, Connection[] targets )
	{
		SetTime( StartTime );
		UpdateTransmit( go, targets );

		SetTime( StartTime + DormancyThreshold + 0.5f );
		UpdateTransmit( go, targets );

		var dormantAt = StartTime + DormancyThreshold + 0.6f;
		SetTime( dormantAt );
		UpdateTransmit( go, targets );

		Assert.IsTrue( go._net.IsDeltaDormant, "Test setup failed to make the object dormant" );

		return dormantAt;
	}

	private static void AssertAwake( GameObject go, string because )
	{
		Assert.IsFalse( go._net.IsDeltaDormant, because );
		Assert.IsTrue( go._net.IsDirty, because );
	}

	private class VisibilityController : Component, Component.INetworkVisible
	{
		public bool Visible;

		public bool IsVisibleToConnection( Connection connection, in BBox worldBounds ) => Visible;
	}

	private class DormancyTestComponent : Component
	{
		[Sync] public NetList<int> SyncList { get; set; }
		[Sync] public NetDictionary<int, int> SyncDictionary { get; set; }
	}
}
