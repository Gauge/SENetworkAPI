using System;
using SEStubs;
using VRage.Game.Components;
using VRage.Game.Entity;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>
	/// End-to-end paths. SENetworkAPI's registries are process-wide statics, so
	/// one process cannot host a server and a client simultaneously. Each test
	/// instead plays the two sides in sequence: it captures the bytes one side
	/// puts on the wire, tears the instance down, starts a fresh instance in the
	/// other role, and delivers those bytes to it.
	/// </summary>
	public class IntegrationTests : NetworkTestBase
	{
		private class TestSessionComponent : MySessionComponentBase { }

		private static NetSync<T> DeclareSessionProperty<T>(T start, TransferType transfer = TransferType.Both)
			=> new NetSync<T>(new TestSessionComponent(), transfer, start, syncOnLoad: false);

		[Fact]
		public void AServerBroadcast_IsAppliedByAClient()
		{
			// --- server ---------------------------------------------------
			GivenServer();
			DeclareSessionProperty(0).Value = 42;
			byte[] wire = Assert.Single(Game.Sent).Data;

			// --- client ---------------------------------------------------
			Restart();
			GivenClient();
			NetSync<int> clientCopy = DeclareSessionProperty(0);
			ulong sender = 0;
			clientCopy.ValueChangedByNetwork += (o, n, s) => sender = s;

			Receive(wire);

			Assert.Equal(42, clientCopy.Value);
			Assert.Equal(HostId, sender);
			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void AClientUpdate_IsAppliedAndRelayedByTheServer()
		{
			// --- client ---------------------------------------------------
			GivenClient();
			DeclareSessionProperty(0).Value = 42;
			byte[] wire = Assert.Single(Game.Sent).Data;

			// --- server ---------------------------------------------------
			Restart();
			GivenServer();
			NetSync<int> serverCopy = DeclareSessionProperty(0);
			Game.ClearTraffic();

			Receive(wire);

			Assert.Equal(42, serverCopy.Value);
			// The server fans the new value back out to the other clients.
			SentPacket relay = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Others, relay.Target);
			Assert.Equal(42, StubSerializer.Deserialize<int>(DecodeSyncData(relay).Data));
		}

		[Fact]
		public void AFetchHandshake_CarriesTheServerValueBackToTheClient()
		{
			// --- client asks ----------------------------------------------
			GivenClient();
			DeclareSessionProperty(0).Fetch();
			Game.NextFrame();
			byte[] request = Assert.Single(Game.Sent).Data;

			// --- server answers -------------------------------------------
			Restart();
			GivenServer();
			DeclareSessionProperty(99);
			Game.ClearTraffic();

			Receive(request);
			Game.NextFrame();

			SentPacket reply = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Direct, reply.Target);
			Assert.Equal(ClientId, reply.Recipient);
			byte[] answer = reply.Data;

			// --- client applies -------------------------------------------
			Restart();
			GivenClient();
			NetSync<int> clientCopy = DeclareSessionProperty(0);

			Receive(answer);

			Assert.Equal(99, clientCopy.Value);
		}

		[Fact]
		public void SyncOnLoad_ProducesTheFetchThatSeedsANewClient()
		{
			// The whole point of syncOnLoad: a client joining mid-game asks for
			// the authoritative value as soon as the property is declared.
			GivenClient();
			Game.ClearTraffic();

			new NetSync<int>(new TestSessionComponent(), TransferType.ServerToClient);
			Game.NextFrame();
			byte[] request = Assert.Single(Game.Sent).Data;

			Restart();
			GivenServer();
			new NetSync<int>(new TestSessionComponent(), TransferType.ServerToClient, 7);
			Game.ClearTraffic();

			Receive(request);
			Game.NextFrame();

			Assert.Equal(7, StubSerializer.Deserialize<int>(DecodeSyncData(Assert.Single(Game.Sent)).Data));
		}

		[Fact]
		public void AChatCommand_CanDriveANetworkCommand()
		{
			// --- client types "/test update" ------------------------------
			NetworkAPI client = GivenClient("/test");
			client.RegisterChatCommand("update", args => client.SendCommand("update", data: new byte[] { 7 }));

			Game.Utilities.SimulateChat("/test update");
			byte[] wire = Assert.Single(Game.Sent).Data;

			// --- server handles it ----------------------------------------
			Restart();
			NetworkAPI server = GivenServer();
			ulong sender = 0;
			byte[] payload = null;
			server.RegisterNetworkCommand("update", (s, c, d, t) => { sender = s; payload = d; });

			Receive(wire);

			Assert.Equal(ClientId, sender);
			Assert.Equal(new byte[] { 7 }, payload);
		}

		[Fact]
		public void ACompressedPayloadSurvivesTheRoundTrip()
		{
			GivenServer();

			// Compressible, so the packet really is compressed - otherwise this
			// would quietly stop exercising the compression path.
			byte[] payload = new byte[NetworkAPI.CompressionThreshold * 8];
			for (int i = 0; i < payload.Length; i++)
			{
				payload[i] = (byte)(i % 4);
			}

			NetworkAPI.Instance.SendCommand("bulk", data: payload);
			SentPacket packet = Assert.Single(Game.Sent);
			Assert.True(StubSerializer.Deserialize<Command>(packet.Data).IsCompressed);

			Restart();
			NetworkAPI client = GivenClient();
			byte[] received = null;
			client.RegisterNetworkCommand("bulk", (s, c, d, t) => received = d);

			Receive(packet.Data);

			Assert.Equal(payload, received);
		}

		[Fact]
		public void AnIncompressiblePayloadSurvivesTheRoundTripUncompressed()
		{
			GivenServer();
			byte[] payload = new byte[NetworkAPI.CompressionThreshold * 2];
			new Random(1234).NextBytes(payload);

			NetworkAPI.Instance.SendCommand("bulk", data: payload);
			SentPacket packet = Assert.Single(Game.Sent);
			Assert.False(StubSerializer.Deserialize<Command>(packet.Data).IsCompressed);

			Restart();
			NetworkAPI client = GivenClient();
			byte[] received = null;
			client.RegisterNetworkCommand("bulk", (s, c, d, t) => received = d);

			Receive(packet.Data);

			Assert.Equal(payload, received);
		}

		[Fact]
		public void AnEntityPropertyUpdate_LandsOnTheMatchingPropertyOfTheMatchingEntity()
		{
			GivenServer();
			MyEntity serverEntity = Game.CreateEntity();
			new NetSync<int>(serverEntity, TransferType.Both, 0, syncOnLoad: false, limitToSyncDistance: false);
			NetSync<string> serverTarget = new NetSync<string>(serverEntity, TransferType.Both, string.Empty, syncOnLoad: false, limitToSyncDistance: false);

			serverTarget.Value = "synced";
			byte[] wire = Assert.Single(Game.Sent).Data;

			// The client declares the same properties in the same order on an
			// entity with the same id -- that ordering is the addressing scheme.
			Restart();
			GivenClient();
			MyEntity clientEntity = Game.CreateEntity();
			Assert.Equal(serverEntity.EntityId, clientEntity.EntityId);
			NetSync<int> clientOther = new NetSync<int>(clientEntity, TransferType.Both, 0, syncOnLoad: false);
			NetSync<string> clientTarget = new NetSync<string>(clientEntity, TransferType.Both, string.Empty, syncOnLoad: false);

			Receive(wire);

			Assert.Equal("synced", clientTarget.Value);
			Assert.Equal(0, clientOther.Value);
		}

		[Fact]
		public void AMismatchedDeclarationOrder_MisroutesTheUpdate()
		{
			// The flip side of index addressing: entity properties are addressed
			// by declaration order, not by name. A client build that declares an
			// extra property first shifts every index and silently applies the
			// update to the wrong property.
			GivenServer();
			MyEntity serverEntity = Game.CreateEntity();
			new NetSync<string>(serverEntity, TransferType.Both, string.Empty, syncOnLoad: false, limitToSyncDistance: false);
			NetSync<string> serverStatusB = new NetSync<string>(serverEntity, TransferType.Both, string.Empty, syncOnLoad: false, limitToSyncDistance: false);

			serverStatusB.Value = "meant for status B";
			byte[] wire = Assert.Single(Game.Sent).Data;

			Restart();
			GivenClient();
			MyEntity clientEntity = Game.CreateEntity();
			new NetSync<string>(clientEntity, TransferType.Both, string.Empty, syncOnLoad: false);          // index 0: extra
			NetSync<string> clientStatusA = new NetSync<string>(clientEntity, TransferType.Both, string.Empty, syncOnLoad: false);  // index 1
			NetSync<string> clientStatusB = new NetSync<string>(clientEntity, TransferType.Both, string.Empty, syncOnLoad: false);  // index 2

			Receive(wire);

			Assert.Equal("meant for status B", clientStatusA.Value);
			Assert.Equal(string.Empty, clientStatusB.Value);
		}
	}
}
