using System.Globalization;

namespace BotSwarm;

internal sealed class Options
{
	public string Host = "127.0.0.1";
	public int Port = 55333;
	public int Bots = 100;
	public float JoinRate = 20;
	public float Duration;
	public float TickRate = 30;
	public string Password;
	public string NamePrefix = "Bot";
	public ulong SteamIdBase = 90071992547400000;

	public bool Input = true;
	public int InputActions = 16;

	public bool Move = true;
	public float MoveSpeed = 200;
	public float MoveRadius = 1500;

	public float ChatInterval = 20;

	public float ChurnMin;
	public float ChurnMax;

	public float HandshakeTimeout = 60;
	public bool Verbose;

	public const string Usage = """
		botswarm - simulate lots of players joining an s&box server

		The server must listen on loopback TCP. Start it with:
		  sbox-server.exe +game <ident> [map] +net_allow_local 1 [+net_local_port <port>]

		Options:
		  --host <ip>              Server address (default 127.0.0.1)
		  --port <port>            net_local_port of the server (default 55333)
		  --bots <n>               Concurrent bots to keep connected (default 100)
		  --rate <n>               Joins started per second (default 20)
		  --duration <sec>         Stop after this long, 0 = until Ctrl+C (default 0)
		  --tick-rate <hz>         ClientTick / movement send rate (default 30)
		  --password <pw>          sv_password, if the server has one
		  --name <prefix>          Bot name prefix (default Bot)
		  --steamid-base <id>      First fake SteamId (default 90071992547400000)
		  --no-input               Don't send held input actions
		  --actions <n>            Randomise the first n input action bits (default 16)
		  --no-move                Don't move objects the bot owns
		  --speed <u/s>            Wander speed (default 200)
		  --radius <u>             Wander radius around the spawn point (default 1500)
		  --chat <sec>             Average seconds between chat messages per bot, 0 = off (default 20)
		  --churn <min>-<max>      Each bot leaves after a random lifetime in seconds and is replaced
		  --handshake-timeout <s>  Give up on a join after this long (default 60)
		  --verbose                Log per-bot events
		""";

	public static Options Parse( string[] args )
	{
		var o = new Options();

		for ( var i = 0; i < args.Length; i++ )
		{
			var arg = args[i];
			string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException( $"{arg} needs a value" );
			float NextFloat() => float.Parse( Next(), CultureInfo.InvariantCulture );
			int NextInt() => int.Parse( Next(), CultureInfo.InvariantCulture );

			switch ( arg )
			{
				case "--host": o.Host = Next(); break;
				case "--port": o.Port = NextInt(); break;
				case "--bots": o.Bots = NextInt(); break;
				case "--rate": o.JoinRate = NextFloat(); break;
				case "--duration": o.Duration = NextFloat(); break;
				case "--tick-rate": o.TickRate = NextFloat(); break;
				case "--password": o.Password = Next(); break;
				case "--name": o.NamePrefix = Next(); break;
				case "--steamid-base": o.SteamIdBase = ulong.Parse( Next(), CultureInfo.InvariantCulture ); break;
				case "--no-input": o.Input = false; break;
				case "--actions": o.InputActions = Math.Clamp( NextInt(), 1, 64 ); break;
				case "--no-move": o.Move = false; break;
				case "--speed": o.MoveSpeed = NextFloat(); break;
				case "--radius": o.MoveRadius = NextFloat(); break;
				case "--chat": o.ChatInterval = NextFloat(); break;
				case "--churn":
					{
						var parts = Next().Split( '-' );
						o.ChurnMin = float.Parse( parts[0], CultureInfo.InvariantCulture );
						o.ChurnMax = parts.Length > 1 ? float.Parse( parts[1], CultureInfo.InvariantCulture ) : o.ChurnMin;
						if ( o.ChurnMax < o.ChurnMin ) throw new ArgumentException( "--churn max is below min" );
						break;
					}
				case "--handshake-timeout": o.HandshakeTimeout = NextFloat(); break;
				case "--verbose": o.Verbose = true; break;
				default: throw new ArgumentException( $"Unknown option {arg}" );
			}
		}

		if ( o.Bots < 1 ) throw new ArgumentException( "--bots must be at least 1" );
		if ( o.JoinRate <= 0 ) throw new ArgumentException( "--rate must be above 0" );
		if ( o.TickRate <= 0 ) throw new ArgumentException( "--tick-rate must be above 0" );

		return o;
	}
}
