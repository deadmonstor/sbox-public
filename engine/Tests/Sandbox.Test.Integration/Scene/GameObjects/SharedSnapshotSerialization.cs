using System;
using System.Linq;
using Sandbox.Internal;
using Sandbox.Network;

namespace SceneTests.GameObjects;

[TestClass]
[DoNotParallelize]
public class SharedSnapshotSerializationTest
{
	[TestMethod]
	public void IdenticalPeersSharePacketsButLateJoinReceivesItsOwnState()
	{
		var oldLibrary = Game.TypeLibrary;
		Game.TypeLibrary = new TypeLibrary();
		Game.TypeLibrary.AddAssembly( typeof( PrefabFile ).Assembly, false );
		Game.TypeLibrary.AddAssembly( typeof( ModelRenderer ).Assembly, false );
		try
		{
			using var scene = new Scene().Push();
			using var peers = new ClientAndHost( Game.TypeLibrary );
			peers.BecomeHost();
			var second = new TestConnection( Guid.NewGuid() );
			Networking.System.OnConnected( second );
			Networking.System.AddConnection( second, UserInfo.Local );
			var go = new GameObject();
			go.Network.AlwaysTransmit = true;
			go.Network.Interpolation = false;
			go.NetworkSpawn( peers.Host );
			IDeltaSnapshot snapshotter = go._net;
			var snapshots = SceneNetworkSystem.Instance.DeltaSnapshots;
			snapshots.Send( [snapshotter], [peers.Client, second] );
			Assert.AreSame( peers.Client.LastEncoded, second.LastEncoded );
			var initial = second.LastEncoded;

			var late = new TestConnection( Guid.NewGuid() );
			Networking.System.OnConnected( late );
			Networking.System.AddConnection( late, UserInfo.Local );
			go.LocalPosition = Vector3.Right * 42;
			peers.Client.Messages.Clear();
			second.Messages.Clear();
			late.Messages.Clear();
			snapshots.Send( [snapshotter], [peers.Client, late, second] );
			Assert.AreNotSame( initial, second.LastEncoded, "Packets from earlier sends must remain immutable" );
			Assert.AreNotSame( peers.Client.LastEncoded, late.LastEncoded, "A late join needs slots already predicted for older peers" );
			CollectionAssert.AreEqual( peers.Client.LastEncoded, second.LastEncoded );
			Assert.IsTrue( late.LastEncoded.Length > peers.Client.LastEncoded.Length );
			foreach ( var peer in new[] { peers.Client, second, late } )
			{
				var message = peer.Messages.Single( m => m.Type == InternalMessageType.DeltaSnapshotCluster );
				using var envelope = ByteStream.CreateReader( (byte[])message.Payload );
				using var payload = envelope.ReadByteStream( envelope.Read<int>() );
				payload.Read<ushort>();
				Assert.AreEqual( (ushort)1, payload.Read<ushort>() );
				payload.Read<ushort>();
				payload.Read<ushort>();
				Assert.AreEqual( go.Id, payload.Read<Guid>() );
				var count = payload.Read<ushort>();
				Assert.AreEqual( peer == late ? go._net.LocalSnapshotState.Entries.Count : 1, (int)count );
			}
			snapshots.Reset();
		}
		finally
		{
			Game.TypeLibrary = oldLibrary;
		}
	}
}
