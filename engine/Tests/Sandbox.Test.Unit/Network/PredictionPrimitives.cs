using Sandbox;
using System;

namespace NetworkTests;

/// <summary>
/// Tests prediction clocks, deterministic randomness, and event confirmation.
/// </summary>
[TestClass]
public class PredictionPrimitivesTest
{
	/// <summary>
	/// Burst execution advances time without counting latency twice.
	/// </summary>
	[TestMethod]
	public void ClockMapsBurstExecutionWithoutDoubleCountingLatencyOrInterpolation()
	{
		var clock = new PredictionClock();
		clock.Reset( 0.02f );

		Assert.AreEqual( 9.9, clock.Execute( 1, 10, 0.1f, 0.5f ), 0.000001 );
		Assert.AreEqual( 9.92, clock.Execute( 2, 10, 0.4f, 0.5f ), 0.000001 );
		Assert.AreEqual( 9.94, clock.Execute( 3, 10, 0, 0.5f ), 0.000001 );

		clock.Reset( 0.02f );
		Assert.AreEqual( 19.5, clock.Execute( uint.MaxValue, 20, 100, 0.5f ), 0.000001 );
		Assert.AreEqual( 19.52, clock.Execute( 0, 20, 0.5f, 0.5f ), 0.000001 );
	}

	/// <summary>
	/// New commands realign to the observed host delay after a stall.
	/// </summary>
	[TestMethod]
	public void ClientStallRealignsNewCommandsToObservedHostDelay()
	{
		var clock = new PredictionClock();
		clock.Reset( 0.02f );

		Assert.AreEqual( 9.9, clock.Execute( 1, 10, 0.1f, 0.5f ), 0.000001 );

		// New input realigns after a stall; jitter still advances by fixed ticks.
		Assert.AreEqual( 19.9, clock.Execute( 2, 20, 0.1f, 0.5f ), 0.000001 );
		Assert.AreEqual( 19.92, clock.Execute( 3, 20, 0.1f, 0.5f ), 0.000001 );
		Assert.AreEqual( 19.94, clock.Execute( 4, 20, 0.3f, 0.5f ), 0.000001 );
	}

	/// <summary>
	/// Random samples distinguish actions and commands within the unit range.
	/// </summary>
	[TestMethod]
	public void RandomSamplesSeparateActionsAndCommandsWithinUnitRange()
	{
		var objectId = Guid.NewGuid();
		var epoch = Guid.NewGuid();
		var first = new PredictionContext( objectId, epoch, 17, 0.02f, PredictionPhase.Predict, 123 );
		var next = new PredictionContext( objectId, epoch, 18, 0.02f, PredictionPhase.Authority, 123 );

		for ( uint i = 0; i < 16; i++ )
		{
			Assert.IsTrue( first.Random( 1, i ) >= 0 && first.Random( 1, i ) < 1 );
			Assert.AreNotEqual( first.Random( 1, i ), first.Random( 2, i ) );
			Assert.AreNotEqual( first.Random( 1, i ), next.Random( 1, i ) );
		}

		Assert.AreNotEqual( first.Event( 1 ), next.Event( 1 ) );
	}

	/// <summary>
	/// Confirmations and rejections present each effect only once.
	/// </summary>
	[TestMethod]
	public void ConfirmationDoesNotRepeatPredictedEffectsAndRejectionOccursOnce()
	{
		var ledger = new PredictionEvents( 4 );
		var objectId = Guid.NewGuid();
		var epoch = Guid.NewGuid();
		ledger.Reset( objectId, epoch );

		var first = new PredictionEventId( objectId, epoch, 1, 0 );
		Assert.IsTrue( ledger.Predict( first ) );
		Assert.IsFalse( ledger.Predict( first ) );
		Assert.AreEqual( PredictionEventConfirmation.AlreadyPredicted, ledger.Confirm( first, true ) );
		Assert.AreEqual( PredictionEventConfirmation.Ignored, ledger.Confirm( first, true ) );

		var rejected = first with { Command = 2 };
		Assert.IsTrue( ledger.Predict( rejected ) );
		Assert.AreEqual( PredictionEventConfirmation.Rejected, ledger.Confirm( rejected, false ) );
		Assert.AreEqual( PredictionEventConfirmation.Ignored, ledger.Confirm( rejected, false ) );
		Assert.AreEqual( PredictionEventConfirmation.Accepted, ledger.Confirm( first with { Command = 3 }, true ) );
		Assert.IsFalse( ledger.Predict( first with { Command = 3 } ) );
	}

	/// <summary>
	/// Eviction and lifetime changes prevent stale effects from presenting again.
	/// </summary>
	[TestMethod]
	public void EvictedEffectsAndPriorLifetimesCannotPresentAgainIncludingWraparound()
	{
		var ledger = new PredictionEvents( 2 );
		var objectId = Guid.NewGuid();
		var epoch = Guid.NewGuid();
		ledger.Reset( objectId, epoch );

		var first = new PredictionEventId( objectId, epoch, uint.MaxValue, 0 );
		Assert.IsTrue( ledger.Predict( first ) );
		Assert.IsTrue( ledger.Predict( first with { Command = 0 } ) );
		Assert.IsTrue( ledger.Predict( first with { Command = 1 } ) );
		Assert.AreEqual( PredictionEventConfirmation.Ignored, ledger.Confirm( first, true ) );
		Assert.IsFalse( ledger.Predict( first ) );

		var nextEpoch = Guid.NewGuid();
		ledger.Reset( objectId, nextEpoch );
		Assert.AreEqual( PredictionEventConfirmation.Ignored, ledger.Confirm( first with { Command = 1 }, true ) );
		Assert.IsTrue( ledger.Predict( first with { Epoch = nextEpoch, Command = 1 } ) );
		Assert.IsFalse( ledger.Predict( first with { Object = Guid.NewGuid(), Epoch = nextEpoch, Command = 2 } ) );
	}
}
