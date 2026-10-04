namespace SceneTests.Core;

[TestClass]
[DoNotParallelize]
public class ScenePushScopeTest : SceneTest
{
	[TestMethod]
	public void ConcreteScopeDoesNotAllocateForCurrentScene()
	{
		var scene = new Scene();
		try
		{
			using var outer = new ScenePushScope( scene );
			for ( int i = 0; i < 10; i++ )
			{
				using var warmup = new ScenePushScope( scene );
			}

			var before = System.GC.GetAllocatedBytesForCurrentThread();
			for ( int i = 0; i < 1000; i++ )
			{
				using var scope = new ScenePushScope( scene );
			}
			var allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
			Assert.AreEqual( 0L, allocated );
		}
		finally
		{
			scene.Destroy();
		}
	}

	[TestMethod]
	public void NestedScopesRestoreSceneAndClock()
	{
		var first = new Scene();
		var second = new Scene();
		var previousScene = Game.ActiveScene;
		var previousNowDouble = Time.NowDouble;
		var previousNow = Time.Now;
		var previousDelta = Time.Delta;
		try
		{
			using ( new ScenePushScope( first ) )
			{
				Assert.AreSame( first, Game.ActiveScene );
				Time.NowDouble = 12.75;
				Time.Now = 12.75f;
				Time.Delta = 0.25f;

				using ( new ScenePushScope( second ) )
				{
					Assert.AreSame( second, Game.ActiveScene );
					Assert.AreEqual( second.TimeNow, Time.NowDouble );
				}

				Assert.AreSame( first, Game.ActiveScene );
				Assert.AreEqual( 12.75, Time.NowDouble );
				Assert.AreEqual( 12.75f, Time.Now );
				Assert.AreEqual( 0.25f, Time.Delta );
			}

			Assert.AreSame( previousScene, Game.ActiveScene );
			Assert.AreEqual( previousNowDouble, Time.NowDouble );
			Assert.AreEqual( previousNow, Time.Now );
			Assert.AreEqual( previousDelta, Time.Delta );
		}
		finally
		{
			first.Destroy();
			second.Destroy();
		}
	}

	[TestMethod]
	public void DefaultScopeDoesNotChangeSceneOrClock()
	{
		var scene = Game.ActiveScene;
		var now = Time.NowDouble;
		var scope = default( ScenePushScope );
		scope.Dispose();
		Assert.AreSame( scene, Game.ActiveScene );
		Assert.AreEqual( now, Time.NowDouble );
	}
}
