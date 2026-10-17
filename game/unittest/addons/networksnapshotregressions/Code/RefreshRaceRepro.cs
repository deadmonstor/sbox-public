using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NetworkSnapshotRepro;

public sealed class RefreshRaceRepro : Component, IReproStatus
{
	public string Title => "02 / Refresh / takeover race";
	public string Instructions => "Host + two clients. On EVERY instance run net_fakelag 100 before starting. " +
		"Wait for A and B readiness, then press Space (Jump) on the HOST once. " +
		"A refreshes twice; B takes ownership 50 ms later. Reports are sampled 1 second after takeover. " +
		"Expected: all reported owner IDs agree. Affected: A reports A while B reports B. " +
		"Restart the scene to repeat; run net_fakelag 0 on every instance afterward.";

	private readonly List<Guid> _clientOrder = new();
	private bool _spawnAttempted;
	private string _status = "Waiting for host and two clients.";

	public string Status
	{
		get
		{
			var actor = Scene.GetAllComponents<RefreshRaceActor>().FirstOrDefault();
			return actor is null ? _status : actor.Describe();
		}
	}

	protected override void OnUpdate()
	{
		if ( !Networking.IsActive || !Networking.IsHost ) return;

		if ( !_spawnAttempted )
		{
			var connections = Connection.All;
			foreach ( var connection in connections.Where( x => !x.IsHost ) )
			{
				if ( !_clientOrder.Contains( connection.Id ) ) _clientOrder.Add( connection.Id );
			}
			_clientOrder.RemoveAll( id => !connections.Any( x => x.Id == id ) );
			_status = $"Waiting for two clients: {_clientOrder.Count}/2. Set net_fakelag 100 on every instance.";
			if ( _clientOrder.Count < 2 ) return;

			_spawnAttempted = true;
			var go = new GameObject( Scene, true, "Refresh race actor" );
			var actor = go.Components.Create<RefreshRaceActor>();
			actor.OwnerA = _clientOrder[0];
			actor.ContenderB = _clientOrder[1];
			if ( !go.Network.SetOwnerTransfer( OwnerTransfer.Takeover ) || !go.NetworkSpawn( Connection.Find( actor.OwnerA ) ) )
			{
				_status = "Actor spawn failed. Restart the scene; no race was scheduled.";
				go.Destroy();
				return;
			}
		}

		if ( Input.Pressed( "Jump" ) )
		{
			Scene.GetAllComponents<RefreshRaceActor>().FirstOrDefault()?.TrySchedule();
		}
	}
}

public sealed class RefreshRaceActor : Component
{
	[Property] public Guid OwnerA { get; set; }
	[Property] public Guid ContenderB { get; set; }

	private readonly Dictionary<Guid, Guid> _ready = new();
	private readonly Dictionary<Guid, Guid> _reports = new();
	private readonly Dictionary<Guid, string> _actions = new();
	private bool _announced;
	private bool _scheduled;
	private bool _acted;
	private bool _reported;
	private double _target;
	private Guid _scheduledA;
	private Guid _scheduledB;
	private string _action = "No local race action yet.";
	private string _startStatus = "Waiting for readiness RPCs from A and B.";

	public void TrySchedule()
	{
		if ( !Networking.IsHost || _scheduled ) return;
		if ( !_ready.TryGetValue( OwnerA, out var ownerOnA ) || ownerOnA != OwnerA ||
			!_ready.TryGetValue( ContenderB, out var ownerOnB ) || ownerOnB != OwnerA ||
			GameObject.Network.OwnerId != OwnerA )
		{
			_startStatus = "Not ready: host, A and B must initially observe A as owner. Wait, then press Space.";
			return;
		}
		var ownerConnection = Connection.Find( OwnerA );
		if ( ownerConnection is null || Connection.Find( ContenderB ) is null )
		{
			_startStatus = "A or B disconnected. Restart the scene; roles are not reassigned.";
			return;
		}
		if ( !ownerConnection.CanRefreshObjects )
		{
			_startStatus = "A lacks refresh permission. Enable ClientsCanRefreshObjects in project networking settings and restart.";
			return;
		}
		ScheduleRace( Time.NowDouble + 2.0, OwnerA, ContenderB );
	}

	[Rpc.Broadcast]
	private void ScheduleRace( double target, Guid ownerA, Guid contenderB )
	{
		if ( Rpc.CallerId != Connection.Host.Id || _scheduled ) return;
		_scheduled = true;
		_target = target;
		_scheduledA = ownerA;
		_scheduledB = contenderB;
		_startStatus = "One race scheduled. No automatic retry or reset.";
	}

	protected override void OnUpdate()
	{
		if ( !GameObject.Network.Active ) return;
		if ( !_announced && OwnerA != Guid.Empty && ContenderB != Guid.Empty )
		{
			_announced = true;
			AnnounceReady( GameObject.Network.OwnerId );
		}
		if ( !_scheduled ) return;

		var now = Time.NowDouble;
		var localId = Connection.Local.Id;
		if ( !_acted && localId == _scheduledA && now >= _target )
		{
			_acted = true;
			if ( !GameObject.Network.IsOwner || !Connection.Local.CanRefreshObjects )
			{
				_action = $"A could not refresh at {now:F3}: owner={GameObject.Network.OwnerId}, refresh permission={Connection.Local.CanRefreshObjects}.";
			}
			else
			{
				GameObject.Network.Refresh();
				GameObject.Network.Refresh();
				_action = $"A called Refresh twice at {now:F3} (target {_target:F3}).";
			}
		}
		if ( !_acted && localId == _scheduledB && now >= _target + 0.05 )
		{
			_acted = true;
			var previousOwner = GameObject.Network.OwnerId;
			var took = GameObject.Network.TakeOwnership();
			_action = $"B called TakeOwnership at {now:F3} (target {_target + 0.05:F3}); returned {took}; previous owner={previousOwner}.";
		}
		if ( !_reported && now >= _target + 1.05 )
		{
			_reported = true;
			ReportOwner( GameObject.Network.OwnerId, _action );
		}
	}

	[Rpc.Broadcast]
	private void AnnounceReady( Guid observedOwner )
	{
		_ready[Rpc.CallerId] = observedOwner;
	}

	[Rpc.Broadcast]
	private void ReportOwner( Guid observedOwner, string action )
	{
		_reports[Rpc.CallerId] = observedOwner;
		_actions[Rpc.CallerId] = action;
	}

	public string Describe()
	{
		var localId = Connection.Local.Id;
		var role = localId == OwnerA ? "A (initial owner)" : localId == ContenderB ? "B (contender)" : Connection.Local.IsHost ? "HOST" : "observer";
		var text = $"Local role: {role}; ID={localId}\nA={OwnerA}\nB={ContenderB}\n" +
			$"Local live OwnerId={GameObject.Network.OwnerId}\n{_startStatus}\n" +
			$"Ready: A={_ready.ContainsKey( OwnerA )}, B={_ready.ContainsKey( ContenderB )}\n";
		if ( _scheduled )
		{
			text += $"Scene time={Time.NowDouble:F3}; A target={_target:F3}; B target={_target + 0.05:F3}; report target={_target + 1.05:F3}\n{_action}\n";
		}
		foreach ( var peer in Connection.All.OrderBy( x => x.Id ) )
		{
			var peerRole = peer.Id == OwnerA ? "A" : peer.Id == ContenderB ? "B" : peer.IsHost ? "HOST" : "observer";
			text += _reports.TryGetValue( peer.Id, out var owner )
				? $"{peerRole} ({peer.Id}) reported owner={owner}\n{_actions[peer.Id]}\n"
				: $"{peerRole} ({peer.Id}): awaiting owner report\n";
		}
		if ( _reports.Count > 0 )
		{
			text += _reports.Values.Distinct().Count() > 1
				? "DISAGREEMENT in received peer reports."
				: "Received reports agree so far; missing reports are not a pass.";
		}
		return text;
	}
}
