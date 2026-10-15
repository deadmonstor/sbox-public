namespace Sandbox;

/// <summary>
/// One root-shared, host-seeded command timeline. Participants share coherent snapshots and
/// acknowledgements; only the owner and host simulate. Fixed updates are required; native physics is not replayed.
/// </summary>
[Title( "Prediction" ), Category( "Networking" ), Icon( "sync" )]
public sealed class Prediction : Component, IPredictionTimeline
{
	internal const int Capacity = 128;
	internal const float MaximumInterpolationDelay = 0.1f;
	const int MaximumParticipants = 16;
	const int MaximumCommandBytes = 4096;
	const int MaximumSnapshotBytes = 65536;
	const int BatchSize = 16;

	/// <summary>
	/// The maximum interpolation delay allowed for owner commands.
	/// </summary>
	[Property, Sync( SyncFlags.FromHost ), Range( 0, MaximumInterpolationDelay )]
	public float InterpolationDelay
	{
		get;
		set => field = Math.Clamp( float.IsFinite( value ) ? value : MaximumInterpolationDelay, 0, MaximumInterpolationDelay );
	} = MaximumInterpolationDelay;

	float RewindLimit => Scene.GetSystem<LagCompensationSystem>()?.MaximumRewind ?? 1f;

	[Sync( SyncFlags.FromHost )]
	byte[] Snapshot { get; set; }

	readonly List<PredictionParticipantData> participants = new();
	PredictionParticipantData[] ordered = Array.Empty<PredictionParticipantData>();
	readonly PredictionCommandQueue<float> queued = new( Capacity );
	readonly uint[] pending = new uint[Capacity];
	readonly byte[][] pendingInputs = new byte[Capacity][];
	readonly int[] pendingInputLengths = new int[Capacity];
	readonly PredictionClock clock = new();
	readonly PredictionCommandTime[] commandTimes = new PredictionCommandTime[Capacity];
	readonly List<(Guid Participant, PredictionEventId Id, bool Accepted)> confirmations = new();
	Guid owner;
	Guid resetEpoch;
	Guid previousEpoch;
	bool authority;
	bool dirty;
	bool finalized;
	bool executing;
	bool interruptedPhysics;
	bool ownsTransformAuthority;
	float configuredInterpolation;
	float configuredRewind;
	float configuredFrequency;
	float localFrequency;
	float delta;
	float localDelta;
	uint seed;
	uint nextCommand;
	uint executingCommand;
	int pendingStart;
	int pendingCount;
	double lastStep = double.NaN;
	IDisposable fixedUpdateHook;

	/// <summary>
	/// The current host-issued timeline identity.
	/// </summary>
	public Guid Epoch { get; private set; }

	/// <summary>
	/// The latest locally issued command number.
	/// </summary>
	public uint Command => nextCommand;

	/// <summary>
	/// The latest command acknowledged by the authority.
	/// </summary>
	public uint AcknowledgedCommand { get; private set; }

	internal PredictionContext Context { get; private set; }

	internal bool IsPredictingLocally => Active && Epoch != Guid.Empty && participants.Count > 0 && !IsProxy;

	uint IPredictionTimeline.ExecutingCommand => executing ? executingCommand : AcknowledgedCommand;

	Guid IPredictionTimeline.Epoch => IsTimelineCurrent ? Epoch : Guid.Empty;

	bool IsAuthority => !Networking.IsActive || Networking.IsHost;

	bool IsTimelineCurrent => Enabled && !dirty && owner == Network.OwnerId && authority == IsAuthority
		&& configuredInterpolation == InterpolationDelay && configuredRewind == RewindLimit
		&& configuredFrequency == ProjectSettings.Physics.FixedUpdateFrequency;

	/// <summary>
	/// Resolve a recent executed command from the live host epoch.
	/// </summary>
	public bool TryGetCommandTime( Guid epoch, uint command, out PredictionCommandTime time )
	{
		time = default;

		if ( !IsAuthority || !IsTimelineCurrent || epoch == Guid.Empty || epoch != Epoch
			|| unchecked(AcknowledgedCommand - command) >= Capacity )
		{
			return false;
		}

		var recorded = commandTimes[command % Capacity];

		if ( recorded.Epoch != Epoch || recorded.Command != command )
		{
			return false;
		}

		time = recorded;

		return true;
	}

	internal static Prediction GetOrCreate( Component participant )
	{
		var root = participant.Network.RootGameObject;
		var runner = root.Components.Get<Prediction>( FindMode.EverythingInSelf );

		if ( runner is not null )
		{
			return runner;
		}

		// Only the host creates the timeline's component identity.
		if ( Networking.IsActive && !Networking.IsHost )
		{
			return null;
		}

		runner = root.Components.Create<Prediction>();

		if ( root.Network.Active )
		{
			root.Network.Refresh( runner );
		}

		return runner;
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();

		if ( Scene.IsEditor )
		{
			return;
		}

		fixedUpdateHook = Scene.AddHook( GameObjectSystem.Stage.FinishFixedUpdate, 0,
			CompleteFixedUpdate, nameof( Prediction ), "Finalize prediction" );

		foreach ( var participant in Components.GetAll<IPredictionParticipant>( FindMode.EnabledInSelfAndDescendants ) )
		{
			if ( ((Component)participant).Network.RootGameObject == GameObject )
			{
				participant.UpdatePredictionRegistration();
			}
		}

		dirty = true;
	}

	internal void Register( PredictionParticipantData participant )
	{
		if ( participants.Contains( participant ) )
		{
			return;
		}

		if ( participants.Count >= MaximumParticipants )
		{
			throw new InvalidOperationException( "A prediction root supports at most 16 participants." );
		}

		if ( participant.Component.Network.RootGameObject != GameObject )
		{
			throw new InvalidOperationException( "Prediction participants must belong to the same network root." );
		}

		participants.Add( participant );
		participant.RunnerChanged( this );
		dirty = true;
		RefreshParticipant( participant );
	}

	internal void Unregister( PredictionParticipantData participant )
	{
		if ( participant is not null && participants.Remove( participant ) )
		{
			participant.RunnerChanged( null );
			dirty = true;
			RefreshParticipant( participant );
		}
	}

	void RefreshParticipant( PredictionParticipantData participant )
	{
		// Keep enrollment changes coherent with the root snapshot.
		if ( IsAuthority && Network.Active && participant.Component.Network.RootGameObject == GameObject )
		{
			Network.Refresh( participant.Component );
		}
	}

	void SetupParticipants()
	{
		foreach ( var participant in participants.ToArray() )
		{
			if ( participant.Component is IPredictionParticipant registration )
			{
				registration.UpdatePredictionRegistration();
			}
		}

		ordered = Array.Empty<PredictionParticipantData>();

		var remaining = participants.OrderBy( x => x.Order ).ThenBy( x => x.Component.Id ).ToList();
		var result = new List<PredictionParticipantData>( remaining.Count );
		var dependencies = remaining.ToDictionary( x => x, x => x.Dependencies.ToArray() );

		foreach ( var entry in dependencies )
		{
			foreach ( var dependency in entry.Value )
			{
				if ( dependency is null || !participants.Any( x => x.Component == dependency ) )
				{
					if ( IsAuthority )
					{
						dirty = false;
						throw new InvalidOperationException( "Prediction dependency is not an enabled participant on this root." );
					}

					// Wait for the host's enrollment refresh before simulating.
					ordered = Array.Empty<PredictionParticipantData>();
					ResetLocal();
					return;
				}
			}
		}

		while ( remaining.Count > 0 )
		{
			var index = remaining.FindIndex( x => dependencies[x].All( d => result.Any( p => p.Component == d ) ) );
			if ( index < 0 )
			{
				dirty = false;
				throw new InvalidOperationException( "Prediction dependency cycle." );
			}

			result.Add( remaining[index] );
			remaining.RemoveAt( index );
		}

		ordered = result.ToArray();
		dirty = false;

		if ( ordered.Length == 0 )
		{
			ReleaseTransformAuthority();
			Epoch = Guid.Empty;
			Snapshot = null;
			ResetLocal();
		}
		else if ( IsAuthority )
		{
			ResetTimeline();
		}
		else
		{
			ResetLocal();
		}
	}

	/// <summary>
	/// Host-only discontinuity: call after teleport, respawn, or changing simulation configuration.
	/// </summary>
	public void ResetTimeline()
	{
		if ( !IsAuthority || Scene.IsEditor || !ProjectSettings.Physics.UseFixedUpdate )
		{
			return;
		}

		var interrupted = executing;
		previousEpoch = Epoch;
		Epoch = Guid.NewGuid();
		seed = (uint)Random.Shared.NextInt64( uint.MaxValue + 1L );
		owner = Network.OwnerId;
		authority = IsAuthority;
		configuredFrequency = ProjectSettings.Physics.FixedUpdateFrequency;

		if ( !float.IsFinite( configuredFrequency ) || configuredFrequency <= 0 )
		{
			throw new InvalidOperationException( "Prediction requires a finite positive fixed frequency." );
		}

		delta = (float)(1d / configuredFrequency);
		configuredInterpolation = InterpolationDelay;
		configuredRewind = RewindLimit;
		clock.Reset( delta );
		ResetLocal();
		interruptedPhysics = interrupted;
		Transform.ClearInterpolation();
		Publish();
	}

	void ResetLocal( bool clearClientEpoch = true )
	{
		if ( clearClientEpoch && !IsAuthority )
		{
			if ( Epoch != Guid.Empty )
			{
				previousEpoch = Epoch;
			}

			Epoch = Guid.Empty;
		}

		nextCommand = 0;
		AcknowledgedCommand = 0;
		pendingStart = pendingCount = 0;
		Array.Clear( pendingInputLengths );
		queued.Reset();
		Array.Clear( commandTimes );
		resetEpoch = Guid.Empty;
		finalized = false;
		executing = false;
		interruptedPhysics = false;
		Context = default;

		foreach ( var participant in ordered )
		{
			participant.Reset();
		}
	}

	protected override void OnFixedUpdate() => Step();

	internal void Step()
	{
		if ( Scene.IsEditor || !ProjectSettings.Physics.UseFixedUpdate || !Enabled )
		{
			return;
		}

		// The controller may step before the runner's fixed callback.
		if ( lastStep == Time.NowDouble )
		{
			return;
		}

		lastStep = Time.NowDouble;
		interruptedPhysics = false;

		for ( var i = participants.Count - 1; i >= 0; i-- )
		{
			var participant = participants[i];
			var root = participant.Component.Network.RootGameObject;

			if ( root == GameObject )
			{
				continue;
			}

			if ( participant.Component is IPredictionParticipant registration )
			{
				registration.UpdatePredictionRegistration();
			}
			else
			{
				Unregister( participant );
				GetOrCreate( participant.Component )?.Register( participant );
			}
		}

		if ( dirty )
		{
			SetupParticipants();
		}

		if ( ordered.Length == 0 )
		{
			return;
		}

		if ( owner != Network.OwnerId || authority != IsAuthority || (localDelta > 0 && MathF.Abs( localDelta - Time.Delta ) > 0.0001f)
			|| (localFrequency > 0 && localFrequency != ProjectSettings.Physics.FixedUpdateFrequency)
			|| (IsAuthority && (configuredFrequency != ProjectSettings.Physics.FixedUpdateFrequency
				|| configuredInterpolation != InterpolationDelay || configuredRewind != RewindLimit)) )
		{
			owner = Network.OwnerId;
			authority = IsAuthority;
			if ( IsAuthority )
			{
				ResetTimeline();
			}
			else
			{
				ResetLocal();
			}
		}

		localDelta = Time.Delta;
		localFrequency = ProjectSettings.Physics.FixedUpdateFrequency;

		if ( IsAuthority && Epoch == Guid.Empty )
		{
			ResetTimeline();
		}

		if ( !IsAuthority )
		{
			Consume();
		}

		if ( Epoch == Guid.Empty )
		{
			return;
		}

		if ( !IsAuthority && configuredFrequency != ProjectSettings.Physics.FixedUpdateFrequency )
		{
			return;
		}

		if ( IsAuthority && (Network.Flags & NetworkFlags.HostTransformAuthority) == 0 )
		{
			ownsTransformAuthority = true;
			Network.Flags |= NetworkFlags.HostTransformAuthority;
		}

		finalized = false;

		if ( !IsProxy )
		{
			if ( !IsAuthority && pendingCount == Capacity )
			{
				RequestResynchronization();
				return;
			}

			var command = unchecked(++nextCommand);

			foreach ( var participant in ordered )
			{
				participant.Capture( command );
			}

			if ( !IsAuthority )
			{
				RecordInput( command );
			}

			if ( !Execute( command, IsAuthority ? PredictionPhase.Authority : PredictionPhase.Predict, CaptureInterpolation() ) )
			{
				return;
			}

			if ( !IsAuthority )
			{
				SendInputs();
			}
		}
		else if ( IsAuthority )
		{
			queued.AddCredit( delta );

			while ( queued.TryTake( delta, out var command, out var interpolation ) )
			{
				nextCommand = command;

				if ( !Execute( command, PredictionPhase.Authority, interpolation ) )
				{
					break;
				}
			}
		}
	}

	float CaptureInterpolation() => Math.Clamp( float.IsFinite( Networking.InterpolationTime ) ? Networking.InterpolationTime : 0, 0, InterpolationDelay );

	bool Execute( uint command, PredictionPhase phase, float interpolation = 0 )
	{
		executing = true;
		executingCommand = command;
		var time = phase == PredictionPhase.Authority
			? new PredictionCommandTime( this, command, clock.Execute( command, Time.NowDouble, IsProxy ? Connection.Find( owner )?.Ping * 0.001f ?? 0 : 0,
				RewindLimit ), interpolation ) : default;
		var context = new PredictionContext( GameObject.Id, Epoch, command, delta, phase, seed, time );
		Context = context;

		try
		{
			foreach ( var participant in ordered )
			{
				if ( dirty || Epoch != context.Epoch )
				{
					return false;
				}

				participant.SetContext( context );
				participant.Execute( context );
			}

			if ( phase != PredictionPhase.Replay )
			{
				foreach ( var participant in ordered )
				{
					if ( dirty || Epoch != context.Epoch )
					{
						return false;
					}

					participant.AfterExecute( context );
				}
			}

			if ( dirty || Epoch != context.Epoch )
			{
				return false;
			}

			if ( phase == PredictionPhase.Authority )
			{
				commandTimes[command % Capacity] = time;
			}
		}
		finally
		{
			executing = false;
		}

		finalized = true;

		return true;
	}

	void CompleteFixedUpdate()
	{
		if ( dirty || lastStep != Time.NowDouble || interruptedPhysics || Epoch == Guid.Empty
			|| (!finalized && !IsAuthority) || ordered.Length == 0 )
		{
			return;
		}

		// Finalize native contacts without another simulation or acknowledgement.
		var context = Context.Epoch == Epoch ? Context
			: new PredictionContext( GameObject.Id, Epoch, AcknowledgedCommand, delta, PredictionPhase.Authority, seed );

		foreach ( var participant in ordered )
		{
			participant.FinalizeSimulation( context );
		}

		if ( IsAuthority && finalized )
		{
			AcknowledgedCommand = context.Command;
			Publish();
		}

		finalized = false;
	}

	void RecordInput( uint command )
	{
		var stream = ByteStream.Create( 256 );
		try
		{
			stream.Write( CaptureInterpolation() );
			stream.Write( ordered.Length );

			foreach ( var participant in ordered )
			{
				stream.Write( participant.Component.Id );
				participant.WriteInput( command, ref stream );

				if ( stream.Length > MaximumCommandBytes )
				{
					throw new InvalidOperationException( "Prediction command exceeds 4096 bytes." );
				}
			}

			var index = (pendingStart + pendingCount++) % Capacity;
			pending[index] = command;

			if ( pendingInputs[index] is not { } buffer || buffer.Length < stream.Length )
			{
				buffer = new byte[Math.Max( 256, stream.Length )];
				pendingInputs[index] = buffer;
			}

			stream.ToSpan().CopyTo( buffer );
			pendingInputLengths[index] = stream.Length;
		}
		finally
		{
			stream.Dispose();
		}
	}

	void SendInputs()
	{
		var stream = ByteStream.Create( 1024 );
		try
		{
			var count = Math.Min( pendingCount, BatchSize );
			var oldest = pendingCount <= BatchSize ? count : (count + 1) / 2;
			stream.Write( count );

			for ( var i = 0; i < count; i++ )
			{
				var offset = i < oldest ? i : pendingCount - count + i;
				var index = (pendingStart + offset) % Capacity;
				stream.Write( pending[index] );
				stream.WriteArray<byte>( pendingInputs[index].AsSpan( 0, pendingInputLengths[index] ) );
			}

			Submit( Epoch, stream.ToArray() );
		}
		finally
		{
			stream.Dispose();
		}
	}

	[Expose, Rpc.Host( NetFlags.OwnerOnly | NetFlags.Unreliable )]
	void Submit( Guid epoch, byte[] payload )
	{
		if ( !IsTimelineCurrent || epoch != Epoch || epoch == Guid.Empty || Rpc.Caller?.Id != owner
			|| payload is null || payload.Length > BatchSize * (MaximumCommandBytes + 8) + 4 )
		{
			return;
		}

		var stream = ByteStream.CreateReader( payload );
		try
		{
			var count = stream.Read<int>();

			if ( count < 0 || count > BatchSize )
			{
				return;
			}

			for ( var i = 0; i < count; i++ )
			{
				var command = stream.Read<uint>();
				var length = stream.Read<int>();

				if ( length < 0 || length > MaximumCommandBytes )
				{
					return;
				}

				stream.Position -= sizeof( int );
				var input = stream.ReadArraySpan<byte>( MaximumCommandBytes );

				if ( queued.CanAccept( command ) && ReadInput( command, input, out var interpolation ) )
				{
					queued.Enqueue( command, interpolation );
				}
			}
		}
		catch ( Exception exception ) when ( exception is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException or FormatException )
		{
			// Malformed owner payload is not an executable command.
		}
		finally
		{
			stream.Dispose();
		}
	}

	bool ReadInput( uint command, ReadOnlySpan<byte> payload, out float interpolation )
	{
		var stream = ByteStream.CreateReader( payload );
		try
		{
			interpolation = stream.Read<float>();

			if ( !float.IsFinite( interpolation ) || interpolation < 0 )
			{
				return false;
			}

			interpolation = Math.Min( interpolation, InterpolationDelay );

			if ( stream.Read<int>() != ordered.Length )
			{
				return false;
			}

			foreach ( var participant in ordered )
			{
				if ( stream.Read<Guid>() != participant.Component.Id || !participant.ReadInput( command, ref stream ) )
				{
					return false;
				}
			}

			return stream.ReadRemaining == 0;
		}
		finally
		{
			stream.Dispose();
		}
	}

	void Publish()
	{
		var stream = ByteStream.Create( 1024 );
		try
		{
			stream.Write( Epoch );
			stream.Write( owner );
			stream.Write( AcknowledgedCommand );
			stream.Write( seed );
			stream.Write( delta );
			stream.Write( configuredFrequency );
			stream.Write( ordered.Length );

			foreach ( var participant in ordered )
			{
				stream.Write( participant.Component.Id );
				participant.WriteState( ref stream );

				if ( stream.Length > MaximumSnapshotBytes )
				{
					throw new InvalidOperationException( "Prediction snapshot exceeds 65536 bytes." );
				}
			}

			Snapshot = stream.ToArray();
		}
		finally
		{
			stream.Dispose();
		}
	}

	void Consume()
	{
		if ( Snapshot is null || Snapshot.Length > MaximumSnapshotBytes )
		{
			return;
		}

		var stream = ByteStream.CreateReader( Snapshot );
		try
		{
			var epoch = stream.Read<Guid>();
			var snapshotOwner = stream.Read<Guid>();
			var command = stream.Read<uint>();
			var snapshotSeed = stream.Read<uint>();
			var step = stream.Read<float>();
			var frequency = stream.Read<float>();

			if ( !float.IsFinite( frequency ) || frequency <= 0 || step != (float)(1d / frequency) )
			{
				return;
			}

			if ( epoch == Guid.Empty || snapshotOwner != owner || epoch == resetEpoch || epoch == previousEpoch || !float.IsFinite( step ) || step <= 0 )
			{
				return;
			}

			if ( stream.Read<int>() != ordered.Length )
			{
				return;
			}

			var newEpoch = epoch != Epoch;

			if ( !newEpoch && unchecked(command - AcknowledgedCommand) is 0 or > 0x7FFFFFFF )
			{
				return;
			}

			var acknowledgedIndex = -1;

			if ( !newEpoch && !IsProxy )
			{
				for ( var i = 0; i < pendingCount; i++ )
				{
					if ( pending[(pendingStart + i) % Capacity] == command )
					{
						acknowledgedIndex = i;
						break;
					}
				}

				if ( acknowledgedIndex < 0 )
				{
					RequestResynchronization();
					return;
				}
			}

			foreach ( var participant in ordered )
			{
				if ( stream.Read<Guid>() != participant.Component.Id )
				{
					return;
				}

				participant.ReadState( ref stream );
			}

			if ( stream.ReadRemaining != 0 )
			{
				return;
			}

			if ( !newEpoch && !IsProxy )
			{
				foreach ( var participant in ordered )
				{
					participant.BeginCorrection();
				}
			}

			if ( newEpoch )
			{
				if ( Epoch != Guid.Empty )
				{
					previousEpoch = Epoch;
				}

				Epoch = epoch;
				ResetLocal( false );
				nextCommand = command;
			}

			seed = snapshotSeed;
			delta = step;
			configuredFrequency = frequency;
			configuredInterpolation = InterpolationDelay;
			configuredRewind = RewindLimit;
			AcknowledgedCommand = command;

			foreach ( var participant in ordered )
			{
				participant.Restore();
			}

			if ( !newEpoch && !IsProxy )
			{
				foreach ( var participant in ordered )
				{
					participant.Acknowledge( command );
				}

				for ( var i = 0; i <= acknowledgedIndex; i++ )
				{
					pendingInputLengths[(pendingStart + i) % Capacity] = 0;
				}

				pendingStart = (pendingStart + acknowledgedIndex + 1) % Capacity;
				pendingCount -= acknowledgedIndex + 1;

				for ( var i = 0; i < pendingCount; i++ )
				{
					if ( !Execute( pending[(pendingStart + i) % Capacity], PredictionPhase.Replay ) )
					{
						return;
					}
				}

				finalized = false;
			}

			if ( !newEpoch && !IsProxy )
			{
				foreach ( var participant in ordered )
				{
					participant.EndCorrection();
				}
			}

			if ( newEpoch && !IsProxy )
			{
				Transform.ClearInterpolation();
			}

			FlushConfirmations();
		}
		finally
		{
			stream.Dispose();
		}
	}

	void RequestResynchronization()
	{
		var epoch = Epoch;
		ResetLocal();
		resetEpoch = epoch;
		RequestReset( epoch );
	}

	[Expose, Rpc.Host( NetFlags.OwnerOnly )]
	void RequestReset( Guid epoch )
	{
		if ( IsTimelineCurrent && epoch == Epoch && Rpc.Caller?.Id == owner )
		{
			ResetTimeline();
		}
	}

	internal void ConfirmEvent( Component participant, PredictionEventId id, bool accepted )
	{
		if ( !IsAuthority || !IsTimelineCurrent || !executing || id.Epoch != Epoch || id.Command != executingCommand )
		{
			return;
		}

		ReceiveConfirmation( participant.Id, id, accepted );
	}

	[Expose, Rpc.Owner( NetFlags.HostOnly )]
	void ReceiveConfirmation( Guid participantId, PredictionEventId id, bool accepted )
	{
		DeliverConfirmation( participantId, id, accepted );
	}

	void DeliverConfirmation( Guid participantId, PredictionEventId id, bool accepted )
	{
		if ( IsProxy || id.Object != GameObject.Id || id.Epoch == Guid.Empty || id.Epoch == previousEpoch )
		{
			return;
		}

		if ( id.Epoch == Epoch )
		{
			foreach ( var participant in ordered )
			{
				if ( participant.Component.Id == participantId )
				{
					participant.EventConfirmed( id, accepted );
					return;
				}
			}

			// The participant is no longer in the accepted layout.
			return;
		}

		if ( confirmations.Count >= Capacity )
		{
			confirmations.RemoveAt( 0 );
		}

		confirmations.Add( (participantId, id, accepted) );
	}

	void FlushConfirmations()
	{
		for ( var i = 0; i < confirmations.Count; i++ )
		{
			var confirmation = confirmations[i];

			if ( confirmation.Id.Epoch != Epoch && confirmation.Id.Epoch != previousEpoch )
			{
				continue;
			}

			confirmations.RemoveAt( i );
			i--;

			if ( confirmation.Id.Epoch == Epoch )
			{
				DeliverConfirmation( confirmation.Participant, confirmation.Id, confirmation.Accepted );
			}
		}
	}

	void ReleaseTransformAuthority()
	{
		if ( !IsAuthority || !ownsTransformAuthority )
		{
			return;
		}

		Network.Flags &= ~NetworkFlags.HostTransformAuthority;
		ownsTransformAuthority = false;
	}

	protected override void OnDisabled()
	{
		fixedUpdateHook?.Dispose();
		fixedUpdateHook = null;
		ReleaseTransformAuthority();
		previousEpoch = Epoch;
		Epoch = Guid.Empty;
		ResetLocal();
		base.OnDisabled();
	}
}
