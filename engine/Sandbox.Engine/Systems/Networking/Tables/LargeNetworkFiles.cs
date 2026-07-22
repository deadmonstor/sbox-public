using Sandbox.Internal;
using Sandbox.Network;
using System.Collections.Concurrent;
using System.Threading;

namespace Sandbox;

internal class LargeNetworkFiles
{
	public BaseFileSystem Files { get; private set; }
	public StringTable StringTable { get; init; }

	record struct LargeFileInfo( long Size, ulong CRC );

	RedirectFileSystem RedirectFileSystem { get; set; }

	HashSet<string> downloadQueue = new();

	Dictionary<Guid, BatchState> pendingBatches = new();

	public LargeNetworkFiles( string name )
	{
		StringTable = new( name, true );
		StringTable.OnChangeOrAdd += OnTableEntryUpdated;
		StringTable.OnRemoved += OnTableEntryRemoved;
		StringTable.OnSnapshot += OnTableSnapshot;
	}

	/// <summary>
	/// Reset the string table.
	/// </summary>
	public void Reset()
	{
		StringTable.Reset();

		Files?.Dispose();
		RedirectFileSystem = AssetDownloadCache.CreateRedirectFileSystem();
		Files = new BaseFileSystem( RedirectFileSystem );
	}

	/// <summary>
	/// Add all files from the network.
	/// </summary>
	public void Refresh()
	{
		foreach ( var (_, entry) in StringTable.Entries )
		{
			AddFileToFileSystem( entry.Name, entry.Read<LargeFileInfo>() );
		}
	}

	/// <summary>
	/// Add a file to be networked.
	/// </summary>
	public bool AddFile( string fileName )
	{
		if ( !EngineFileSystem.Mounted.FileExists( fileName ) )
			return false;

		var crc = EngineFileSystem.Mounted.GetCrc( fileName );
		var size = EngineFileSystem.Mounted.FileSize( fileName );
		var normalizedFileName = NormalizeFileName( fileName );
		StringTable.Set( normalizedFileName, new LargeFileInfo( size, crc ) );

		return true;
	}

	/// <summary>
	/// Remove a networked file.
	/// </summary>
	public void RemoveFile( string fileName )
	{
		var normalizedFileName = NormalizeFileName( fileName );
		StringTable.Remove( normalizedFileName );
	}

	string NormalizeFileName( string fileName )
	{
		return BaseFileSystem.NormalizeFilename( fileName ).TrimStart( '/' );
	}

	void OnTableEntryUpdated( StringTable.Entry entry )
	{
		AddFileToFileSystem( entry.Name, entry.Read<LargeFileInfo>() );
	}

	void OnTableEntryRemoved( StringTable.Entry entry )
	{

	}

	void OnTableSnapshot()
	{
		Log.Info( "Checking for network files.." );
		var sw = System.Diagnostics.Stopwatch.StartNew();

		Refresh();

		Log.Info( $"..done in {sw.Elapsed.TotalSeconds:0.00}s" );
	}

	void AddFileToFileSystem( string fileName, LargeFileInfo contents )
	{
		if ( EngineFileSystem.Mounted.FileExists( fileName ) )
		{
			var size = EngineFileSystem.Mounted.FileSize( fileName );
			if ( size == contents.Size )
			{
				var crc = EngineFileSystem.Mounted.GetCrc( fileName );
				if ( crc == contents.CRC )
				{
					if ( AssetDownloadCache.DebugNetworkFiles )
					{
						Log.Info( $"Skipping downloading {fileName} - we already have it" );
					}

					return;
				}
			}
		}

		if ( !AssetDownloadCache.IsLegalDownload( fileName ) )
			return;

		if ( AssetDownloadCache.TryMount( RedirectFileSystem, fileName, contents.CRC ) )
			return;

		if ( AssetDownloadCache.DebugNetworkFiles )
		{
			Log.Info( $"Queued Network File: {fileName} / {contents.Size} / {contents.CRC}" );
		}

		downloadQueue.Add( fileName );
	}

	public async Task RunDownloadQueue( NetworkSystem system, CancellationToken token )
	{
		if ( RedirectFileSystem is null )
			return;

		system.AddHandler<FileChunk>( OnFileChunk );

		Assert.NotNull( Connection.Host );

		var toFetch = new List<string>();
		foreach ( var file in downloadQueue )
		{
			if ( !StringTable.Entries.ContainsKey( file ) )
				continue;

			if ( RedirectFileSystem.FileExists( file.NormalizeFilename( true ) ) )
				continue;

			toFetch.Add( file );
		}

		if ( toFetch.Count == 0 )
		{
			downloadQueue.Clear();
			return;
		}

		Log.Info( $"Downloading {toFetch.Count} files.." );
		var sw = System.Diagnostics.Stopwatch.StartNew();

		var batchId = Guid.NewGuid();
		var state = new BatchState { Total = toFetch.Count };
		pendingBatches[batchId] = state;

		try
		{
			using ( token.Register( () => state.Tcs.TrySetCanceled() ) )
			{
				if ( Connection.Host is null )
					throw new TaskCanceledException( "Connection became null" );

				Connection.Host.SendMessage( new RequestFiles { batchId = batchId, filenames = toFetch.ToArray() }, NetFlags.Reliable );
				LoadingScreen.Title = $"Downloading Files (0/{state.Total})";

				await state.Tcs.Task;
			}
		}
		finally
		{
			pendingBatches.Remove( batchId );
		}

		var stores = new List<Task<(string filename, string absPath)>>();
		while ( state.Chunks.TryDequeue( out var chunk ) )
		{
			if ( !StringTable.Entries.TryGetValue( chunk.filename, out var entry ) )
				continue;

			var filename = chunk.filename;
			var data = chunk.data;
			var crc = entry.Read<LargeFileInfo>().CRC;
			stores.Add( Task.Run( () => (filename, AssetDownloadCache.StoreFile( filename, crc, data )) ) );
		}

		foreach ( var (filename, absPath) in await Task.WhenAll( stores ) )
		{
			if ( absPath is not null )
				RedirectFileSystem.AddAbsFile( filename, absPath );
		}

		LoadingScreen.Subtitle = null;
		Log.Info( $"Download Complete ({state.Received} files total) ({sw.Elapsed.TotalSeconds:0.00}s)" );

		downloadQueue.Clear();
	}

	internal void NetworkInitialize( GameNetworkSystem instance )
	{
		instance.AddHandler<RequestFiles>( OnRequestNetworkFiles );
	}

	void OnFileChunk( FileChunk chunk, Connection connection, Guid msgGuid )
	{
		if ( Networking.IsHost )
			return;

		if ( !pendingBatches.TryGetValue( chunk.batchId, out var state ) )
			return;

		state.Received++;
		LoadingScreen.Title = $"Downloading Files ({state.Received}/{state.Total})";
		LoadingScreen.Subtitle = chunk.filename;

		if ( chunk.data is null || chunk.data.Length == 0 )
			Log.Warning( $"Failed to download file {chunk.filename}!" );
		else
			state.Chunks.Enqueue( chunk );

		if ( state.Received >= state.Total )
			state.Tcs.TrySetResult();
	}

	async Task OnRequestNetworkFiles( RequestFiles request, Connection connection, Guid msgGuid )
	{
		if ( !Networking.IsHost )
			return;

		int total = request.filenames.Length;

		for ( int i = 0; i < total; i++ )
		{
			var filename = request.filenames[i];
			byte[] contents;

			try
			{
				if ( !EngineFileSystem.Mounted.FileExists( filename ) )
				{
					Log.Warning( $"Client ({connection.Name}) requested missing file: {filename}" );
					contents = [];
				}
				else
				{
					contents = await EngineFileSystem.Mounted.ReadAllBytesAsync( filename );
				}
			}
			catch ( Exception e )
			{
				Log.Warning( $"Failed to read requested file {filename} for client ({connection.Name}): {e.Message}" );
				contents = [];
			}

			connection.SendMessage( new FileChunk( request.batchId, filename, contents ), NetFlags.Reliable );
		}
	}

	[Expose]
	public record struct RequestFiles( Guid batchId, string[] filenames );

	[Expose]
	public record struct FileChunk( Guid batchId, string filename, byte[] data );

	class BatchState
	{
		public TaskCompletionSource Tcs { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );
		public int Total;
		public int Received;
		public ConcurrentQueue<FileChunk> Chunks { get; } = new();
	}
}
