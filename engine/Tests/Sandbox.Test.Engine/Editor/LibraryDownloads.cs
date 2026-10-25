using Editor;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EditorTests;

[TestClass]
public class LibraryDownloadsTest
{
	static ManifestSchema.File File( string path, long size ) => new() { Path = path, Size = size };

	/// <summary>
	/// Installing compiles the library as soon as the downloads return, so they must not return - even when
	/// cancelled while waiting for room to start the next file - while a started download is still writing.
	/// </summary>
	[TestMethod]
	public async Task CancellingWhileThrottledWaitsForStartedDownloads()
	{
		var big = new TaskCompletionSource<bool>();
		var started = new List<string>();
		using var cancel = new CancellationTokenSource();

		var downloads = LibrarySystem.DownloadFiles( [File( "big.cs", 100_000 ), File( "small.cs", 10 )], file =>
		{
			started.Add( file.Path );
			return file.Path == "big.cs" ? big.Task : Task.FromResult( true );
		}, cancel.Token );

		await Task.Delay( 50 );
		CollectionAssert.AreEqual( new[] { "big.cs" }, started, "the second file should wait for room" );

		cancel.Cancel();
		await Task.Delay( 50 );
		Assert.IsFalse( downloads.IsCompleted, "returned while big.cs was still downloading" );

		big.SetResult( true );
		await Assert.ThrowsExceptionAsync<TaskCanceledException>( () => downloads );
		CollectionAssert.AreEqual( new[] { "big.cs" }, started );
	}

	[TestMethod]
	public async Task AnyFailedDownloadFailsTheInstall()
	{
		var result = await LibrarySystem.DownloadFiles( [File( "a.cs", 10 ), File( "b.cs", 10 ), File( "c.cs", 10 )], file => file.Path switch
		{
			"a.cs" => Task.FromResult( true ),
			"b.cs" => Task.FromResult( false ),
			_ => throw new InvalidOperationException( "no connection" ),
		}, CancellationToken.None );

		Assert.IsFalse( result );
	}
}
