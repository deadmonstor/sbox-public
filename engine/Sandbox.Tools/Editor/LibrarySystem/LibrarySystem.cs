using Sandbox.Engine;
using System;
using System.Threading;

namespace Editor;

public static class LibrarySystem
{
	static HashSet<LibraryProject> _all = new();

	static string LibraryFolder { get; set; }

	/// <summary>
	/// Get all active libraries
	/// </summary>
	static public IEnumerable<LibraryProject> All => _all;

	/// <summary>
	/// Scan this project's Libraries folder and add all of the library projects from it.
	/// </summary>
	internal static void InitializeFromProject( Project project )
	{
		LibraryFolder = System.IO.Path.Combine( project.RootDirectory.FullName, "Libraries" );

		foreach ( var folder in System.IO.Directory.EnumerateDirectories( LibraryFolder ) )
		{
			AddFromFolder( folder );
		}
	}

	/// <summary>
	/// Add a library project from a specific folder
	/// </summary>
	private static LibraryProject AddFromFolder( string folder )
	{
		var configs = System.IO.Directory.EnumerateFiles( folder, "*.sbproj" ).ToArray();
		if ( configs.Length != 1 ) return default;

		var project = Project.AddFromFile( configs[0], true );
		if ( project is null ) return default;

		var existing = _all.SingleOrDefault( x => x.Project == project );

		if ( existing is not null )
		{
			// Re-read the version - in case it updated
			existing.ReadVersionFromFile();
			return existing;
		}

		var lib = new LibraryProject( project );
		_all.Add( lib );
		return lib;
	}

	/// <summary>
	/// Add a library from this folder
	/// </summary>
	public static async Task Add( string folderName, CancellationToken token )
	{
		var fullPath = FileSystem.Libraries.GetFullPath( folderName );
		if ( !System.IO.Directory.Exists( fullPath ) )
		{
			Log.Warning( "Tried to add library but it doesn't exist" );
			return;
		}

		var project = AddFromFolder( fullPath );
		if ( project is null )
		{
			Log.Warning( "Tried to add library but it couldn't create project" );
			return;
		}

		// If we have an assets path, then AddFromFolder called the LibraryProject constructor
		// which added a new CONTENT path. So here we tell the engine to rebuild the content 
		// paths, so it finds all the new assets.
		if ( project.Project.HasAssetsPath() )
		{
			Editor.FileSystem.RebuildContentPath();

			if ( !Sandbox.Application.IsUnitTest )
			{
				IAssetSystem.UpdateMods();
			}

			await ResourceLoader.LoadAllGameResourceAsync( FileSystem.Mounted, token );
		}

		// install it
		await PackageManager.InstallAsync( new PackageLoadOptions( project.Project.Package.FullIdent, "local", token ) );
		await PackageManager.InstallAsync( new PackageLoadOptions( project.Project.Package.FullIdent, "gamemenu", token ) );

		// update the sln with the new projects
		await Project.GenerateSolution();

		// I don't know if this does anything?
		IAssetSystem.UpdateMods();



		// Trigger a rebuild of all projects and wait for the result
		await EditorUtility.Projects.Updated( Project.Current );

		// Recreate the project filesystem
		//FileSystem.InitializeFromProject( Project.Current );

		// Reload the game project, which'll load the new library too
		await GameInstanceDll.Current.LoadGamePackageAsync( Project.Current.Package.FullIdent, GameLoadingFlags.Host | GameLoadingFlags.Reload, token );
	}


	internal static void RemoveLibrary( LibraryProject proj )
	{
		_all.Remove( proj );

		var path = proj.Project.GetRootPath();

		DeleteFolderWithRetry( path );

		EditorUtility.DisplayDialog( "Library Removed", "This library has been deleted. Please restart the editor to fully remove it. This is horrible I am so sorry." );
	}

	private static void DeleteFolderWithRetry( string path )
	{
		// try to delete the folder a few times
		// expect trouble, so retry and ignore some errors
		for ( int i = 0; i < 10; i++ )
		{
			try
			{
				System.IO.Directory.Delete( path, true );
			}
			catch ( System.IO.DirectoryNotFoundException )
			{
				// Good - that's what we want
				break;
			}
			catch ( System.Exception )
			{
				Thread.Sleep( 100 );
			}
		}
	}

	[ConCmd( "library_install", ConVarFlags.Protected )]
	internal static void InstallLibrary( string name )
	{
		if ( !Package.TryParseIdent( name, out var ident ) )
		{
			Log.Warning( "Invalid package ident" );
			return;
		}

		Log.Warning( $"Installing Library {ident.org}.{ident.package}" );
		_ = Install( $"{ident.org}.{ident.package}", ident.version ?? -1 );
	}

	/// <summary>
	/// Install a library from a package. This will download the package and install it in the project's Library folder.
	/// </summary>
	public static async Task<bool> Install( string ident, long versionId = -1, CancellationToken token = default )
	{
		//
		// TODO - We need a scoped thing to prevent filesystem reacting to file changes
		// something like using( FileSystem.DeferChanges() ), but globally
		//

		using var progress = Application.Editor.ProgressSection();
		progress.Title = "Installing Package";

		var package = await Package.FetchAsync( ident, false );
		if ( package is null )
		{
			Log.Warning( "Failed to install library, package not found" );
			return false;
		}

		// no version specified - then use the current live version
		if ( versionId <= 0 )
		{
			versionId = package.Revision.VersionId;
		}

		// get the version info 
		var version = (await Package.FetchVersions( ident )).Where( x => x.VersionId == versionId ).FirstOrDefault();
		if ( version == null )
		{
			Log.Warning( "Failed to install library, version wasn't found" );
			return false;
		}

		var folderName = package.FullIdent;
		var versionPath = $"{folderName}/.version";
		var versionIdent = new Version( 1, 0, (int)version.VersionId );

		// Download the manifest
		await version.DownloadManifestAsync( token );

		// TODO: Remove this once we have FileSystem.DeferChanges()
		FileWatch.SuppressWatchers = RealTime.Now + 999;

		// does this already exist?
		if ( FileSystem.Libraries.FileExists( versionPath ) )
		{
			var versionText = FileSystem.Libraries.ReadAllText( versionPath );

			if ( Version.TryParse( versionText.Trim(), out var result ) )
			{
				if ( versionIdent == result )
				{
					Log.Warning( "Updating library to the same version. Erasing local changes." );
				}
				else
				{
					Log.Info( $"Updating library from {result} to {versionIdent}" );
				}
			}

			// loop over existing files and delete any with changes or that are no longer in the manifest
			var existingFiles = FileSystem.Libraries.FindFile( folderName, "*", true );
			foreach ( var existingFile in existingFiles )
			{
				var newFile = version.Manifest.Files.FirstOrDefault( x => x.Path == existingFile );
				var fullPath = $"{folderName}/{existingFile}";
				if ( !version.Manifest.Files.Any( x => x.Path == existingFile ) || FileSystem.Libraries.GetCrc( fullPath ) != Convert.ToUInt64( newFile.Crc, 16 ) )
				{
					FileSystem.Libraries.DeleteFile( fullPath );
					progress.Title = $"Deleting {existingFile}";
				}
			}
		}

		FileSystem.Libraries.CreateDirectory( folderName );

		// Write the version id
		FileSystem.Libraries.WriteAllText( versionPath, versionIdent.ToString() );

		// Download the files we don't have yet, a few at a time. The library is compiled as soon as Add
		// runs, so every download has to have finished first.
		var missingFiles = version.Manifest.Files.Where( x => !FileSystem.Libraries.FileExists( $"{folderName}/{x.Path}" ) );

		async Task<bool> DownloadFile( ManifestSchema.File file )
		{
			var targetPath = FileSystem.Libraries.GetFullPath( $"{folderName}/{file.Path}" );
			var success = await EditorUtility.DownloadAsync( file.Url, targetPath, null, token );
			progress.Title = file.Path;
			return success;
		}

		bool downloaded;
		try
		{
			downloaded = await DownloadFiles( missingFiles, DownloadFile, token );
		}
		finally
		{
			FileWatch.SuppressWatchers = RealTime.Now;
		}

		if ( !downloaded )
		{
			Log.Warning( $"Failed to install library {ident}, some of its files didn't download" );
			return false;
		}

		token.ThrowIfCancellationRequested();
		await Add( folderName, token );

		return true;
	}

	/// <summary>
	/// Run <paramref name="download"/> for each file, keeping about 10kb in flight at once. Returns false if any
	/// failed. Never returns, or throws, while a download it started is still running.
	/// </summary>
	internal static async Task<bool> DownloadFiles( IEnumerable<ManifestSchema.File> files, Func<ManifestSchema.File, Task<bool>> download, CancellationToken token )
	{
		var downloads = new List<Task<bool>>();
		long bytesDownloading = 0;

		async Task<bool> Download( ManifestSchema.File file )
		{
			bytesDownloading += file.Size;

			try
			{
				return await download( file );
			}
			catch ( Exception e ) when ( e is not OperationCanceledException )
			{
				Log.Warning( e, $"Couldn't download {file.Path}: {e.Message}" );
				return false;
			}
			finally
			{
				bytesDownloading -= file.Size;
			}
		}

		try
		{
			foreach ( var file in files )
			{
				while ( bytesDownloading >= 10_000 )
				{
					await Task.Delay( 1, token );
				}

				downloads.Add( Download( file ) );
			}
		}
		finally
		{
			// If we're cancelled while waiting to start the next one, the ones already started are still
			// writing files. Wait for them, without throwing, so the caller can tidy up after them.
			await Task.WhenAny( Task.WhenAll( downloads ) );
		}

		var results = await Task.WhenAll( downloads );
		return results.All( x => x );
	}
}

public class LibraryProject
{
	public Version Version { get; set; }

	public Project Project { get; }

	internal LibraryProject( Project project )
	{
		Project = project;
		ReadVersionFromFile();

		// add the content path
		if ( project.HasAssetsPath() )
		{
			var assets = project.AssetsFileSystem;

			// Add to our c# content search path
			FileSystem.Content.Mount( assets );
			FileSystem.Mounted.Mount( assets );

			// make content available to the game
			EngineFileSystem.LibraryContent.Mount( assets );

			// Add to the c++ filesystem search path
			NativeEngine.FullFileSystem.AddProjectPath( project.Config.FullIdent, project.GetAssetsPath() );
		}

		if ( project.HasCodePath() )
		{
			// make .scss etc available to the game
			EngineFileSystem.LibraryContent.Mount( project.CodeFileSystem );
		}
	}

	/// <summary>
	/// Try to read the library version from /.version
	/// </summary>
	internal void ReadVersionFromFile()
	{
		Version = new Version( 1, 1, 1 );

		var versionInfo = System.IO.Path.Combine( Project.GetRootPath(), ".version" );
		if ( !System.IO.File.Exists( versionInfo ) ) return;

		var versionText = System.IO.File.ReadAllText( versionInfo );

		if ( Version.TryParse( versionText.Trim(), out var result ) )
		{
			Version = result;
		}
	}

	/// <summary>
	/// Remove and delete this library, and folder
	/// </summary>
	public void RemoveAndDelete()
	{
		LibrarySystem.RemoveLibrary( this );
	}
}
