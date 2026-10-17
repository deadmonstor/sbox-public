using Sandbox;
using System.Linq;

namespace NetworkSnapshotRepro;

public sealed class HotloadRepro : Component, IReproStatus
{
	private enum Phase { WaitingForClient, Warming, Settling, Ready, Triggered }

	private HotloadValue _actor;
	private Phase _phase;
	private float _elapsed;
	private bool _actorCreated;
	private int _warmupChanges;

	public string Title => "01 / Host-only scene hotload: snapshot ID reset";
	public string Instructions => "Join at least one client before warmup starts. Keep that client connected for the five-second warmup. "
		+ "Wait until the client actually shows Value=239 and the host is ready, then use Editor > Repros > Network snapshots > Hotload host scene ON THE HOST. "
		+ "Compare each peer's observed Value with the expected value received by RPC. Do not edit source: normal code hotloads affect all peers. Restart Play to repeat.";

	public bool HasClient => Connection.All.Any( c => c.IsActive && c.Id != Connection.Local.Id && c.Address != "empty" );
	public bool Ready => Networking.IsActive && Networking.IsHost && HasClient && _phase == Phase.Ready && _actor.IsValid();

	public string Status
	{
		get
		{
			var peer = Networking.IsHost ? "HOST" : "CLIENT";
			if ( !Networking.IsActive ) return $"{peer}: waiting for networking.";
			if ( !_actor.IsValid() ) return $"{peer}: waiting for the network actor.";

			var expected = _actor.ExpectedReceived ? _actor.ExpectedValue.ToString() : "not received";
			var observation = $"{peer}: observed Value={_actor.Value}; RPC expected={expected}";
			if ( !Networking.IsHost )
				return observation + "\nBefore hotload, watch the changing warmup values settle at 239. After hotload, compare Value against RPC expected; no automatic pass/fail is inferred.";

			var phase = _phase switch
			{
				Phase.WaitingForClient => "Waiting for a client before starting warmup.",
				Phase.Warming => $"Warming: {_elapsed:0.0}/5 seconds; {_warmupChanges} Value changes.",
				Phase.Settling => "Warmup finished; allowing two seconds for Value=239 to replicate.",
				Phase.Ready => HasClient ? "READY: confirm the client shows Value=239, then use the host editor menu." : "Client disconnected; keep the warmup client connected before using the menu.",
				_ => "Host-only Scene.OnHotload invoked; Value was assigned 999 once. Observe the client without further mutations."
			};
			return observation + "\n" + phase;
		}
	}

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsActive ) return;

		if ( !_actor.IsValid() )
		{
			_actor = Scene.GetAllComponents<HotloadValue>().FirstOrDefault();
			if ( !_actor.IsValid() && Networking.IsHost && !_actorCreated )
			{
				var actorObject = new GameObject( Scene, true, "Hotload snapshot value" );
				_actor = actorObject.Components.Create<HotloadValue>();
				actorObject.NetworkSpawn();
				_actorCreated = true;
			}
		}

		if ( !Networking.IsHost || !_actor.IsValid() ) return;

		if ( _phase == Phase.WaitingForClient )
		{
			if ( !HasClient ) return;
			_phase = Phase.Warming;
			_elapsed = 0;
		}

		if ( _phase == Phase.Warming )
		{
			_elapsed += Time.Delta;
			_actor.Value = ++_warmupChanges;
			if ( _elapsed < 5 ) return;
			_actor.Value = 239;
			_phase = Phase.Settling;
			_elapsed = 0;
			return;
		}

		if ( _phase == Phase.Settling )
		{
			_elapsed += Time.Delta;
			if ( _elapsed >= 2 ) _phase = Phase.Ready;
		}
	}

	public void SetValueAfterHostHotload()
	{
		if ( !Ready ) return;
		_phase = Phase.Triggered;
		_actor.Value = 999;
		_actor.AnnounceExpectedValue( 999 );
	}
}

public sealed class HotloadValue : Component
{
	[Sync] public int Value { get; set; }

	public int ExpectedValue { get; private set; }
	public bool ExpectedReceived { get; private set; }

	[Rpc.Broadcast]
	public void AnnounceExpectedValue( int value )
	{
		ExpectedValue = value;
		ExpectedReceived = true;
	}
}
