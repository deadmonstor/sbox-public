using Sandbox.Network;

namespace Sandbox;

/// <summary>
/// Records a per-GameObject timeline of SyncVar changes and RPC calls, for use by the Editor's
/// Network Timeline debugger. Disabled (near-zero overhead) unless <see cref="IsRecording"/> is true.
/// </summary>
[Expose]
public sealed partial class NetworkTimelineRecorder : GameObjectSystem<NetworkTimelineRecorder>
{
	public NetworkTimelineRecorder( Scene scene ) : base( scene )
	{
	}

	public bool IsRecording { get; private set; }

	/// <summary>
	/// True while <see cref="ScrubTo"/> is actively writing a historical value onto a GameObject.
	/// Checked by the <c>[Sync]</c> property codegen (<c>__sync_SetValue</c>) so a scrub-apply can
	/// still get through - and still fire <c>[Change]</c> callbacks - even though the object's own
	/// live writes are being suppressed by <see cref="NetworkTable.Frozen"/> at the same time.
	/// </summary>
	internal static bool IsApplyingScrub { get; private set; }

	public enum EventKind
	{
		SyncVar,
		RpcOut,
		RpcIn,
		Transform
	}

	/// <summary>
	/// Reserved <see cref="TimelineEvent.Slot"/> value for the GameObject's transform, which is
	/// networked via a dedicated delta-snapshot path rather than a <see cref="NetworkTable.Entry"/>.
	/// </summary>
	private const int TransformSlot = -2;

	public readonly record struct TimelineEvent(
		double Time,
		EventKind Kind,
		Guid TargetGuid,
		int Slot,
		string Label,
		object OldValue,
		object NewValue,
		Guid ConnectionId );

	private const int MaxEventsPerObject = 20_000;

	private sealed class ObjectTimeline
	{
		public readonly List<TimelineEvent> Events = new();
		public readonly Dictionary<int, object> InitialSnapshot = new();
		public bool Scrubbing;
	}

	private readonly Dictionary<Guid, ObjectTimeline> _timelines = new();

	/// <summary>
	/// Start recording SyncVar/RPC events for every currently networked object in the scene.
	/// </summary>
	public void StartRecording()
	{
		IsRecording = true;

		foreach ( var go in Scene.GetAllObjects( false ) )
		{
			if ( go.IsNetworkRoot )
				BeginTracking( go );
		}
	}

	/// <summary>
	/// Stop capturing new events and return every scrubbed object to live. Existing history is kept
	/// so it can still be viewed/scrubbed - use <see cref="ClearHistory"/> to discard it.
	/// </summary>
	public void StopRecording()
	{
		IsRecording = false;

		foreach ( var rootId in _timelines.Keys.ToArray() )
		{
			ReturnToLive( rootId );
		}
	}

	/// <summary>
	/// Return every scrubbed object to live and discard all recorded history.
	/// </summary>
	public void ClearHistory()
	{
		foreach ( var rootId in _timelines.Keys.ToArray() )
		{
			ReturnToLive( rootId );
		}

		_timelines.Clear();
	}

	private ObjectTimeline GetOrCreateTimeline( GameObject root )
	{
		if ( _timelines.TryGetValue( root.Id, out var existing ) )
			return existing;

		BeginTracking( root );
		return _timelines[root.Id];
	}

	private void BeginTracking( GameObject root )
	{
		if ( root is null || !root.IsValid() || root._net?.dataTable is not { } table )
			return;

		if ( _timelines.ContainsKey( root.Id ) )
			return;

		var timeline = new ObjectTimeline();

		timeline.InitialSnapshot[TransformSlot] = root.Transform.TargetLocal;

		foreach ( var entry in table.AllEntriesForSnapshot )
		{
			timeline.InitialSnapshot[entry.Slot] = Clone( entry.GetValue() );
		}

		_timelines[root.Id] = timeline;
	}

	/// <summary>
	/// Called by <see cref="NetworkTable"/> whenever a registered entry's value genuinely changes.
	/// </summary>
	internal void RecordSyncVarChange( GameObject owner, NetworkTable.Entry entry, object oldValue, object newValue )
	{
		if ( !IsRecording || owner is null || !owner.IsValid() )
			return;

		var root = owner.NetworkRoot ?? owner;
		var timeline = GetOrCreateTimeline( root );

		// Don't clutter the timeline with new events for an object while it's paused/scrubbed.
		if ( timeline.Scrubbing )
			return;

		AddEvent( timeline, new TimelineEvent(
			Time.NowDouble,
			EventKind.SyncVar,
			entry.TargetGuid,
			entry.Slot,
			entry.DebugName,
			Clone( oldValue ),
			Clone( newValue ),
			Guid.Empty ) );
	}

	/// <summary>
	/// Called by <see cref="NetworkObject"/> whenever an incoming delta snapshot changes this object's
	/// transform - movement is networked via a dedicated path, not a <see cref="NetworkTable.Entry"/>.
	/// </summary>
	internal void RecordTransformChange( GameObject owner, Transform oldValue, Transform newValue )
	{
		if ( !IsRecording || owner is null || !owner.IsValid() )
			return;

		var root = owner.NetworkRoot ?? owner;
		var timeline = GetOrCreateTimeline( root );

		// Don't clutter the timeline with new events for an object while it's paused/scrubbed.
		if ( timeline.Scrubbing )
			return;

		AddEvent( timeline, new TimelineEvent(
			Time.NowDouble,
			EventKind.Transform,
			root.Id,
			TransformSlot,
			"Transform",
			oldValue,
			newValue,
			Guid.Empty ) );
	}

	/// <summary>
	/// Called by <see cref="Rpc"/> whenever an instance RPC is sent or received.
	/// </summary>
	internal void RecordRpc( Guid goGuid, Guid componentGuid, string label, object[] arguments, bool outbound, Connection connection )
	{
		if ( !IsRecording )
			return;

		var go = Scene.Directory.FindByGuid( goGuid );
		if ( go is null )
			return;

		var root = go.NetworkRoot ?? go;
		var timeline = GetOrCreateTimeline( root );

		// Don't clutter the timeline with new events for an object while it's paused/scrubbed.
		if ( timeline.Scrubbing )
			return;

		AddEvent( timeline, new TimelineEvent(
			Time.NowDouble,
			outbound ? EventKind.RpcOut : EventKind.RpcIn,
			componentGuid != Guid.Empty ? componentGuid : goGuid,
			-1,
			label,
			null,
			Clone( arguments ),
			connection?.Id ?? Guid.Empty ) );
	}

	private static void AddEvent( ObjectTimeline timeline, TimelineEvent ev )
	{
		timeline.Events.Add( ev );

		if ( timeline.Events.Count > MaxEventsPerObject )
			timeline.Events.RemoveAt( 0 );
	}

	/// <summary>
	/// All recorded events for the given network-root GameObject, oldest first.
	/// </summary>
	public IReadOnlyList<TimelineEvent> GetEvents( Guid rootId )
	{
		return _timelines.TryGetValue( rootId, out var timeline ) ? timeline.Events : Array.Empty<TimelineEvent>();
	}

	public bool IsScrubbing( Guid rootId )
	{
		return _timelines.TryGetValue( rootId, out var timeline ) && timeline.Scrubbing;
	}

	/// <summary>
	/// Reconstruct and apply this object's SyncVar state as it was at <paramref name="time"/>, freezing
	/// it against further live network updates until <see cref="ReturnToLive"/> is called.
	/// </summary>
	public void ScrubTo( Guid rootId, double time )
	{
		if ( !_timelines.TryGetValue( rootId, out var timeline ) )
			return;

		var root = Scene.Directory.FindByGuid( rootId );
		if ( root?._net?.dataTable is not { } table )
			return;

		table.Frozen = true;
		timeline.Scrubbing = true;

		// Lets the scrub-apply below get through the [Sync] setter (and fire [Change]) even though
		// NetworkTable.Frozen is simultaneously blocking the object's own live writes from doing the
		// same. RecordSyncVarChange/RecordTransformChange still won't log this as a new event, since
		// timeline.Scrubbing is already true.
		IsApplyingScrub = true;

		try
		{
			foreach ( var (slot, initialValue) in timeline.InitialSnapshot )
			{
				var value = initialValue;

				foreach ( var ev in timeline.Events )
				{
					if ( ev.Slot != slot || ev.Time > time )
						continue;

					if ( ev.Kind != EventKind.SyncVar && ev.Kind != EventKind.Transform )
						continue;

					value = ev.NewValue;
				}

				if ( slot == TransformSlot )
				{
					root.Transform.SetLocalTransformFast( (Transform)value );
				}
				else
				{
					table.GetEntry( slot )?.SetValue?.Invoke( value );
				}
			}
		}
		finally
		{
			IsApplyingScrub = false;
		}
	}

	/// <summary>
	/// Stop overriding this object's state - it will resync from the network as normal.
	/// </summary>
	public void ReturnToLive( Guid rootId )
	{
		if ( !_timelines.TryGetValue( rootId, out var timeline ) )
			return;

		timeline.Scrubbing = false;

		var root = Scene.Directory.FindByGuid( rootId );
		if ( root?._net?.dataTable is { } table )
			table.Frozen = false;
	}

	private static object Clone( object value )
	{
		if ( value is null )
			return null;

		try
		{
			var bs = ByteStream.Create( 256 );
			try
			{
				Game.TypeLibrary.ToBytes( value, ref bs );
				var reader = ByteStream.CreateReader( bs.ToArray() );
				try
				{
					return Game.TypeLibrary.FromBytes<object>( ref reader );
				}
				finally
				{
					reader.Dispose();
				}
			}
			finally
			{
				bs.Dispose();
			}
		}
		catch
		{
			// Not everything round-trips (e.g. some custom INetworkSerializer types) - fall back to
			// referencing the value directly rather than losing the event entirely.
			return value;
		}
	}
}
