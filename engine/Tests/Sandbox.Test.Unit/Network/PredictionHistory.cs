using Sandbox;

namespace NetworkTests;

[TestClass]
public class PredictionHistoryTest
{
	[TestMethod]
	public void CompletedContactResponseIsRetainedForReplay()
	{
		var history = new PredictionHistory<int, int>( 4 );
		history.Record( 1, 2, 2 );
		history.Record( 2, 3, 5 );
		Assert.IsFalse( history.UpdateLatest( 1, 100, 100 ) );
		Assert.IsTrue( history.UpdateLatest( 2, 7, 9 ) );
		Assert.IsTrue( history.TryGetState( 2, out var completedState ) );
		Assert.AreEqual( 9, completedState );
		Assert.IsTrue( history.Reconcile( 1, 10, ( state, input ) => state + input, out var result ) );
		Assert.AreEqual( 17, result );
		Assert.IsTrue( history.Reconcile( 2, 17, ( state, input ) => state + input, out _ ) );
		Assert.IsFalse( history.UpdateLatest( 2, 100, 100 ) );
	}

	[TestMethod]
	public void InputBatchesIncludeGapRecoveryAndRecentCommands()
	{
		var history = new PredictionHistory<int, int>( 16 );
		for ( uint command = 1; command <= 10; command++ ) history.Record( command, (int)command, 0 );
		CollectionAssert.AreEqual( new[] { 1, 2, 9, 10 }, history.GetInputBatch( 4 ) );
		Assert.IsTrue( history.Reconcile( 8, 0, ( state, input ) => state, out _ ) );
		CollectionAssert.AreEqual( new[] { 9, 10 }, history.GetInputBatch( 4 ) );
	}

	[TestMethod]
	public void CorrectionReplaysOnlyUnacknowledgedInputs()
	{
		var history = new PredictionHistory<int, int>( 4 );
		history.Record( 1, 2, 2 );
		history.Record( 2, 3, 5 );
		history.Record( 3, 4, 9 );
		int calls = 0;
		Assert.IsTrue( history.Reconcile( 1, 10, ( state, input ) => { calls++; return state + input; }, out var result ) );
		Assert.AreEqual( 17, result );
		Assert.AreEqual( 2, calls );
		Assert.AreEqual( 2, history.Count );
		Assert.IsTrue( history.TryGetState( 2, out var replayedState ) );
		Assert.AreEqual( 13, replayedState );
		Assert.IsFalse( history.TryGetState( 1, out _ ) );
		Assert.IsFalse( history.Reconcile( 1, 0, ( state, input ) => state + input, out _ ) );
		Assert.IsTrue( history.Reconcile( 3, 18, ( state, input ) => throw new System.InvalidOperationException( "No inputs should be replayed" ), out result ) );
		Assert.AreEqual( 18, result );
		Assert.AreEqual( 0, history.Count );
		Assert.IsFalse( history.Record( 3, 4, 18 ) );
	}

	[TestMethod]
	public void OverflowRequiresResynchronizationForEvictedCommands()
	{
		var history = new PredictionHistory<int, int>( 2 );
		for ( uint command = 1; command <= 3; command++ )
			history.Record( command, 1, (int)command );
		Assert.IsFalse( history.Reconcile( 1, 10, ( state, input ) => state + input, out _ ) );
		Assert.AreEqual( 2, history.Count );
		Assert.IsTrue( history.Reconcile( 2, 10, ( state, input ) => state + input, out var result ) );
		Assert.AreEqual( 11, result );
	}

	[TestMethod]
	public void CommandsWrapAndRejectOldOrDuplicateInputs()
	{
		var history = new PredictionHistory<int, int>( 4 );
		Assert.IsTrue( history.Record( uint.MaxValue, 1, 1 ) );
		Assert.IsTrue( history.Record( 0, 2, 3 ) );
		Assert.IsFalse( history.Record( 0, 2, 3 ) );
		Assert.IsFalse( history.Record( uint.MaxValue, 1, 1 ) );
		Assert.IsTrue( history.Record( 1, 3, 6 ) );
		Assert.IsTrue( history.Reconcile( uint.MaxValue, 10, ( state, input ) => state + input, out var result ) );
		Assert.AreEqual( 15, result );
		history.Clear();
		Assert.AreEqual( 0, history.Count );
		Assert.IsTrue( history.Record( 1, 1, 1 ) );
	}
}
