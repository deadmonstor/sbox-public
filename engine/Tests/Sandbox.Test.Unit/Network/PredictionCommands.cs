using Sandbox;
using System;

namespace NetworkTests;

/// <summary>
/// Tests command ordering, host credit, and replay effects.
/// </summary>
[TestClass]
public class PredictionCommandsTest
{
	/// <summary>
	/// Reordering, duplicates, and gaps cannot skip commands.
	/// </summary>
	[TestMethod]
	public void ReorderedDuplicatesAndLostGapsNeverSkipExecution()
	{
		var queue = new PredictionCommandQueue<int>( 8 );
		queue.AddCredit( 0.02f );

		Assert.IsTrue( queue.Enqueue( 2, 20 ) );
		Assert.IsFalse( queue.Enqueue( 2, 200 ) );
		Assert.IsFalse( queue.TryTake( 0.02f, out _, out _ ) );
		Assert.IsTrue( queue.Enqueue( 1, 10 ) );
		Assert.IsTrue( queue.TryTake( 0.02f, out var command, out var value ) );
		Assert.AreEqual( 1u, command );
		Assert.AreEqual( 10, value );
		Assert.IsFalse( queue.Enqueue( 1, 100 ) );
		Assert.IsFalse( queue.TryTake( 0.02f, out _, out _ ) );

		queue.AddCredit( 0.02f );
		Assert.IsTrue( queue.TryTake( 0.02f, out command, out value ) );
		Assert.AreEqual( 2u, command );
		Assert.AreEqual( 20, value );
		Assert.IsFalse( queue.TryTake( 0.02f, out _, out _ ) );
	}

	/// <summary>
	/// Command wraparound preserves queue capacity and ordering.
	/// </summary>
	[TestMethod]
	public void WraparoundAndCapacityRemainBounded()
	{
		var queue = new PredictionCommandQueue<int>( 4 );
		queue.Reset( uint.MaxValue - 1 );

		Assert.IsTrue( queue.Enqueue( 0, 2 ) );
		Assert.IsTrue( queue.Enqueue( uint.MaxValue, 1 ) );
		Assert.IsTrue( queue.Enqueue( 1, 3 ) );
		Assert.IsFalse( queue.Enqueue( 3, 5 ) );
		Assert.IsFalse( queue.Enqueue( uint.MaxValue - 1, 0 ) );

		for ( var i = 0; i < 3; i++ )
		{
			queue.AddCredit( 0.02f );
		}

		Assert.IsTrue( queue.TryTake( 0.02f, out var command, out var value ) );
		Assert.AreEqual( uint.MaxValue, command );
		Assert.AreEqual( 1, value );
		Assert.IsTrue( queue.TryTake( 0.02f, out command, out value ) );
		Assert.AreEqual( 0u, command );
		Assert.AreEqual( 2, value );
		Assert.IsTrue( queue.TryTake( 0.02f, out command, out value ) );
		Assert.AreEqual( 1u, command );
		Assert.AreEqual( 3, value );

		queue.Reset();
		Assert.IsFalse( queue.TryTake( 0.02f, out _, out _ ) );
	}

	/// <summary>
	/// Idle host credit permits only bounded catch-up.
	/// </summary>
	[TestMethod]
	public void IdleHostCreditCannotProduceUnboundedCatchUp()
	{
		var queue = new PredictionCommandQueue<int>( 16 );

		for ( var i = 0; i < 1000; i++ )
		{
			queue.AddCredit( 1 );
		}

		for ( uint command = 1; command <= 16; command++ )
		{
			Assert.IsTrue( queue.Enqueue( command, (int)command ) );
		}

		for ( var i = 0; i < 8; i++ )
		{
			Assert.IsTrue( queue.TryTake( 1, out _, out _ ) );
		}

		Assert.IsFalse( queue.TryTake( 1, out _, out _ ) );

		queue.AddCredit( 1 );
		Assert.IsTrue( queue.TryTake( 1, out var next, out _ ) );
		Assert.AreEqual( 9u, next );
	}

	sealed class Replayable : PredictionParticipantData<int, int>
	{
		internal override Component Component => null;

		internal override int Order => 0;

		/// <summary>
		/// Accumulated simulated input.
		/// </summary>
		public int Value;

		/// <summary>
		/// Number of first-pass effects.
		/// </summary>
		public int Effects;

		/// <summary>
		/// Number of replay simulations.
		/// </summary>
		public int Replays;

		protected override int CaptureInput() => 2;

		protected override int CaptureState() => Value;

		protected override void RestoreState( in int state ) => Value = state;

		protected override void Simulate( in int input, in PredictionContext context )
		{
			Value += input;

			if ( context.IsReplay )
			{
				Replays++;
			}
		}

		protected override void AfterSimulate( in int input, in PredictionContext context ) => Effects++;
	}

	/// <summary>
	/// Replay reuses captured input without repeating first-pass effects.
	/// </summary>
	[TestMethod]
	public void ExplicitReplayReusesTypedInputAndNeverRepeatsAfterSimulate()
	{
		var participant = new Replayable();
		var context = new PredictionContext( Guid.NewGuid(), Guid.NewGuid(), 1, 0.02f, PredictionPhase.Predict, 17 );

		participant.Capture( 1 );
		participant.Execute( context );
		Assert.AreEqual( 0, participant.Effects, "Effects are deferred until all participants have simulated." );

		participant.AfterExecute( context );

		var replay = new PredictionContext( context.ObjectId, context.Epoch, 1, context.Delta, PredictionPhase.Replay, context.Seed );
		participant.Execute( replay );
		participant.AfterExecute( replay );

		Assert.AreEqual( 4, participant.Value );
		Assert.AreEqual( 1, participant.Replays );
		Assert.AreEqual( 1, participant.Effects );

		participant.Reset();
		Assert.ThrowsException<InvalidOperationException>( () => participant.Execute( replay ), "A reset must discard the old epoch's input." );
	}
}
