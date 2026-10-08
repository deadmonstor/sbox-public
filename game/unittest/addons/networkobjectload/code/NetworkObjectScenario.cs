using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NetworkObjectLoad;

public enum Workload
{
	Empty,
	LocalIdle,
	LocalMoving,
	LocalChanging,
	IdleBare,
	IdleSynced,
	Moving,
	Changing,
	SparseChanges,
	Visibility,
	Lifecycle,
	LateJoin,
	Mixed
}

public class Payload : Component
{
	[Sync] public int Value { get; set; }
	[Sync] public int Health { get; set; } = 100;
	[Sync] public Vector3 Target { get; set; }
	[Sync] public bool Flag { get; set; } = true;

	public void Mutate( int tick, int index )
	{
		Value = tick;
		Health = tick % 100;
		Target = new Vector3( tick % 100, index % 100, 0 );
		Flag = tick % 2 == 0;
	}
}

public sealed class VisibilityGate : Component, Component.INetworkVisible
{
	public bool Visible { get; set; }
	public bool IsVisibleToConnection( Connection connection, in BBox worldBounds ) => Visible;
}

public sealed class NetworkObjectScenario : Component
{
	[Property] public Workload Scenario { get; set; } = Workload.IdleSynced;
	[Property] public int Population { get; set; } = 1000;
	[Property] public int PropertyGroups { get; set; } = 1;
	[Property] public float ActiveFraction { get; set; } = 0.1f;
	[Property] public int MinimumClients { get; set; } = 1;
	[Property] public int SpawnPerTick { get; set; } = 100;
	[Property] public int ChildrenPerObject { get; set; }
	[Property] public int WarmupTicks { get; set; } = 500;
	[Property] public int RunTicks { get; set; } = 1500;
	[Property] public int LifecycleIntervalTicks { get; set; } = 50;
	[Property] public int LifecycleBurst { get; set; } = 10;
	[Property] public int RpcsPerTick { get; set; }
	[Property] public float SettleSeconds { get; set; } = 5;
	[Property] public float TimeoutSeconds { get; set; } = 120;
	[Property] public bool RenderSubjects { get; set; }
	[Sync( SyncFlags.FromHost )] public string Status { get; set; } = "Waiting for host and clients";
	[Sync( SyncFlags.FromHost )] public string Detail { get; set; } = "Restart Play to reset. No performance samples collected.";

	private enum Phase { Waiting, Spawning, Warmup, Running, Settling, Checking, Finished }
	private sealed class Subject
	{
		public GameObject Object;
		public Payload[] Payloads;
		public VisibilityGate Gate;
		public int Generation;
	}

	private readonly List<Subject> _subjects = new();
	private readonly HashSet<Guid> _participants = new();
	private readonly HashSet<Guid> _pendingReports = new();
	private Phase _phase;
	private RealTimeSince _phaseTime;
	private bool _initialized;
	private bool _replicationChecked;
	private int _phaseTicks;
	private int _scheduleTick;
	private int _completedRunTicks;
	private int _lifecycleCursor;
	private long _mutations;
	private long _spawns;
	private long _destroys;
	private long _rpcs;
	private ulong _expectedDigest;
	private int _expectedCount;
	private bool LocalOnly => Scenario is Workload.LocalIdle or Workload.LocalMoving or Workload.LocalChanging;
	private bool Moves => Scenario is Workload.Moving or Workload.LocalMoving or Workload.Mixed;
	private bool Changes => Scenario is Workload.Changing or Workload.LocalChanging or Workload.SparseChanges or Workload.Mixed;
	private bool Churns => Scenario is Workload.Lifecycle or Workload.Mixed;

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsActive || !Networking.IsHost || _phase == Phase.Finished ) return;
		if ( !_initialized )
		{
			_initialized = true;
			_phaseTime = 0;
			if ( !Network.Active ) GameObject.NetworkSpawn();
			if ( !ValidConfiguration() ) { Finish( false, "Invalid configuration" ); return; }
			SetPhase( Phase.Waiting, $"Connect {MinimumClients} client(s)" );
		}

		if ( _phase == Phase.Waiting )
		{
			var peers = Peers();
			if ( peers.Length < MinimumClients )
			{
				if ( _phaseTime > TimeoutSeconds ) Finish( false, "Client connection timeout" );
				return;
			}
			foreach ( var peer in peers ) _participants.Add( peer.Id );
			SetPhase( Phase.Spawning, "Building subjects in bounded batches" );
		}

		if ( _phase == Phase.Spawning )
		{
			int target = Scenario == Workload.Empty ? 0 : Population;
			for ( int i = 0; i < SpawnPerTick && _subjects.Count < target; i++ )
				_subjects.Add( Spawn( _subjects.Count, 0 ) );
			if ( _subjects.Count == target ) SetPhase( Phase.Warmup, "Initial replication and JIT warmup" );
			else if ( _phaseTime > TimeoutSeconds ) Finish( false, "Population creation timeout" );
			return;
		}

		if ( _phase == Phase.Warmup || _phase == Phase.Running )
		{
			if ( _phaseTime > TimeoutSeconds ) { Finish( false, "Workload wall-clock timeout" ); return; }
			if ( _phase == Phase.Warmup && _phaseTicks >= WarmupTicks )
			{
				_mutations = _spawns = _destroys = _rpcs = 0;
				SetPhase( Phase.Running, Scenario == Workload.LateJoin
					? "Connect one additional client now" : "Fixed-tick workload active" );
			}
			StepWorkload();
			_phaseTicks++;
			if ( _phase == Phase.Running ) _completedRunTicks++;
			if ( _phase == Phase.Running && _phaseTicks >= RunTicks )
			{
				if ( Scenario == Workload.Visibility )
					foreach ( var subject in _subjects ) subject.Gate.Visible = true;
				SetPhase( Phase.Settling, "Workload frozen; waiting for final replication" );
			}
			return;
		}

		if ( _phase == Phase.Settling && _phaseTime >= SettleSeconds ) BeginCheck();
		if ( _phase == Phase.Checking && _phaseTime > TimeoutSeconds ) Finish( false, "Missing client state reports" );
	}

	private bool ValidConfiguration() => Population >= 0 && Population <= 100000
		&& PropertyGroups >= 0 && PropertyGroups <= 8 && ChildrenPerObject >= 0 && ChildrenPerObject <= 16
		&& SpawnPerTick >= 1 && SpawnPerTick <= 10000 && WarmupTicks >= 0 && RunTicks >= 1
		&& ActiveFraction >= 0 && ActiveFraction <= 1 && MinimumClients >= 0 && MinimumClients <= 15
		&& (Scenario != Workload.LateJoin || MinimumClients >= 1)
		&& LifecycleIntervalTicks >= 1 && LifecycleBurst >= 0 && LifecycleBurst <= 10000
		&& RpcsPerTick >= 0 && RpcsPerTick <= 100 && SettleSeconds >= 1 && TimeoutSeconds > SettleSeconds;

	private Connection[] Peers() => Connection.All.Where( c => c.IsActive && c.Id != Connection.Local.Id ).ToArray();
	private static Vector3 Position( int index ) => new( index % 100 * 48, index / 100 * 48, 48 );

	private Subject Spawn( int index, int generation )
	{
		var go = new GameObject( Scene, true, $"Load Subject {index}:{generation}" );
		go.WorldPosition = Position( index );
		if ( LocalOnly ) go.NetworkMode = NetworkMode.Never;
		int groups = Scenario == Workload.IdleBare ? 0 : PropertyGroups;
		var subject = new Subject { Object = go, Payloads = new Payload[groups], Generation = generation };
		for ( int i = 0; i < groups; i++ ) subject.Payloads[i] = go.Components.Create<Payload>();
		if ( Scenario == Workload.Visibility )
		{
			subject.Gate = go.Components.Create<VisibilityGate>();
			go.Network.AlwaysTransmit = false;
		}
		if ( RenderSubjects )
		{
			var renderer = go.Components.Create<ModelRenderer>();
			renderer.Model = Model.Load( "models/dev/box.vmdl" );
			renderer.Tint = new Color( 0.2f, 0.7f, 1 );
		}
		for ( int i = 0; i < ChildrenPerObject; i++ )
		{
			var child = new GameObject( go, true, $"Load Child {i}" );
			child.LocalPosition = new Vector3( 0, 0, (i + 1) * 16 );
		}
		if ( !LocalOnly ) go.NetworkSpawn();
		return subject;
	}

	private void StepWorkload()
	{
		int active = Scenario is Workload.SparseChanges or Workload.Mixed
			? (int)(_subjects.Count * ActiveFraction) : _subjects.Count;
		for ( int i = 0; i < active && (Moves || Changes); i++ )
		{
			var subject = _subjects[i];
			if ( Moves ) subject.Object.WorldPosition = Position( i ) + new Vector3( 0, 0, _scheduleTick % 100 );
			if ( Changes ) foreach ( var payload in subject.Payloads ) payload.Mutate( _scheduleTick, i );
			if ( _phase == Phase.Running && (Moves || subject.Payloads.Length > 0) ) _mutations++;
		}
		if ( Scenario == Workload.Visibility && _phase == Phase.Running )
		{
			if ( _phaseTicks == RunTicks / 2 )
				foreach ( var subject in _subjects ) subject.Gate.Visible = true;
		}
		if ( Churns && _subjects.Count > 0 && (_phaseTicks + 1) % LifecycleIntervalTicks == 0 )
		{
			for ( int n = 0; n < LifecycleBurst; n++ )
			{
				int i = _lifecycleCursor++ % _subjects.Count;
				int generation = _subjects[i].Generation + 1;
				_subjects[i].Object.Destroy();
				_subjects[i] = Spawn( i, generation );
				if ( _phase == Phase.Running ) { _spawns++; _destroys++; }
			}
		}
		for ( int i = 0; i < RpcsPerTick; i++ )
		{
			WorkloadRpc( _scheduleTick, i );
			if ( _phase == Phase.Running ) _rpcs++;
		}
		_scheduleTick++;
	}

	[Rpc.Broadcast]
	private void WorkloadRpc( int tick, int sequence )
	{
		// Deliberately no rendering, logging or state mutation in the receiver.
	}

	private void BeginCheck()
	{
		var peers = Peers();
		if ( _participants.Any( id => !peers.Any( c => c.Id == id ) ) || peers.Length < MinimumClients )
		{
			Finish( false, "An initial participant disconnected" );
			return;
		}
		if ( Scenario == Workload.LateJoin && !peers.Any( c => !_participants.Contains( c.Id ) ) )
		{
			Finish( false, "No additional client joined" );
			return;
		}
		var expected = LocalOnly ? Array.Empty<GameObject>() : _subjects.Select( s => s.Object ).ToArray();
		_expectedCount = expected.Length;
		_expectedDigest = Digest( expected );
		foreach ( var peer in peers ) _pendingReports.Add( peer.Id );
		SetPhase( Phase.Checking, "Comparing settled subject state on every client" );
		if ( peers.Length == 0 ) { Finish( true, "Host-only workload; replication unverified" ); return; }
		_replicationChecked = true;
		RequestReport();
	}

	[Rpc.Broadcast]
	private void RequestReport()
	{
		if ( Networking.IsHost || Rpc.CallerId != Connection.Host.Id ) return;
		var subjects = Scene.GetAllObjects( false ).Where( go => go.Name.StartsWith( "Load Subject " ) ).ToArray();
		Report( subjects.Length, Digest( subjects ) );
	}

	[Rpc.Host]
	private void Report( int count, ulong digest )
	{
		if ( _phase != Phase.Checking || !_pendingReports.Remove( Rpc.CallerId ) ) return;
		if ( count != _expectedCount || digest != _expectedDigest )
		{
			Finish( false, $"Client {Rpc.CallerId}: count {count}/{_expectedCount}, digest {digest}/{_expectedDigest}" );
			return;
		}
		if ( _pendingReports.Count == 0 ) Finish( true, "All client subject counts and final state matched" );
	}

	private static ulong Digest( GameObject[] subjects )
	{
		ulong hash = 14695981039346656037UL;
		void Add( string text )
		{
			foreach ( var character in text ) hash = unchecked((hash ^ character) * 1099511628211UL);
			hash = unchecked((hash ^ 255UL) * 1099511628211UL);
		}
		void AddVector( Vector3 v )
		{
			Add( ((int)MathF.Round( v.x * 10 )).ToString() );
			Add( ((int)MathF.Round( v.y * 10 )).ToString() );
			Add( ((int)MathF.Round( v.z * 10 )).ToString() );
		}
		foreach ( var go in subjects.OrderBy( go => go.Name, StringComparer.Ordinal ) )
		{
			Add( go.Id.ToString() );
			Add( go.Name );
			Add( go.Enabled.ToString() );
			AddVector( go.WorldPosition );
			foreach ( var payload in go.Components.GetAll<Payload>().OrderBy( c => c.Id ) )
			{
				Add( payload.Id.ToString() );
				Add( payload.Value.ToString() );
				Add( payload.Health.ToString() );
				AddVector( payload.Target );
				Add( payload.Flag.ToString() );
			}
			foreach ( var child in go.Children.OrderBy( c => c.Name, StringComparer.Ordinal ) )
			{
				Add( child.Id.ToString() );
				Add( child.Name );
				AddVector( child.LocalPosition );
			}
		}
		return hash;
	}

	private void SetPhase( Phase phase, string detail )
	{
		_phase = phase;
		_phaseTicks = 0;
		_phaseTime = 0;
		Status = $"{Scenario} / {phase}";
		Detail = detail;
		Log.Info( $"NETWORK LOAD {Status}: {detail}" );
	}

	private void Finish( bool success, string detail )
	{
		_phase = Phase.Finished;
		Status = success ? (_replicationChecked ? "PASS" : "COMPLETE (host only)") : "FAIL";
		Detail = $"{detail}\nObjects={_subjects.Count}; run ticks={_completedRunTicks}/{RunTicks}; object updates={_mutations}; spawns={_spawns}; destroys={_destroys}; RPCs={_rpcs}";
		Log.Info( $"NETWORK LOAD {Scenario} {Status}: {Detail}" );
	}
}
