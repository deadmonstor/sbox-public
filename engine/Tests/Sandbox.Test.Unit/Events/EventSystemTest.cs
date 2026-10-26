using Sandbox.Internal;
using System;

namespace EventTests;

/// <summary>
/// Tests for <see cref="EventSystem"/>.
/// </summary>
[TestClass]
public class EventSystemTest
{
	private static EventSystem CreateEventSystem() => new EventSystem();

	/// <summary>
	/// Events must be dispatched to handlers registered with <see cref="EventSystem.Register"/>.
	/// </summary>
	[TestMethod]
	public void Registered()
	{
		using var system = CreateEventSystem();

		var handler = new EventHandler();

		system.Register( handler );

		Assert.IsFalse( handler.Handled );

		system.RunInterface<IEventInterface>( x => x.EventMethod() );

		Assert.IsTrue( handler.Handled );
	}

	/// <summary>
	/// Events must <i>only</i> be dispatched to handlers registered with <see cref="EventSystem.Register"/>.
	/// </summary>
	[TestMethod]
	public void NotRegistered()
	{
		using var system = CreateEventSystem();

		var handler = new EventHandler();

		Assert.IsFalse( handler.Handled );

		system.RunInterface<IEventInterface>( x => x.EventMethod() );

		Assert.IsFalse( handler.Handled );
	}

	/// <summary>
	/// Events must <i>not</i> be dispatched to handlers unregistered with <see cref="EventSystem.Unregister"/>.
	/// </summary>
	[TestMethod]
	public void Unregistered()
	{
		using var system = CreateEventSystem();

		var handler = new EventHandler();

		system.Register( handler );
		system.Unregister( handler );

		Assert.IsFalse( handler.Handled );

		system.RunInterface<IEventInterface>( x => x.EventMethod() );

		Assert.IsFalse( handler.Handled );
	}

	private static WeakReference<EventHandler> RegisterHandlerAndReturnWeakReference( EventSystem system )
	{
		var handler = new EventHandler();

		system.Register( handler );

		return new WeakReference<EventHandler>( handler );
	}

	/// <summary>
	/// Registered handlers must be garbage collectable if not referenced elsewhere.
	/// </summary>
	[TestMethod]
	public async Task AllowCollection()
	{
		using var system = CreateEventSystem();

		var weakRef = RegisterHandlerAndReturnWeakReference( system );

		// Weak ref to something that definitely isn't referenced anywhere else,
		// if this doesn't get collected either then we know something else is wrong.

		var canary = new WeakReference<object>( new object() );

		const int maxAttempts = 100;

		var attempts = 0;

		while ( weakRef.TryGetTarget( out _ ) )
		{
			if ( attempts++ >= maxAttempts )
			{
				if ( canary.TryGetTarget( out _ ) )
				{
					Assert.Inconclusive( "No garbage collections were actually happening." );
				}
				else
				{
					Assert.Fail( "Handler wasn't garbage collected." );
				}
			}

			await Task.Delay( 1 );

			GC.Collect();
		}

		// James: Gets collected after the first attempt when I test locally in Debug

		Console.WriteLine( $"Collected after {attempts} attempt(s)" );
	}

	[TestMethod]
	public void RepeatedRegistrationInvokesEachInstanceOnce()
	{
		using var system = CreateEventSystem();
		var assembly = typeof( AttributedHandler ).Assembly;
		system.RegisterAssembly( assembly );
		var first = new AttributedHandler();
		var second = new AttributedHandler();

		system.Register( first );
		system.Register( first );
		system.Register( second );
		system.Run( "test.registration" );
		Assert.AreEqual( 1, first.Calls );
		Assert.AreEqual( 1, second.Calls );

		system.UnregisterAssembly( assembly );
		system.RegisterAssembly( assembly );
		system.Run( "test.registration" );
		Assert.AreEqual( 2, first.Calls );
		Assert.AreEqual( 2, second.Calls );

		system.Unregister( first );
		system.Run( "test.registration" );
		Assert.AreEqual( 2, first.Calls );
		Assert.AreEqual( 3, second.Calls );
	}

	private sealed class AttributedHandler
	{
		public int Calls { get; private set; }

		[Event( "test.registration" )]
		void OnEvent() => Calls++;
	}

	private interface IEventInterface
	{
		void EventMethod();
	}

	private sealed class EventHandler : IEventInterface
	{
		public bool Handled { get; private set; }

		public void EventMethod()
		{
			Handled = true;
		}
	}
}
