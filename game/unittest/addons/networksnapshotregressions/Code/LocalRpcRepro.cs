using Sandbox;
using System;

namespace NetworkSnapshotRepro;

public sealed class LocalRpcRepro : Component, IReproStatus
{
	private int _attempts;
	private int _controlCalls;
	private int _callbackCalls;
	private string _result = "Waiting for a host-local call.";

	public string Title => "03 / Self-targeted RPC serialization";
	public string Instructions => "No clients needed. Wait for the lobby, then press Space on the host.\nBoth RPCs target this host; neither needs to send an argument over the network.";
	public string Status => $"Networking active: {Networking.IsActive}\nAttempts: {_attempts}\nInteger control bodies: {_controlCalls}\nDelegate callback bodies: {_callbackCalls}\nResult: {_result}\nExpected: both body counts equal attempts, with no serialization exception.";

	protected override void OnUpdate()
	{
		if ( Networking.IsActive && Networking.IsHost && Input.Pressed( "Jump" ) )
			Run();
	}

	[Button]
	public void Run()
	{
		if ( !Networking.IsActive || !Networking.IsHost )
		{
			_result = "Run this on the active host.";
			return;
		}

		_attempts++;
		Control( 42 );

		try
		{
			ApplyLocally( () => _callbackCalls++ );
			_result = "Local callback completed.";
		}
		catch ( Exception e )
		{
			_result = $"{e.GetType().Name}: {e.Message}";
		}
	}

	[Rpc.Host]
	private void Control( int value )
	{
		_controlCalls += value == 42 ? 1 : 0;
	}

	[Rpc.Host]
	private void ApplyLocally( Action callback )
	{
		callback();
	}
}
