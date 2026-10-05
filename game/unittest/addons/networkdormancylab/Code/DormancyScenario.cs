using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DormancyLab;

public enum DormancyCase
{
	AlwaysTransmit,
	VisibilityWake,
	NestedTransformAndParent,
	EnabledState,
	ScalarSync,
	QueryCollections,
	ReliableCollections,
	LateJoin,
	OwnershipTransfer,
	QueryDestruction
}

public sealed class DormancyScenario : Component
{
	[Property] public DormancyCase Scenario { get; set; }
	[Property] public float SettleSeconds { get; set; } = 6f;
	[Property] public float ReplicationTimeout { get; set; } = 3f;
	[Property] public float ConnectionTimeout { get; set; } = 120f;
	[Property] public bool AutoAdvance { get; set; } = true;
	[Property] public string NextScene { get; set; }
	[Property] public float AdvanceDelay { get; set; } = 3f;
	[Sync( SyncFlags.FromHost )] public string Status { get; set; } = "Waiting for networking";
	[Sync( SyncFlags.FromHost )] public string Detail { get; set; } = "Start one host and one client.";

	private enum Phase { Waiting, Settling, Replicating, Checking, LateJoin, Finished, Advancing }
	private Phase _phase;
	private RealTimeSince _phaseTime;
	private int _round;
	private bool _initialized;
	private bool _checkingLateJoinSnapshot;
	private Guid _firstPeer;
	private readonly List<GameObject> _subjects = new();
	private readonly Dictionary<Guid, string> _expected = new();
	private readonly HashSet<Guid> _pendingPeers = new();
	private GameObject _parentA;
	private GameObject _parentB;

	private Connection[] Peers() => Connection.All.Where( x => x.Id != Connection.Local.Id && x.IsActive ).ToArray();

	protected override void OnUpdate()
	{
		if ( !Networking.IsActive || !Networking.IsHost || _phase == Phase.Finished )
			return;

		if ( !_initialized )
		{
			_initialized = true;
			if ( !Network.Active ) GameObject.NetworkSpawn();
			ChangePhase( Phase.Waiting, "WAIT: connect a client", "A host-only run cannot pass replication checks." );
		}

		switch ( _phase )
		{
			case Phase.Waiting:
				if ( Peers().Length > 0 )
				{
					if ( Scenario == DormancyCase.LateJoin && Peers().Length != 1 )
					{
						Fail( "LateJoin requires one initial client. Connect the second client only at the late-join prompt." );
						break;
					}
					_firstPeer = Peers()[0].Id;
					BuildSubjects();
					ChangePhase( Phase.Settling, "SETTLING: initial state", "Waiting for initial snapshots and acknowledgements." );
				}
				else if ( _phaseTime > ConnectionTimeout ) Fail( "No client connected before timeout." );
				break;
			case Phase.Settling:
			case Phase.Replicating:
				if ( _phaseTime >= (_phase == Phase.Settling ? SettleSeconds : ReplicationTimeout) )
					BeginCheck();
				break;
			case Phase.Checking:
				if ( _phaseTime > ReplicationTimeout ) Fail( $"Missing reports from {_pendingPeers.Count} client(s)." );
				break;
			case Phase.LateJoin:
				if ( Peers().Any( x => x.Id != _firstPeer ) )
				{
					_checkingLateJoinSnapshot = true;
					ChangePhase( Phase.Replicating, "REPLICATING: late-join snapshot", "The new client must receive value 42 before any second mutation." );
				}
				else if ( _phaseTime > ConnectionTimeout ) Fail( "A second client did not join before timeout." );
				break;
			case Phase.Advancing:
				if ( _phaseTime >= AdvanceDelay ) AdvanceScene();
				break;
		}
	}

	private void BuildSubjects()
	{
		if ( Scenario == DormancyCase.NestedTransformAndParent )
		{
			_parentA = Spawn<DormancyActor>( "Parent A", new Vector3( 0, -120, 48 ) );
			_parentB = Spawn<DormancyActor>( "Parent B", new Vector3( 0, 120, 48 ) );
			var child = Spawn<DormancyActor>( "Subject", new Vector3( 0, -120, 128 ), _parentA );
			_subjects.Add( child );
			return;
		}

		var subject = Scenario switch
		{
			DormancyCase.QueryCollections or DormancyCase.QueryDestruction => Spawn<QueryActor>( "Subject", new Vector3( 0, 0, 48 ) ),
			DormancyCase.ReliableCollections => Spawn<ReliableActor>( "Subject", new Vector3( 0, 0, 48 ) ),
			_ => Spawn<DormancyActor>( "Subject", new Vector3( 0, 0, 48 ) )
		};
		_subjects.Add( subject );
		if ( Scenario == DormancyCase.VisibilityWake || Scenario == DormancyCase.LateJoin )
			subject.Network.AlwaysTransmit = false;
		if ( Scenario == DormancyCase.LateJoin ) subject.Components.Get<DormancyActor>().Value = 42;
		if ( Scenario == DormancyCase.QueryDestruction )
			_subjects.Add( Spawn<QueryActor>( "Query Victim", new Vector3( 0, 120, 48 ) ) );
	}

	private GameObject Spawn<T>( string name, Vector3 position, GameObject parent = null ) where T : DormancyActor, new()
	{
		var go = new GameObject( parent ?? Scene, true, name );
		go.WorldPosition = position;
		go.Components.Create<T>();
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = name == "Subject" ? new Color( 0.15f, 0.7f, 1f ) : new Color( 1f, 0.6f, 0.15f );
		go.NetworkSpawn();
		return go;
	}

	private void ApplyRound( int round )
	{
		_round = round;
		var go = _subjects[0];
		var actor = go.Components.Get<DormancyActor>( FindMode.InSelf | FindMode.Enabled | FindMode.Disabled );
		switch ( Scenario )
		{
			case DormancyCase.AlwaysTransmit:
				actor.Visible = false;
				go.LocalPosition = new Vector3( round * 64, 0, 48 );
				break;
			case DormancyCase.VisibilityWake:
				actor.Visible = round == 2;
				if ( round == 2 ) go.LocalPosition = new Vector3( 128, 0, 48 );
				break;
			case DormancyCase.NestedTransformAndParent:
				if ( round == 2 ) go.Parent = _parentB;
				go.LocalPosition = new Vector3( round * 64, 0, 80 );
				break;
			case DormancyCase.EnabledState:
				go.Enabled = round == 2;
				break;
			case DormancyCase.ScalarSync:
				actor.Value = round * 100;
				break;
			case DormancyCase.QueryCollections:
				var query = (QueryActor)actor;
				query.SetQuerySource( round * 100 );
				query.PlainList.Add( round );
				query.PlainDictionary[round] = round * 10;
				break;
			case DormancyCase.ReliableCollections:
				var reliable = (ReliableActor)actor;
				if ( round == 1 )
				{
					reliable.Items.Add( 1 );
					reliable.Lookup[1] = 10;
				}
				else
				{
					reliable.Items.Clear();
					reliable.Items.Add( 2 );
					reliable.Lookup.Remove( 1 );
					reliable.Lookup[2] = 20;
				}
				break;
			case DormancyCase.LateJoin:
				actor.HiddenFrom = round == 1 ? _firstPeer : Guid.Empty;
				if ( round == 2 ) actor.Value = 84;
				break;
			case DormancyCase.OwnershipTransfer:
				go.Network.AssignOwnership( round == 1 ? Connection.Find( _firstPeer ) : Connection.Local );
				WriteFromOwner( go.Id, round );
				break;
			case DormancyCase.QueryDestruction:
				((QueryActor)actor).DestroyDuringNextQuery( round == 1 ? _subjects[1] : go );
				break;
		}
	}

	[Rpc.Broadcast]
	private void WriteFromOwner( Guid id, int round )
	{
		if ( Rpc.CallerId != Connection.Host.Id ) return;
		var go = Scene.Directory.FindByGuid( id );
		if ( !go.IsValid() || !go.Network.IsOwner ) return;
		go.Components.Get<DormancyActor>().Value = round * 100;
		go.LocalPosition = new Vector3( round * 64, 0, 48 );
	}

	private void BeginCheck()
	{
		if ( _round == 1 && !_checkingLateJoinSnapshot && (Scenario == DormancyCase.VisibilityWake || Scenario == DormancyCase.LateJoin)
			&& !_subjects[0].Network.IsDeltaDormant )
		{
			Fail( "The invisible subject did not enter delta dormancy." );
			return;
		}
		if ( Scenario == DormancyCase.AlwaysTransmit && _subjects[0].Network.IsDeltaDormant )
		{
			Fail( "An AlwaysTransmit object entered dormancy." );
			return;
		}
		if ( Scenario == DormancyCase.QueryDestruction && _round > 0
			&& (_round == 1 ? _subjects[1] : _subjects[0]).IsValid() )
		{
			Fail( "The query getter did not destroy the intended object." );
			return;
		}
		if ( Scenario == DormancyCase.OwnershipTransfer && _round > 0
			&& _subjects[0].Components.Get<DormancyActor>().Value != _round * 100 )
		{
			Fail( "The new owner did not publish its mutation." );
			return;
		}

		_expected.Clear();
		foreach ( var subject in _subjects ) _expected[subject.Id] = Describe( subject );
		_pendingPeers.Clear();
		foreach ( var peer in Peers() ) _pendingPeers.Add( peer.Id );
		if ( _pendingPeers.Count == 0 )
		{
			Fail( "All clients disconnected; replication cannot pass." );
			return;
		}
		ChangePhase( Phase.Checking, $"CHECK: round {_round}", $"Waiting for {_pendingPeers.Count} independent client observation(s)." );
		foreach ( var subject in _subjects ) Observe( subject.Id, _round );
	}

	[Rpc.Broadcast]
	private void Observe( Guid id, int round )
	{
		if ( Rpc.CallerId != Connection.Host.Id || Networking.IsHost ) return;
		ReportObservation( id, round, Describe( Scene.Directory.FindByGuid( id ) ) );
	}

	private readonly Dictionary<Guid, HashSet<Guid>> _reportedSubjects = new();

	[Rpc.Host]
	private void ReportObservation( Guid id, int round, string observed )
	{
		var peer = Rpc.CallerId;
		if ( _phase != Phase.Checking || round != _round || !_pendingPeers.Contains( peer ) ) return;
		if ( !_expected.TryGetValue( id, out var expected ) || expected != observed )
		{
			Fail( $"Client {peer}, round {round}: expected {expected}; observed {observed}" );
			return;
		}
		if ( !_reportedSubjects.TryGetValue( peer, out var subjects ) )
			_reportedSubjects[peer] = subjects = new();
		subjects.Add( id );
		if ( subjects.Count != _subjects.Count ) return;
		_pendingPeers.Remove( peer );
		if ( _pendingPeers.Count > 0 ) return;
		_reportedSubjects.Clear();
		Log.Info( $"DORMANCY CHECK {Scenario} round {round}: all clients matched" );
		if ( _round == 2 )
		{
			CompleteScenario();
			return;
		}
		if ( Scenario == DormancyCase.LateJoin && _round == 1 && !_checkingLateJoinSnapshot )
		{
			ChangePhase( Phase.LateJoin, "WAIT: start a second client now", "The subject is dormant for the first client. A new connection must receive its current state." );
			return;
		}
		ApplyRound( _round + 1 );
		var needsDormancy = _round == 1 && (Scenario == DormancyCase.VisibilityWake || Scenario == DormancyCase.LateJoin);
		ChangePhase( needsDormancy ? Phase.Settling : Phase.Replicating, $"REPLICATING: round {_round}", "No refresh snapshots or subject RPCs are used to force replication." );
	}

	private static string Describe( GameObject go )
	{
		if ( !go.IsValid() ) return "missing";
		var actor = go.Components.Get<DormancyActor>( FindMode.InSelf | FindMode.Enabled | FindMode.Disabled );
		var p = go.LocalPosition;
		// Scene display names can differ between host and client. Networked parents have shared IDs.
		var parent = go.Parent is null or Sandbox.Scene ? "scene" : go.Parent.Id.ToString();
		return $"{go.Name};enabled={go.Enabled};position={MathF.Round( p.x )},{MathF.Round( p.y )},{MathF.Round( p.z )};parent={parent};owner={go.Network.OwnerId};{actor.DescribeValues()}";
	}

	private void CompleteScenario()
	{
		if ( !AutoAdvance )
		{
			ChangePhase( Phase.Finished, "PASS: replicated state", "AutoAdvance is disabled. Check the console for engine exceptions." );
			return;
		}

		if ( string.IsNullOrWhiteSpace( NextScene ) )
		{
			ChangePhase( Phase.Finished, "COMPLETE: final scene passed", "The sequence has finished. Review host and client consoles for engine exceptions." );
			return;
		}

		ChangePhase( Phase.Advancing, "PASS: advancing", $"All client states matched. Loading {NextScene} for the host and connected clients in {AdvanceDelay:0.#} seconds." );
	}

	private void AdvanceScene()
	{
		var options = new SceneLoadOptions { DeleteEverything = true };
		if ( !options.SetScene( NextScene ) )
		{
			Fail( $"Cannot find next scene: {NextScene}" );
			return;
		}

		// Loading destroys this controller. Stop scheduling it before changing the shared scene.
		_phase = Phase.Finished;
		if ( !Game.ChangeScene( options ) )
		{
			Log.Error( $"DORMANCY: Failed to change to {NextScene}" );
			if ( IsValid ) Fail( $"Failed to change to {NextScene}" );
		}
	}

	private void ChangePhase( Phase phase, string status, string detail )
	{
		_phase = phase;
		_phaseTime = 0;
		Status = status;
		Detail = detail;
		Log.Info( $"DORMANCY {Scenario}: {status} — {detail}" );
	}

	private void Fail( string reason ) => ChangePhase( Phase.Finished, "FAIL", reason );
}
