using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Numerics;

namespace BotSwarm;

internal enum BotState
{
	Connecting,
	Handshaking,
	Active,
	Closed
}

/// <summary>
/// One fake player: a loopback TCP connection that runs the engine's join handshake, then
/// sends input, moves whatever objects it owns and chats, like a real client would.
/// </summary>
internal sealed class Bot
{
	/// <summary>
	/// A networked object the bot has heard about. Only the bot's own ones are moved.
	/// </summary>
	sealed class NetObject
	{
		public Guid Id;
		public Guid Parent;
		public Guid Owner;
		public ushort Version;
		public uint SnapshotId;
		public Vector3 Position;
		public Vector3 Home;
		public Vector3 Target;
		public float Yaw;
	}

	static readonly string[] ChatLines =
	[
		"hello", "anyone here?", "lag?", "gg", "where is everyone", "brb", "this server is busy",
		"how do i spawn stuff", "nice", "lol", "test test", "who built that", "o/", "follow me"
	];

	public int Slot { get; }
	public BotState State { get; private set; } = BotState.Connecting;
	public string CloseReason { get; private set; }

	readonly Options options;
	readonly Stats stats;
	readonly string name;
	readonly ulong steamId;
	readonly Random random;

	Socket socket;

	// Per-bot send queue, drained by SendLoop
	const int MaxQueuedSends = 256;
	readonly ConcurrentQueue<(byte[] Buffer, int Length)> outgoing = new();
	readonly SemaphoreSlim outgoingQueued = new( 0 );
	int queuedSends;
	Task sending;

	readonly PacketWriter receiveWriter = new();
	readonly PacketWriter tickWriter = new( 1024 );
	readonly Packet packet = new();

	Guid handshakeId;
	Guid connectionId;
	Guid hostId;
	long joinStarted;
	long lastHeartbeat;
	CancellationTokenSource lifetime;

	// Touched by the receive loop and the tick thread
	readonly object stateLock = new();
	readonly Dictionary<Guid, NetObject> objects = new();
	readonly List<ushort> pendingClusterAcks = new();

	// Tick thread only
	uint commandNumber;
	ulong actions;
	double nextActionChange;
	double nextChat;
	ushort clusterId;
	Vector3 origin;

	/// <summary>
	/// Whether the last tick moved an owned object.
	/// </summary>
	public bool IsMoving { get; private set; }

	/// <summary>
	/// Whether the bot finished the handshake at some point.
	/// </summary>
	public bool Joined { get; private set; }

	public Bot( int slot, Options options, Stats stats )
	{
		Slot = slot;
		this.options = options;
		this.stats = stats;
		name = $"{options.NamePrefix}{slot:000}";
		steamId = options.SteamIdBase + (ulong)slot;
		random = new Random( HashCode.Combine( slot, Environment.TickCount ) );
	}

	/// <summary>
	/// Connect, join and play until disconnected, kicked, cancelled or churned out.
	/// </summary>
	public async Task RunAsync( CancellationToken cancel )
	{
		lifetime = CancellationTokenSource.CreateLinkedTokenSource( cancel );
		lifetime.CancelAfter( TimeSpan.FromSeconds( options.HandshakeTimeout ) );

		Interlocked.Increment( ref stats.Connecting );

		try
		{
			socket = new Socket( SocketType.Stream, ProtocolType.Tcp )
			{
				NoDelay = true,
				ReceiveBufferSize = 1024 * 1024,
				SendBufferSize = 256 * 1024
			};

			await socket.ConnectAsync( options.Host, options.Port, lifetime.Token );

			joinStarted = Stopwatch.GetTimestamp();
			SetState( BotState.Handshaking );

			sending = SendLoop( lifetime.Token );
			await ReceiveLoop( lifetime.Token );
			Close( CloseReason ?? "Server closed the connection" );
		}
		catch ( OperationCanceledException ) when ( cancel.IsCancellationRequested )
		{
			Close( "Stopped" );
		}
		catch ( OperationCanceledException )
		{
			// The lifetime ran out: either the handshake took too long or we're churning
			Close( State == BotState.Active ? "Churn" : $"Handshake timed out in state {State}" );
		}
		catch ( Exception e ) when ( e is SocketException or IOException or InvalidDataException or ObjectDisposedException )
		{
			Close( e.Message );
		}
		finally
		{
			if ( State != BotState.Closed ) Close( "Finished" );
			try { lifetime.Cancel(); } catch ( ObjectDisposedException ) { }
			if ( sending is not null ) await sending;
			socket?.Dispose();

			while ( outgoing.TryDequeue( out var item ) )
				ArrayPool<byte>.Shared.Return( item.Buffer );
			lifetime.Dispose();
		}
	}

	void SetState( BotState state )
	{
		lock ( stateLock )
		{
			if ( State == state || State == BotState.Closed ) return;

			Interlocked.Decrement( ref Gauge( State ) );
			State = state;
			if ( state != BotState.Closed ) Interlocked.Increment( ref Gauge( state ) );
		}
	}

	ref long Gauge( BotState state )
	{
		switch ( state )
		{
			case BotState.Connecting: return ref stats.Connecting;
			case BotState.Handshaking: return ref stats.Handshaking;
			default: return ref stats.Active;
		}
	}

	/// <summary>
	/// Close the connection, remembering why. Safe to call from any thread, more than once.
	/// </summary>
	public void Close( string reason )
	{
		lock ( stateLock )
		{
			if ( State == BotState.Closed ) return;
			CloseReason ??= reason;
		}

		IsMoving = false;

		SetState( BotState.Closed );

		try { socket?.Shutdown( SocketShutdown.Both ); } catch ( Exception ) { }
		try { lifetime?.Cancel(); } catch ( ObjectDisposedException ) { }

		if ( options.Verbose ) Console.WriteLine( $"{name}: closed ({CloseReason})" );
	}

	async Task ReceiveLoop( CancellationToken cancel )
	{
		var header = new byte[sizeof( int )];
		var buffer = new byte[64 * 1024];

		while ( await ReadExactly( header, cancel ) )
		{
			var length = BinaryPrimitives.ReadInt32LittleEndian( header );
			if ( length <= 0 || length > 512 * 1024 * 1024 )
				throw new InvalidDataException( $"Bad frame length {length}" );

			if ( buffer.Length < length )
				buffer = new byte[Math.Max( length, buffer.Length * 2 )];

			if ( !await ReadExactly( buffer.AsMemory( 0, length ), cancel ) )
				throw new IOException( "Server closed the connection mid-frame" );

			Interlocked.Add( ref stats.BytesIn, length + sizeof( int ) );
			OnPacket( buffer.AsSpan( 0, length ) );

			if ( buffer.Length > 256 * 1024 )
				buffer = new byte[64 * 1024];

			if ( State == BotState.Closed ) return;
		}
	}

	/// <summary>
	/// Fill <paramref name="memory"/>. False if the connection closed before the first byte.
	/// </summary>
	async ValueTask<bool> ReadExactly( Memory<byte> memory, CancellationToken cancel )
	{
		var first = true;

		while ( memory.Length > 0 )
		{
			var read = await socket.ReceiveAsync( memory, cancel );
			if ( read == 0 )
			{
				if ( first ) return false;
				throw new IOException( "Server closed the connection mid-frame" );
			}

			first = false;
			memory = memory[read..];
		}

		return true;
	}

	void OnPacket( ReadOnlySpan<byte> data )
	{
		try
		{
			if ( !packet.TryDecode( data, out var message ) )
				return;

			Interlocked.Increment( ref stats.MessagesIn );
			Dispatch( message, hostId, 0 );
			packet.Trim();
		}
		catch ( UnknownPackTypeException e )
		{
			Interlocked.Increment( ref stats.UnknownMessages );
			if ( options.Verbose ) Console.WriteLine( $"{name}: skipped message with unknown type {e.Ident}" );
		}
		catch ( InvalidDataException e )
		{
			Interlocked.Increment( ref stats.DecodeErrors );
			if ( options.Verbose ) Console.WriteLine( $"{name}: bad message: {e.Message}" );
		}
	}

	void Dispatch( ReadOnlySpan<byte> message, Guid sender, int depth )
	{
		var r = new SpanReader( message );
		var type = (MessageType)r.Read<byte>();

		switch ( type )
		{
			case MessageType.HeartbeatPing:
				OnHeartbeat( r.Read<float>() );
				return;

			case MessageType.Packed:
				OnPacked( ref r );
				return;

			case MessageType.Request:
			case MessageType.Response:
				r.Read<Guid>();
				if ( (MessageType)r.Read<byte>() == MessageType.Packed )
					OnPacked( ref r );
				return;

			case MessageType.ClientTick:
				// The host sends its visibility origins to everyone once per network tick
				if ( depth == 0 ) Interlocked.Increment( ref stats.ServerTicks );
				return;

			case MessageType.DeltaSnapshotCluster:
				{
					r.Read<int>();
					var id = r.Read<ushort>();

					lock ( stateLock ) pendingClusterAcks.Add( id );
					return;
				}

			case MessageType.DeltaSnapshot:
				{
					r.Read<int>();
					var objectId = r.Read<Guid>();
					r.Read<ushort>();
					var snapshotId = r.Read<uint>();

					// [DeltaSnapshotAck][len][Guid objectId][uint snapshotId]
					var w = receiveWriter.Reset();
					w.Write( (byte)MessageType.DeltaSnapshotAck );
					w.Write( 20 );
					w.Write( objectId );
					w.Write( snapshotId );
					Send( w );
					return;
				}

			case MessageType.DeltaSnapshotClusterAck:
				{
					// [len][ushort count] { [ushort clusterId][ushort invalidCount][uint invalidSnapshotIds...] }
					r.Read<int>();
					var count = r.Read<ushort>();

					for ( var i = 0; i < count; i++ )
					{
						r.Read<ushort>();
						var invalid = r.Read<ushort>();
						r.Skip( invalid * sizeof( uint ) );

						Interlocked.Increment( ref stats.MovesAcked );
						if ( invalid > 0 ) Interlocked.Add( ref stats.MovesRejected, invalid );
					}
					return;
				}

			case MessageType.Rpc:
				OnRpc( ref r, sender );
				return;

			case MessageType.Multicast:
				{
					if ( depth > 0 ) return;

					var from = r.Read<Guid>();
					r.Read<byte>();
					var targets = r.Read<int>();
					r.Skip( Math.Max( targets, 0 ) * 16 );
					Dispatch( message[r.Position..], from, depth + 1 );
					return;
				}
		}
	}

	void OnHeartbeat( float serverRealTime )
	{
		var now = Stopwatch.GetTimestamp();
		if ( lastHeartbeat != 0 ) stats.HeartbeatGap( now - lastHeartbeat );
		lastHeartbeat = now;

		// [HeartbeatPong][float echoed server time]
		var w = receiveWriter.Reset();
		w.Write( (byte)MessageType.HeartbeatPong );
		w.Write( serverRealTime );
		Send( w );
	}

	void OnPacked( ref SpanReader r )
	{
		var start = r.Position;
		if ( BytePack.ReadHeader( ref r, out var ident ) != PackId.Runtime )
			return;

		// Big and full of game types we can't decode. We already know the handshake id.
		if ( ident == Protocol.InitialSnapshotResponse.Ident )
		{
			SendPod( Protocol.ClientReady, handshakeId );
			return;
		}

		// Game messages and engine messages we don't care about
		if ( !BytePack.TryGetSchema( ident, out _ ) )
			throw new UnknownPackTypeException( ident );

		r.Position = start;
		var value = BytePack.ReadValue( ref r );

		switch ( value )
		{
			case PackedObject o when o.Schema == Protocol.ServerInfo:
				OnServerInfo( o );
				break;

			case PackedPod p when p.Schema == Protocol.Welcome:
				SendPod( Protocol.RequestMountedVPKs, handshakeId );
				break;

			case PackedObject o when o.Schema == Protocol.MountedVPKsResponse:
				{
					// [RequestInitialSnapshot] UserData, HandshakeId
					var w = BeginPacked( receiveWriter );
					w.WriteStruct( Protocol.RequestInitialSnapshot, new Dictionary<string, Action<PacketWriter>>
					{
						["UserData"] = x => x.WriteNull(),
						["HandshakeId"] = x => x.WriteGuid( handshakeId ),
					} );
					Send( w );
					break;
				}

			case PackedPod p when p.Schema == Protocol.Activate:
				OnActivate();
				break;

			case PackedObject o when o.Schema == Protocol.KickMsg:
				{
					var reason = o["Reason"] as string ?? "no reason";
					Interlocked.Increment( ref stats.Kicks );
					stats.Reason( "kick", reason );
					Close( $"Kicked: {reason}" );
					break;
				}

			case PackedObject o when o.Schema == Protocol.ReconnectMsg:
				Close( "Server asked us to reconnect" );
				break;

			case PackedObject o when o.Schema == Protocol.ObjectCreateMsg:
				OnObjectCreate( o );
				break;

			case PackedObject o when o.Schema == Protocol.ObjectCreateBatchMsg:
				if ( o["CreateMsgs"] is List<object> list )
				{
					foreach ( var item in list.OfType<PackedObject>() )
						OnObjectCreate( item );
				}
				break;

			case PackedObject o when o.Schema == Protocol.ObjectRefreshMsg:
				OnObjectRefresh( o );
				break;

			case PackedObject o when o.Schema == Protocol.ObjectDestroyMsg:
				lock ( stateLock ) objects.Remove( (Guid)o["Guid"] );
				break;
		}
	}

	void OnServerInfo( PackedObject info )
	{
		handshakeId = (Guid)info["HandshakeId"];
		connectionId = ((PackedPod)info["Assigned"]).As<Guid>();
		hostId = ((PackedPod)info["Host"]).As<Guid>();

		if ( info["PasswordRequired"] is true && options.Password is null )
		{
			Close( "Server needs a password (--password)" );
			return;
		}

		// [UserInfo] - no auth ticket, the loopback TCP listener doesn't ask for one
		var w = BeginPacked( receiveWriter );
		w.WriteStruct( Protocol.UserInfo, new Dictionary<string, Action<PacketWriter>>
		{
			["SteamId"] = x => x.WriteSteamId( steamId ),
			["Name"] = x => x.WriteString( name ),
			["ConnectionTime"] = x => x.WriteDateTimeOffset( DateTimeOffset.UtcNow ),
			["UserData"] = x => x.WriteStringDictionary( new Dictionary<string, string>() ),
			["EngineVersion"] = x => x.WriteInt( 0 ),
			["PartyId"] = x => x.WriteSteamId( 0 ),
			["AuthTicket"] = x => x.WriteNull(),
			["HandshakeId"] = x => x.WriteGuid( handshakeId ),
			["IsVr"] = x => x.WriteBool( false ),
			["InventoryBlob"] = x => x.WriteNull(),
			["Password"] = x => x.WriteString( options.Password ),
		} );
		Send( w );
	}

	void OnActivate()
	{
		var seconds = Stopwatch.GetElapsedTime( joinStarted ).TotalSeconds;
		stats.JoinTime( seconds );
		Interlocked.Increment( ref stats.Joins );

		// Joined: the handshake timeout no longer applies, churn takes over
		lifetime.CancelAfter( options.ChurnMax > 0
			? TimeSpan.FromSeconds( options.ChurnMin + random.NextSingle() * (options.ChurnMax - options.ChurnMin) )
			: Timeout.InfiniteTimeSpan );

		Joined = true;
		SetState( BotState.Active );

		if ( options.Verbose ) Console.WriteLine( $"{name}: joined in {seconds:0.00}s as {connectionId}" );
	}

	void OnObjectCreate( PackedObject msg )
	{
		var transform = msg["Transform"] is PackTransform t ? t : default;

		lock ( stateLock )
		{
			var id = (Guid)msg["Guid"];
			if ( objects.ContainsKey( id ) ) return;

			objects[id] = new NetObject
			{
				Id = id,
				Parent = (Guid)msg["Parent"],
				Owner = (Guid)msg["Owner"],
				Version = (ushort)msg["SnapshotVersion"],
				Position = transform.Position,
				Home = transform.Position,
				Target = transform.Position
			};
		}
	}

	void OnObjectRefresh( PackedObject msg )
	{
		// The refresh carries a full DeltaSnapshot body: [Guid objectId][ushort version]...
		if ( msg["Snapshot"] is not byte[] snapshot || snapshot.Length < 18 )
			return;

		var version = BinaryPrimitives.ReadUInt16LittleEndian( snapshot.AsSpan( 16 ) );

		lock ( stateLock )
		{
			if ( objects.TryGetValue( (Guid)msg["Guid"], out var obj ) && (short)(version - obj.Version) > 0 )
				obj.Version = version;
		}
	}

	/// <summary>
	/// Only ownership changes matter to us - they bump the object's snapshot version.
	/// </summary>
	void OnRpc( ref SpanReader r, Guid sender )
	{
		if ( r.Read<byte>() != 0 ) return; // Object RPCs only

		var objectId = r.Read<Guid>();
		r.Read<Guid>(); // component
		var method = r.Read<int>();
		r.Skip( r.Read<byte>() * sizeof( int ) ); // generic args
		r.Read<byte>(); // arg count

		Guid owner;
		object versionArg;

		if ( method == Protocol.RpcAssignOwnership )
		{
			owner = (Guid)BytePack.ReadValue( ref r );
			versionArg = BytePack.ReadValue( ref r );
		}
		else if ( method == Protocol.RpcTakeOwnership )
		{
			owner = sender;
			versionArg = BytePack.ReadValue( ref r );
		}
		else if ( method == Protocol.RpcDropOwnership )
		{
			owner = Guid.Empty;
			versionArg = BytePack.ReadValue( ref r );
		}
		else
		{
			return;
		}

		if ( versionArg is not ushort snapshotVersion ) return;

		lock ( stateLock )
		{
			if ( !objects.TryGetValue( objectId, out var obj ) ) return;

			// Same rule as GameObject.ApplyOwnership
			var version = (ushort)(snapshotVersion + 1);
			var ahead = (short)(version - obj.Version);
			if ( ahead < 0 || (ahead == 0 && owner.CompareTo( obj.Owner ) <= 0) ) return;

			obj.Version = version;
			obj.Owner = owner;
		}
	}

	/// <summary>
	/// Called on the tick thread at the tick rate once joined.
	/// </summary>
	public void Tick( double now, float delta )
	{
		if ( State != BotState.Active ) return;

		try
		{
			TickInput( now );
			var moving = TickMovement( now, delta );
			SendClientTick();
			SendClusterAcks();
			TickChat( now );

			IsMoving = moving;
		}
		catch ( Exception e ) when ( e is SocketException or ObjectDisposedException or IOException )
		{
			Close( e.Message );
		}
	}

	void TickInput( double now )
	{
		if ( !options.Input || now < nextActionChange ) return;

		// Hold a few random actions for a while, like someone running around pressing things
		actions = 0;
		for ( var i = 0; i < options.InputActions; i++ )
		{
			if ( random.NextSingle() < 0.2f ) actions |= 1UL << i;
		}

		nextActionChange = now + 0.3 + random.NextDouble() * 1.5;
	}

	bool TickMovement( double now, float delta )
	{
		if ( !options.Move ) return false;

		var w = tickWriter.Reset();
		var count = 0;

		// [DeltaSnapshotCluster][len][ushort clusterId][ushort count][double time] then per object:
		// [ushort version][uint snapshotId][Guid id][ushort slots] { [int slot][int len][BytePack value] }
		w.Write( (byte)MessageType.DeltaSnapshotCluster );
		var lengthAt = w.ReserveInt();
		w.Write( clusterId++ );
		var countAt = w.PayloadLength;
		w.Write( (ushort)0 );
		w.Write( now );

		lock ( stateLock )
		{
			foreach ( var obj in objects.Values )
			{
				// Move the roots we own; owned children follow their parent
				if ( obj.Owner != connectionId || connectionId == Guid.Empty ) continue;
				if ( objects.TryGetValue( obj.Parent, out var parent ) && parent.Owner == connectionId ) continue;

				Wander( obj, delta );

				var rotation = Quaternion.CreateFromAxisAngle( Vector3.UnitZ, obj.Yaw );

				w.Write( obj.Version );
				w.Write( ++obj.SnapshotId );
				w.Write( obj.Id );
				w.Write( (ushort)2 );

				w.Write( Protocol.SlotPosition );
				w.Write( 13 );
				w.Write( (byte)PackId.Vector3 );
				w.Write( obj.Position );

				w.Write( Protocol.SlotRotation );
				w.Write( 17 );
				w.Write( (byte)PackId.Rotation );
				w.Write( rotation );

				if ( count == 0 ) origin = obj.Position;
				count++;
			}
		}

		if ( count == 0 ) return false;

		BinaryPrimitives.WriteUInt16LittleEndian( w.Payload[countAt..], (ushort)count );
		w.PatchLength( lengthAt );
		Send( w, droppable: true );
		return true;
	}

	void Wander( NetObject obj, float delta )
	{
		var toTarget = obj.Target - obj.Position;
		toTarget.Z = 0;
		var distance = toTarget.Length();
		var step = options.MoveSpeed * delta;

		if ( distance <= step )
		{
			obj.Position = obj.Target with { Z = obj.Position.Z };

			var angle = random.NextSingle() * MathF.Tau;
			var radius = MathF.Sqrt( random.NextSingle() ) * options.MoveRadius;
			obj.Target = obj.Home + new Vector3( MathF.Cos( angle ) * radius, MathF.Sin( angle ) * radius, 0 );
			return;
		}

		var direction = toTarget / distance;
		obj.Position += direction * step;
		obj.Yaw = MathF.Atan2( direction.Y, direction.X );
	}

	void SendClientTick()
	{
		// [ClientTick][char originCount] { float x, y, z } [uint commandNumber][ulong actions]
		var w = tickWriter.Reset();
		w.Write( (byte)MessageType.ClientTick );
		w.Write( (char)1 );
		w.Write( origin );
		w.Write( ++commandNumber );
		w.Write( options.Input ? actions : 0UL );
		Send( w, droppable: true );
	}

	void SendClusterAcks()
	{
		var w = tickWriter.Reset();

		lock ( stateLock )
		{
			if ( pendingClusterAcks.Count == 0 ) return;

			// [DeltaSnapshotClusterAck][len][ushort count] { [ushort clusterId][ushort invalidCount = 0] }
			w.Write( (byte)MessageType.DeltaSnapshotClusterAck );
			var lengthAt = w.ReserveInt();
			w.Write( (ushort)pendingClusterAcks.Count );

			foreach ( var id in pendingClusterAcks )
			{
				w.Write( id );
				w.Write( (ushort)0 );
			}

			pendingClusterAcks.Clear();
			w.PatchLength( lengthAt );
		}

		Send( w, droppable: true );
	}

	void TickChat( double now )
	{
		if ( options.ChatInterval <= 0 ) return;

		if ( nextChat == 0 )
		{
			nextChat = now + random.NextDouble() * options.ChatInterval * 2;
			return;
		}

		if ( now < nextChat ) return;
		nextChat = now + options.ChatInterval * (0.5 + random.NextDouble());

		var w = BeginPacked( tickWriter );
		w.WriteStruct( Protocol.ChatMsg, new Dictionary<string, Action<PacketWriter>>
		{
			["Message"] = x => x.WriteString( ChatLines[random.Next( ChatLines.Length )] ),
		} );
		Send( w, droppable: true );
		Interlocked.Increment( ref stats.ChatSent );
	}

	static PacketWriter BeginPacked( PacketWriter w )
	{
		w.Reset();
		w.Write( (byte)MessageType.Packed );
		return w;
	}

	void SendPod( PackSchema schema, Guid value )
	{
		var w = BeginPacked( receiveWriter );
		w.WritePod( schema, value );
		Send( w );
	}

	/// <summary>
	/// Queue a message for this bot's writer. Never blocks, so a server that stops reading only backs up
	/// its own bots, not the tick for the whole swarm. Per-tick traffic is <paramref name="droppable"/> -
	/// it's unreliable in the engine too - and is dropped once the queue is backed up.
	/// </summary>
	void Send( PacketWriter w, bool droppable = false )
	{
		if ( State == BotState.Closed ) return;

		if ( droppable && Volatile.Read( ref queuedSends ) >= MaxQueuedSends )
		{
			Interlocked.Increment( ref stats.SendsDropped );
			return;
		}

		var frame = w.Frame();
		var buffer = ArrayPool<byte>.Shared.Rent( frame.Length );
		frame.CopyTo( buffer );

		outgoing.Enqueue( (buffer, frame.Length) );
		Interlocked.Increment( ref queuedSends );
		outgoingQueued.Release();
	}

	async Task SendLoop( CancellationToken cancel )
	{
		try
		{
			while ( true )
			{
				await outgoingQueued.WaitAsync( cancel );
				if ( !outgoing.TryDequeue( out var item ) ) continue;

				Interlocked.Decrement( ref queuedSends );

				try
				{
					var memory = item.Buffer.AsMemory( 0, item.Length );
					while ( memory.Length > 0 )
					{
						var sent = await socket.SendAsync( memory, cancel );
						memory = memory[sent..];
					}
				}
				finally
				{
					ArrayPool<byte>.Shared.Return( item.Buffer );
				}

				Interlocked.Add( ref stats.BytesOut, item.Length );
				Interlocked.Increment( ref stats.MessagesOut );
			}
		}
		catch ( OperationCanceledException )
		{
			// Closing
		}
		catch ( Exception e ) when ( e is SocketException or ObjectDisposedException )
		{
			Close( e.Message );
		}
	}
}
