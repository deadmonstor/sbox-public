namespace Sandbox;

public partial class GameTransform
{
	Action _onTransformChanged;
	Action<GameTransform> _onTransformChangedInternal;

	/// <summary>
	/// Called when the transform is changed
	/// </summary>
	public Action OnTransformChanged
	{
		get => _onTransformChanged;
		set
		{
			var had = HasListeners;
			_onTransformChanged = value;
			ListenersChanged( had );
		}
	}

	/// <summary>
	/// I need to know the root transform that actually changed
	/// </summary>
	internal Action<GameTransform> OnTransformChangedInternal
	{
		get => _onTransformChangedInternal;
		set
		{
			var had = HasListeners;
			_onTransformChangedInternal = value;
			ListenersChanged( had );
		}
	}

	bool HasListeners => _onTransformChanged is not null || _onTransformChangedInternal is not null;

	/// <summary>
	/// How many transforms in our hierarchy, including us, have change listeners. A change skips
	/// branches without any.
	/// </summary>
	int _listenersInHierarchy;

	/// <summary>
	/// A world transform may be cached somewhere below us. Set by whatever caches one, cleared when a change
	/// comes through. A change skips branches with nothing cached - mostly bones, whose world transform
	/// nobody reads between changes.
	/// </summary>
	bool _descendantCached;

	bool HasCachedWorld => _worldCached.HasValue || _worldInterpCached.HasValue;

	void ListenersChanged( bool had )
	{
		var has = HasListeners;
		if ( had != has )
			AddListenersToHierarchy( GameObject, has ? 1 : -1 );
	}

	static void AddListenersToHierarchy( GameObject go, int count )
	{
		for ( ; go is not null; go = go.Parent )
		{
			go.Transform._listenersInHierarchy += count;
		}
	}

	/// <summary>
	/// Our cached world transform came from our ancestors, so changes to any of them must reach us.
	/// Every marked transform's ancestors are marked too, so we can stop at the first one that is.
	/// </summary>
	void MarkCachedInAncestors()
	{
		for ( var go = GameObject.Parent; go is not null; go = go.Parent )
		{
			var transform = go.Transform;
			if ( transform._descendantCached )
				return;

			transform._descendantCached = true;
		}
	}

	/// <summary>
	/// Called before our GameObject leaves its parent. Its listeners stop counting towards the old hierarchy.
	/// </summary>
	internal void OnDetachingFromParent()
	{
		if ( _listenersInHierarchy != 0 )
			AddListenersToHierarchy( GameObject.Parent, -_listenersInHierarchy );
	}

	/// <summary>
	/// Called once our GameObject has a new parent. Our listeners and caches count towards the new hierarchy.
	/// </summary>
	internal void OnAttachedToParent()
	{
		if ( _listenersInHierarchy != 0 )
			AddListenersToHierarchy( GameObject.Parent, _listenersInHierarchy );

		if ( _descendantCached || HasCachedWorld )
			MarkCachedInAncestors();
	}

	/// <summary>
	/// Our transform has changed, which means our children transforms changed too
	/// tell them all.
	/// </summary>
	internal unsafe void TransformChanged( bool useTargetLocal = false, GameTransform root = null )
	{
		root ??= this;

		_worldCached = default;
		_worldInterpCached = default;

		// Whatever is cached below gets cleared on the way down. Anything cached again from now on,
		// like by the callbacks, marks us again.
		var descendantCached = _descendantCached;
		_descendantCached = false;

		// Most child bones have no listeners, skip the callback setup for them
		if ( HasListeners )
		{
			InsideChangeCallback = useTargetLocal;
			var notified = false;

			try
			{
				_onTransformChanged?.Invoke();
				_onTransformChangedInternal?.Invoke( root );
				notified = true;
			}
			finally
			{
				InsideChangeCallback = false;

				// A throwing listener ends the change here, before anything below is cleared - keep it reachable
				if ( !notified && descendantCached )
				{
					_descendantCached = true;
					MarkCachedInAncestors();
				}
			}
		}

		// Nobody below is listening or has a world transform from before the change
		if ( !descendantCached && _listenersInHierarchy <= (HasListeners ? 1 : 0) )
			return;

		var data = new TransformChangedData { Root = root };
		GameObject.ForEachChildFast( "TransformChanged", true, &TransformChangedCallback, ref data );
	}

	struct TransformChangedData
	{
		public GameTransform Root;
	}

	static void TransformChangedCallback( GameObject c, ref TransformChangedData data )
	{
		var transform = c.Transform;

		if ( !transform.IsFollowingParent() )
		{
			// Not affected now, but what's cached in there stays reachable for if it follows us again
			if ( transform._descendantCached || transform.HasCachedWorld )
				transform.MarkCachedInAncestors();

			return;
		}

		if ( transform._listenersInHierarchy == 0 && !transform._descendantCached && !transform.HasCachedWorld )
			return;

		transform.TransformChanged( false, data.Root );
	}
}
