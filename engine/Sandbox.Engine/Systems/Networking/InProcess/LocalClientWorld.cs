using Sandbox.Audio;
using Sandbox.Engine;
using Sandbox.Network;
using Sandbox.Utility;

namespace Sandbox;

internal sealed class LocalClientWorld : IDisposable
{
	const ulong FakeSteamIdOffset = 1000;

	static readonly List<LocalClientWorld> _all = new();

	public static IReadOnlyList<LocalClientWorld> All => _all;

	public int Number { get; }
	public string PlayerName { get; }
	public GlobalContext Context { get; }
	public NetworkSystem System => Context.Network.System;
	public Scene Scene => Context.ActiveScene;
	public Mixer Mixer { get; private set; }

	public bool IsConnected => !_disposed && Context.Network.LocalConnection?.State == Connection.ChannelState.Connected;
	public bool IsDefunct => _disposed || System is null || System.IsDisconnected;

	readonly InProcessSocket _socket;
	readonly InProcessConnection _hostSide;
	readonly Input.Context _perFrameInput;
	bool _disposed;
	bool _inside;

	public static LocalClientWorld Create( string name = null )
	{
		ThreadSafe.AssertIsMainThread();

		var host = Networking.System;
		if ( host is null || !host.IsHost )
			throw new InvalidOperationException( "Can't create a local client world without an active host" );

		var socket = host.Sockets.OfType<InProcessSocket>().FirstOrDefault();
		if ( socket is null )
		{
			socket = new InProcessSocket();
			host.AddSocket( socket );
		}

		var world = new LocalClientWorld( host, socket, name );
		_all.Add( world );

		socket.Accept( world._hostSide );

		return world;
	}

	LocalClientWorld( NetworkSystem host, InProcessSocket socket, string name )
	{
		_socket = socket;

		var number = 1;
		while ( _all.Any( x => x.Number == number ) )
			number++;

		Number = number;
		PlayerName = string.IsNullOrWhiteSpace( name ) ? $"Client {number}" : name;

		var hostContext = GlobalContext.Current;

		Context = new GlobalContext
		{
			IsSecondaryWorld = true,
			LocalAssembly = hostContext.LocalAssembly,
			TypeLibrary = hostContext.TypeLibrary,
			NodeLibrary = hostContext.NodeLibrary,
			ResourceSystem = hostContext.ResourceSystem,
			FileMount = hostContext.FileMount,
			FileData = hostContext.FileData,
			FileOrg = hostContext.FileOrg,
			JsonSerializerOptions = hostContext.JsonSerializerOptions,
			Language = hostContext.Language,
			Cookies = hostContext.Cookies,
		};

		using ( new GlobalContext.GlobalContextScope( Context ) )
		{
			Context.TaskSource = new TaskSource( 1 );

			var uiSystem = new UISystem();
			var input = new InputContext
			{
				Name = PlayerName,
				TargetUISystem = uiSystem
			};

			input.OnGameMouseWheel += Input.AddMouseWheel;
			input.OnMouseMotion += Input.AddMouseMovement;
			input.OnGameButton += Input.OnButton;

			Context.UISystem = uiSystem;
			Context.InputContext = input;

			_perFrameInput = Input.Context.Create( PlayerName );

			if ( Mixer.Master is not null )
			{
				Mixer = Mixer.Master.AddChild();
				Mixer.Name = PlayerName;
				Context.AudioRoot = Mixer;
			}

			SteamId steamId = Utility.Steam.BaseFakeSteamId + FakeSteamIdOffset + (ulong)number;
			(_hostSide, var clientSide) = InProcessConnection.CreatePair( PlayerName, steamId );

			var system = new NetworkSystem( $"local-client-{number}", Context.TypeLibrary )
			{
				Environment = new InProcessClientEnvironment( host )
			};

			Networking.System = system;
			system.Connect( clientSide );
		}
	}

	public IDisposable Push()
	{
		ThreadSafe.AssertIsMainThread();

		if ( _inside )
			throw new InvalidOperationException( "LocalClientWorld.Push is not re-entrant" );

		_inside = true;

		var screenSize = Screen.Size;
		var timeNow = Time.NowDouble;
		var timeDelta = (double)Time.Delta;
		var scope = new GlobalContext.GlobalContextScope( Context );

		return DisposeAction.Create( () =>
		{
			scope.Dispose();
			Time.Update( timeNow, timeDelta );
			Screen.Size = screenSize;
			_inside = false;
		} );
	}

	public static void TickAll()
	{
		foreach ( var world in _all.ToArray() )
		{
			world.Tick();
		}
	}

	public void Tick()
	{
		if ( _disposed )
			return;

		ThreadSafe.AssertIsMainThread();

		using var _ = Push();

		try
		{
			TickInternal();
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"{PlayerName} tick error: {e.Message}" );
		}
	}

	void TickInternal()
	{
		var system = System;
		var scene = Game.ActiveScene;

		if ( !scene.IsValid() )
		{
			system?.Tick();
			system?.SendTableUpdates();
			return;
		}

		using var sceneScope = scene.Push();

		scene.UpdateTime( RealTime.Delta );
		scene.SyncServerTime();
		Time.Update( scene.TimeNow, scene.TimeDelta );

		_perFrameInput.Flip();
		using var inputScope = _perFrameInput.Push();
		Input.ProcessContext();

		system?.Tick();

		if ( Game.ActiveScene == scene && scene.IsValid() && system is not null && !system.IsConnecting && !scene.IsLoading )
		{
			scene.GameTick( 0 );
		}

		system?.SendTableUpdates();

		Context.UISystem?.Simulate( InputRouter.FocusedWorld == Context );
		scene.ProcessDeletes();
	}

	public void Dispose()
	{
		if ( _disposed )
			return;

		ThreadSafe.AssertIsMainThread();

		_disposed = true;
		_all.Remove( this );

		if ( InputRouter.FocusedWorld == Context )
			InputRouter.FocusedWorld = null;

		using ( Push() )
		{
			try
			{
				if ( System is { IsDisconnected: false } system )
					system.Disconnect();

				Game.ActiveScene?.Destroy();
				Game.ActiveScene = null;

				Context.UISystem?.Clear();
				Context.CancellationTokenSource?.Cancel();
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"{PlayerName} shutdown error: {e.Message}" );
			}
		}

		try
		{
			_socket.Disconnect( _hostSide );
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"{PlayerName} host disconnect error: {e.Message}" );
		}

		if ( Mixer is not null )
		{
			SoundHandle.StopAll( 0f, Mixer );
			Mixer.Destroy();
			Mixer = null;
		}
	}

	public static void DisposeAll()
	{
		foreach ( var world in _all.ToArray() )
		{
			world.Dispose();
		}
	}
}
