using Sandbox;

namespace NetworkTests;

/// <summary>
/// Tests input history retention and command wraparound.
/// </summary>
[TestClass]
public class PredictionHistoryTest
{
	/// <summary>
	/// History handles command wraparound and rejects stale inputs.
	/// </summary>
	[TestMethod]
	public void CommandsWrapAndRejectOldOrDuplicateInputs()
	{
		var history = new PredictionHistory<int>( 4 );

		Assert.IsTrue( history.Record( uint.MaxValue, 1 ) );
		Assert.IsTrue( history.Record( 0, 2 ) );
		Assert.IsFalse( history.Record( 0, 2 ) );
		Assert.IsFalse( history.Record( uint.MaxValue, 1 ) );
		Assert.IsTrue( history.Record( 1, 3 ) );
		Assert.IsTrue( history.DiscardThrough( uint.MaxValue ) );
		Assert.IsTrue( history.TryGetInput( 0, out var input ) );
		Assert.AreEqual( 2, input );

		history.Clear();
		Assert.AreEqual( 0, history.Count );
		Assert.IsTrue( history.Record( 1, 1 ) );
	}

	/// <summary>
	/// Acknowledgements retain only the inputs needed for replay.
	/// </summary>
	[TestMethod]
	public void CoherentAcknowledgementRetainsTypedReplayInputs()
	{
		var history = new PredictionHistory<int>( 4 );
		history.Record( uint.MaxValue, 1 );
		history.Record( 0, 2 );
		history.Record( 1, 3 );

		Assert.IsTrue( history.DiscardThrough( uint.MaxValue ) );
		Assert.AreEqual( 2, history.Count );
		Assert.IsFalse( history.TryGetInput( uint.MaxValue, out _ ) );
		Assert.IsTrue( history.TryGetInput( 0, out var input ) );
		Assert.AreEqual( 2, input );
		Assert.IsFalse( history.DiscardThrough( uint.MaxValue ) );
		Assert.AreEqual( 2, history.Count );
		Assert.IsTrue( history.DiscardThrough( 1 ) );
		Assert.AreEqual( 0, history.Count );
		Assert.IsFalse( history.Record( 1, 3 ) );
	}
}
