using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Sandbox.Network;


/// <summary>
/// A listen socket over TCP. For testing locally.
/// </summary>
internal class TcpSocket : NetworkSocket, IValid
{
	// Accepted on the listener thread, handed to the tick thread through this queue.
	readonly ConcurrentQueue<TcpChannel> accepted = new();

	// Only touched on the tick thread.
	readonly List<TcpChannel> Connections = new List<TcpChannel>();

	internal override bool SupportsHostMigration => true;

	async Task SocketThread( string address, int port, CancellationToken token )
	{
		TcpListener listener = null;
		SocketException lastError = null;

		try
		{
			// The previous host may still hold the port; a failed listener can't be reused
			for ( var attempt = 0; attempt < 20 && !token.IsCancellationRequested; attempt++ )
			{
				try
				{
					listener = new TcpListener( new IPEndPoint( IPAddress.Parse( address ), port ) );
					listener.Start();
					break;
				}
				catch ( SocketException e )
				{
					lastError = e;
					listener?.Dispose();
					listener = null;
					await Task.Delay( 500 ).ConfigureAwait( false );
				}
				catch ( System.Exception e )
				{
					Log.Warning( e, "Couldnt start TcpSocket" );
					return;
				}
			}

			if ( listener is null )
			{
				Log.Warning( $"Couldnt start TcpSocket on port {port}: {lastError?.Message}" );
				return;
			}

			while ( !token.IsCancellationRequested )
			{
				try
				{
					// Accept off the main thread, so joins don't wait on (or stall with) the game's frame
					var client = await listener.AcceptTcpClientAsync( token ).ConfigureAwait( false );
					accepted.Enqueue( new TcpChannel( client ) );
				}
				catch ( OperationCanceledException )
				{
					// Dispose() was called
				}
				catch ( Exception e )
				{
					Log.Warning( e, $"TcpSocket exception: {e.Message}" );
				}
			}

			listener.Stop();
		}
		finally
		{
			listener?.Dispose();
		}
	}

	[SkipHotload]
	CancellationTokenSource tokenSource;

	public bool IsValid => true;

	public TcpSocket( string address, int port )
	{
		tokenSource = new();

		// Runs inline up to the first await, so the port is listening once we return
		_ = SocketThread( address, port, tokenSource.Token );
	}

	~TcpSocket()
	{
		Dispose();
	}

	internal override void Dispose()
	{
		tokenSource.Cancel();
		tokenSource.Dispose();

		GC.SuppressFinalize( this );
	}

	internal override void ProcessMessagesInThread()
	{

	}

	internal override void GetIncomingMessages( NetworkSystem.MessageHandler handler )
	{
		while ( accepted.TryDequeue( out var accept ) )
		{
			Connections.Add( accept );

			try
			{
				OnClientConnect?.Invoke( accept );
			}
			catch ( Exception e )
			{
				Log.Warning( e );
			}
		}

		for ( var i = 0; i < Connections.Count; i++ )
		{
			var c = Connections[i];

			if ( !c.IsConnected )
			{
				Connections.RemoveAt( i-- );
				OnClientDisconnect?.Invoke( c );
				c.Close( 0, "Disconnect" );
				continue;
			}

			c.GetIncomingMessages( handler );
		}
	}
}
