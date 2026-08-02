namespace Sandbox.Network;

/// <summary>
/// GameObject and Component sync vars are sent as guids and looked up when they arrive. The object
/// might not be here yet if it isn't visible to us, and the sender has no reason to send the value
/// again - so keep the guid and apply it if the object turns up.
/// </summary>
internal static class NetworkReferenceResolver
{
	private static readonly Dictionary<Guid, HashSet<NetworkTable>> _waiting = new();

	private static bool _isReading;
	private static Guid _unresolved;

	internal static void BeginRead()
	{
		_isReading = true;
		_unresolved = default;
	}

	/// <summary>
	/// Stop watching, parking the slot if the value we read pointed at something we don't have.
	/// </summary>
	internal static void EndRead( NetworkTable table, int slot )
	{
		var unresolved = _unresolved;

		_isReading = false;
		_unresolved = default;

		table.ClearPendingReference( slot );

		if ( unresolved == default )
			return;

		table.SetPendingReference( slot, unresolved );

		if ( !_waiting.TryGetValue( unresolved, out var tables ) )
		{
			tables = new HashSet<NetworkTable>();
			_waiting[unresolved] = tables;
		}

		tables.Add( table );
	}

	/// <summary>
	/// Called by the GameObject and Component byte packers when a guid isn't in the scene.
	/// </summary>
	internal static void NotifyUnresolved( Guid id )
	{
		if ( !_isReading ) return;

		_unresolved = id;
	}

	internal static void Resolve( GameObject go ) => Resolve( go.Id, go );

	internal static void Resolve( Component component ) => Resolve( component.Id, component );

	private static void Resolve( Guid id, object value )
	{
		if ( _waiting.Count == 0 )
			return;

		if ( !_waiting.Remove( id, out var tables ) )
			return;

		foreach ( var table in tables )
		{
			table.ResolvePendingReferences( id, value );
		}
	}

	internal static void StopWaiting( Guid id, NetworkTable table )
	{
		if ( !_waiting.TryGetValue( id, out var tables ) )
			return;

		tables.Remove( table );

		if ( tables.Count == 0 )
			_waiting.Remove( id );
	}

	/// <summary>
	/// Called when the scene network system shuts down.
	/// </summary>
	internal static void Clear()
	{
		_waiting.Clear();

		_isReading = false;
		_unresolved = default;
	}
}
