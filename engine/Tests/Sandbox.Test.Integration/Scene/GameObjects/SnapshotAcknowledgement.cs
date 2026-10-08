using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Internal;
using Sandbox.Network;

namespace SceneTests.GameObjects;

[TestClass]
[DoNotParallelize] // ClientAndHost switches process-wide networking state.
public class SnapshotAcknowledgementWireTest
{
	private static byte[] Payload( TestConnection.Message message )
	{
		using var envelope = ByteStream.CreateReader( (byte[])message.Payload );
		using var payload = envelope.ReadByteStream( envelope.Read<int>() );
		return payload.GetRemainingBytes().ToArray();
	}

	[TestMethod]
	[DataRow( false, false, false )]
	[DataRow( false, true, false )]
	[DataRow( true, false, false )]
	[DataRow( true, true, false )]
	[DataRow( false, false, true )]
	public void ClientGeneratedAcksHandleReorderingDuplicatesLossAndWrapping( bool wrap, bool dropOlder, bool returnToKnownValue )
	{
		var oldTypeLibrary = Game.TypeLibrary;
		Game.TypeLibrary = new TypeLibrary();
		Game.TypeLibrary.AddAssembly( typeof( PrefabFile ).Assembly, false );
		Game.TypeLibrary.AddAssembly( typeof( ModelRenderer ).Assembly, false );

		try
		{
			using var scope = new Scene().Push();
			using var peers = new ClientAndHost( Game.TypeLibrary );
			peers.BecomeHost();
			var go = new GameObject();
			go.Network.Interpolation = false;
			go.Network.AlwaysTransmit = true;
			go.NetworkSpawn( peers.Host );
			IDeltaSnapshot snapshotter = go._net;
			var host = SceneNetworkSystem.Instance.DeltaSnapshots;

			if ( wrap )
			{
				var ids = (Dictionary<Guid, ushort>)typeof( DeltaSnapshotSystem )
					.GetProperty( "LastSentSnapshotIds", BindingFlags.Instance | BindingFlags.NonPublic )!.GetValue( host );
				ids[go.Id] = ushort.MaxValue - 1;
			}

			byte[] SendPacket( float position )
			{
				peers.BecomeHost();
				Assert.AreSame( go, Game.ActiveScene.Directory.FindByGuid( go.Id ) );
				go.LocalPosition = Vector3.Right * position;
				peers.Client.Messages.Clear();
				host.Send( [snapshotter], [peers.Client] );
				var message = peers.Client.Messages.Single( m => m.Type == InternalMessageType.DeltaSnapshotCluster );
				return Payload( message );
			}

			byte[] ReceivePacket( byte[] packet, float position )
			{
				peers.BecomeClient();
				peers.Host.Messages.Clear();
				using ( var reader = ByteStream.CreateReader( packet ) )
					SceneNetworkSystem.Instance.DeltaSnapshots.OnDeltaSnapshotCluster( peers.Host, reader );
				Assert.AreEqual( Vector3.Right * position, go.Transform.TargetLocal.Position );
				var ack = peers.Host.Messages.Single( m => m.Type == InternalMessageType.DeltaSnapshotClusterAck );
				var ackPayload = Payload( ack );
				using ( var reader = ByteStream.CreateReader( ackPayload ) )
				{
					reader.Read<ushort>();
					Assert.AreEqual( (ushort)0, reader.Read<ushort>(), "Client rejected the snapshot" );
				}
				peers.BecomeHost();
				return ackPayload;
			}

			byte[] SendAndReceive( float position ) => ReceivePacket( SendPacket( position ), position );

			void ReceiveAck( byte[] packet )
			{
				using var reader = ByteStream.CreateReader( packet );
				using var header = ByteStream.CreateReader( packet );
				var clusterId = header.Read<ushort>();
				foreach ( var sent in host.GetConnection( peers.Client ).SentClusters.Where( c => c.Cluster.Id == clusterId ) )
					foreach ( var snapshot in sent.Cluster.Snapshots )
						Assert.AreEqual( snapshotter.SnapshotVersion, snapshot.Version, "Ack version changed" );
				host.OnDeltaSnapshotClusterAck( peers.Client, reader );
			}

			// These acknowledgements come from the production client decoder, not fabricated IDs.
			var older = SendAndReceive( 1 );
			var newer = SendAndReceive( 2 );
			ReceiveAck( newer );
			ReceiveAck( newer ); // Duplicate cluster ACK must be harmless.

			if ( returnToKnownValue )
			{
				ReceiveAck( SendAndReceive( 1 ) );
				var inFlightDifferent = SendAndReceive( 3 );
				var latestPacket = SendPacket( 1 );
				ReceiveAck( older );
				Assert.IsFalse( go._net.LocalSnapshotState.UpdatedConnections.Contains( peers.Client.Id ),
					"A matching older value cannot confirm the latest transmission while a different value is in flight" );
				ReceiveAck( ReceivePacket( latestPacket, 1 ) );
				ReceiveAck( inFlightDifferent );
			}
			else if ( dropOlder )
			{
				Assert.IsFalse( go._net.LocalSnapshotState.UpdatedConnections.Contains( peers.Client.Id ) );
				// Expire predicted slots so the missing ACK causes a real retransmission.
				typeof( DeltaSnapshotSystem ).GetProperty( "Time", BindingFlags.Instance | BindingFlags.NonPublic )!
					.SetValue( host, 1f );
				ReceiveAck( SendAndReceive( 3 ) );
			}
			else
			{
				ReceiveAck( older ); // Held ACK delivered after the newer one.
				ReceiveAck( older );
			}

			var remote = host.GetConnection( peers.Client ).ReceivedSnapshotStates[go.Id];
			Assert.IsTrue( go._net.LocalSnapshotState.UpdatedConnections.Contains( peers.Client.Id ),
				string.Join( "; ", go._net.LocalSnapshotState.Entries.Select( entry =>
					$"slot={entry.Slot}, local={entry.Hash}, known={(remote.Data.TryGetValue( entry.Slot, out var known ) ? known.Hash : 0)}, member={entry.Connections.Contains( peers.Client.Id )}" ) ) );
			foreach ( var entry in go._net.LocalSnapshotState.Entries )
			{
				Assert.IsTrue( entry.Connections.Contains( peers.Client.Id ) );
				Assert.AreEqual( entry.Hash, remote.Data[entry.Slot].Hash );
			}
			host.Reset();
		}
		finally
		{
			Game.TypeLibrary = oldTypeLibrary;
		}
	}
}
