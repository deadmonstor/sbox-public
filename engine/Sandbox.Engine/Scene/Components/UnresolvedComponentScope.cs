namespace Sandbox;

/// <summary>
/// Records the components that <see cref="Component.Serialize"/> leaves out because their type isn't in
/// <see cref="Game.TypeLibrary"/>, which happens while game assemblies are unloaded or being reloaded.
/// Anything that stores serialized scene data and restores or writes it later must check this, or it
/// will silently delete those components.
/// </summary>
internal sealed class UnresolvedComponentScope : IDisposable
{
	[ThreadStatic]
	private static UnresolvedComponentScope _current;

	private readonly UnresolvedComponentScope _previous;
	private List<Component> _components;

	private UnresolvedComponentScope( UnresolvedComponentScope previous )
	{
		_previous = previous;
	}

	/// <summary>
	/// Start recording. Scopes nest; an inner scope's components are also reported to the outer one.
	/// </summary>
	public static UnresolvedComponentScope Begin()
	{
		return _current = new UnresolvedComponentScope( _current );
	}

	/// <summary>
	/// Components that were left out of the serialized output.
	/// </summary>
	public IReadOnlyList<Component> Components => _components is null ? [] : _components;

	/// <summary>
	/// True if the serialized output is missing at least one component.
	/// </summary>
	public bool Any => _components is { Count: > 0 };

	/// <summary>
	/// A short description of the missing components, for error messages.
	/// </summary>
	public string Describe()
	{
		return string.Join( ", ", Components.Select( x => $"{x.GetType().FullName} on {x.GameObject?.Name}" ) );
	}

	internal static void Report( Component component )
	{
		var scope = _current;
		if ( scope is null ) return;

		scope._components ??= new();
		scope._components.Add( component );
	}

	public void Dispose()
	{
		_current = _previous;

		if ( _previous is not null && _components is not null )
		{
			_previous._components ??= new();
			_previous._components.AddRange( _components );
		}
	}
}
