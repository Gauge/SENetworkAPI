using System;
using System.Linq;
using SEStubs;
using VRage.Game;
using VRage;
using VRage.Game.ModAPI;
using VRageMath;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>Server.SendCommand -- broadcasts, targeted sends and radius sends.</summary>
	public class ServerSendTests : NetworkTestBase
	{
		[Fact]
		public void SendCommand_WithoutATarget_BroadcastsToEveryone()
		{
			Server server = GivenServer();
			Game.ClearTraffic();

			server.SendCommand("update");

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Others, packet.Target);
			Assert.Equal("update", DecodeCommand(packet).CommandString);
		}

		[Fact]
		public void SendCommand_WithASteamId_GoesOnlyToThatClient()
		{
			Server server = GivenServer();
			Game.ClearTraffic();

			server.SendCommand("update", steamId: 4242);

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Direct, packet.Target);
			Assert.Equal(4242UL, packet.Recipient);
		}

		[Fact]
		public void SendCommand_StampsTheRecipientInSteamId_NotTheSender()
		{
			// Asymmetry worth knowing: on the client Command.SteamId is the
			// sender, on the server it is whoever the packet is addressed to.
			Server server = GivenServer();
			Game.ClearTraffic();

			server.SendCommand("update", steamId: 4242);

			Assert.Equal(4242UL, TheOnlyCommandSent().SteamId);
		}

		[Fact]
		public void SendCommand_HonoursTheSuppliedTimestamp()
		{
			Server server = GivenServer();
			Game.ClearTraffic();
			DateTime past = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc);

			server.SendCommand("update", sent: past);

			Assert.Equal(past.Ticks, TheOnlyCommandSent().Timestamp);
		}

		[Fact]
		public void SendCommand_WithAMessage_EchoesItLocallyOnAListenServer()
		{
			Server server = GivenServer();
			Game.ClearTraffic();

			server.SendCommand(null, "server announcement");

			Assert.Single(Game.ShownMessages);
			Assert.Equal("server announcement", Game.ShownMessages[0].Text);
		}

		[Fact]
		public void SendCommand_WithoutASession_SkipsTheLocalEchoButStillSends()
		{
			Server server = GivenServer();
			Game.ClearTraffic();
			Game.DestroySession();

			server.SendCommand(null, "announcement");

			Assert.Empty(Game.ShownMessages);
			Assert.Single(Game.Sent);
		}

		[Fact]
		public void Say_BroadcastsAndEchoes()
		{
			Server server = GivenServer();
			Game.ClearTraffic();

			server.Say("hello everyone");

			Command cmd = TheOnlyCommandSent();
			Assert.Null(cmd.CommandString);
			Assert.Equal("hello everyone", cmd.Message);
			Assert.Single(Game.ShownMessages);
		}

		[Fact]
		public void SendCommandTo_SendsOneCopyPerRecipient()
		{
			Server server = GivenServer();
			Game.ClearTraffic();

			server.SendCommandTo(new ulong[] { 1, 2, 3 }, "update", "hi");

			Assert.Equal(3, Game.Sent.Count);
			Assert.Equal(new ulong[] { 1, 2, 3 }, Game.Sent.Select(p => p.Recipient).ToArray());
			Assert.All(Game.Sent, p => Assert.Equal(PacketTarget.Direct, p.Target));
			Assert.All(AllCommandsSent(), c => Assert.Equal("update", c.CommandString));
		}

		[Fact]
		public void SendCommandTo_WithAnEmptyList_SendsNothing()
		{
			Server server = GivenServer();
			Game.ClearTraffic();

			server.SendCommandTo(new ulong[0], "update");

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void SendCommand_AboveTheCompressionThreshold_Compresses()
		{
			Server server = GivenServer();
			Game.ClearTraffic();
			byte[] payload = new byte[NetworkAPI.CompressionThreshold + 1];

			server.SendCommand("bulk", data: payload);

			Assert.Equal(1, MyCompression.CompressCallCount);
			Assert.True(StubSerializer.Deserialize<Command>(Game.Sent[0].Data).IsCompressed);
			Assert.Equal(payload, DecodeCommand(Game.Sent[0]).Data);
		}

		// -------------------------------------------------------------------
		//  Positional (radius) sends
		// -------------------------------------------------------------------

		/// <summary>
		/// Moves the host's player out of every plausible radius. A listen server's
		/// own player is in MyAPIGateway.Players and would otherwise be collected
		/// by the range query alongside the remote clients under test.
		/// </summary>
		private void MoveHostOutOfRange()
		{
			((FakePlayer)Game.Session.Player).Position = new Vector3D(1e9, 0, 0);
		}

		[Fact]
		public void RadiusSend_ReachesOnlyPlayersInsideTheSphere()
		{
			Server server = GivenServer();
			MoveHostOutOfRange();
			Game.Players.Add(201, new Vector3D(100, 0, 0));   // inside
			Game.Players.Add(202, new Vector3D(5000, 0, 0));  // outside
			Game.ClearTraffic();

			server.SendCommand("boom", Vector3D.Zero, 1000);

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(201UL, packet.Recipient);
		}

		[Fact]
		public void RadiusSend_WithZeroRadius_FallsBackToTheSessionSyncDistance()
		{
			Server server = GivenServer();
			MoveHostOutOfRange();
			Game.Session.SessionSettings = new MyObjectBuilder_SessionSettings { SyncDistance = 2000 };
			Game.Players.Add(201, new Vector3D(1900, 0, 0));  // inside sync distance
			Game.Players.Add(202, new Vector3D(2100, 0, 0));  // outside
			Game.ClearTraffic();

			server.SendCommand("boom", Vector3D.Zero);

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(201UL, packet.Recipient);
		}

		[Fact]
		public void RadiusSend_ExcludesThePlayerTheCommandCameFrom()
		{
			Server server = GivenServer();
			MoveHostOutOfRange();
			Game.Players.Add(201, Vector3D.Zero);
			Game.Players.Add(202, Vector3D.Zero);
			Game.ClearTraffic();

			// SteamId on a broadcast identifies the originator to filter out.
			server.SendCommand(new Command { CommandString = "boom", SteamId = 201 }, Vector3D.Zero, 1000);

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(202UL, packet.Recipient);
		}

		[Fact]
		public void RadiusSend_ToASpecificPlayer_IgnoresDistanceEntirely()
		{
			Server server = GivenServer();
			Game.Players.Add(201, new Vector3D(1000000, 0, 0));
			Game.ClearTraffic();

			server.SendCommand("boom", Vector3D.Zero, 10, steamId: 201);

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(201UL, packet.Recipient);
		}

		[Fact]
		public void RadiusSend_WithNobodyInRange_SendsNothing()
		{
			Server server = GivenServer();
			MoveHostOutOfRange();
			Game.Players.Add(201, new Vector3D(9999, 0, 0));
			Game.ClearTraffic();

			server.SendCommand("boom", Vector3D.Zero, 10);

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void RadiusSend_IncludesTheHostPlayerWhenInRange()
		{
			// The host's own player object is in MyAPIGateway.Players, so a
			// listen server addresses a packet to itself as well.
			Server server = GivenServer();
			Game.ClearTraffic();

			server.SendCommand("boom", Vector3D.Zero, 1000);

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(HostId, packet.Recipient);
		}

		[Fact]
		public void RadiusSend_HonoursTheSuppliedTimestamp()
		{
			Server server = GivenServer();
			Game.Players.Add(201, Vector3D.Zero);
			Game.ClearTraffic();
			DateTime past = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc);

			server.SendCommand("boom", Vector3D.Zero, 1000, sent: past);

			Assert.Equal(past.Ticks, DecodeCommand(Game.Sent[0]).Timestamp);
		}

		[Fact]
		public void RadiusSend_WithAMessage_AmplifiesItOnAListenServer()
		{
			// The host is inside its own radius and is not excluded (the filter
			// only skips Command.SteamId, which is 0 here), and the engine
			// delivers a packet addressed to the local player straight back.
			// So the line is shown three times on the host -- local echo, own
			// packet, then the relay's echo -- and the clients receive it twice.
			Server server = GivenServer();
			Game.Players.Add(201, Vector3D.Zero);
			Game.ClearTraffic();

			server.SendCommand("boom", Vector3D.Zero, 1000, message: "kaboom");

			Assert.Equal(3, Game.ShownMessages.Count);
			Assert.All(Game.ShownMessages, m => Assert.Equal("kaboom", m.Text));
			// Two directed packets (host, client 201) plus a full broadcast that
			// the host emitted while handling its own copy.
			Assert.Equal(3, Game.Sent.Count);
			Assert.Single(Game.Sent, p => p.Target == PacketTarget.Others);
		}

		[Fact]
		public void RadiusSend_WithoutAMessage_DoesNotAmplify()
		{
			Server server = GivenServer();
			MoveHostOutOfRange();
			Game.Players.Add(201, Vector3D.Zero);
			Game.ClearTraffic();

			server.SendCommand("boom", Vector3D.Zero, 1000);

			Assert.Single(Game.Sent);
			Assert.Empty(Game.ShownMessages);
		}

		[Fact]
		public void ADirectedSendToTheHostIsDeliveredLocally()
		{
			// HandleMessageClient in the engine dispatches to the local handlers
			// when recipient == Sync.MyId, so a server addressing itself runs its
			// own receive path.
			NetworkAPI server = GivenServer();
			bool received = false;
			server.RegisterNetworkCommand("ping", (s, c, d, t) => received = true);
			Game.ClearTraffic();

			server.SendCommand("ping", steamId: HostId);

			Assert.True(received);
		}

		[Fact]
		public void AnUnreliableSendTooBigForTheEngineIsUpgradedToReliable()
		{
			// MyMultiplayerBase refuses any unreliable message over 1024 bytes
			// and reports it through a return value nobody reads, so the data
			// used to vanish. It is sent reliably instead.
			Server server = GivenServer();
			Game.ClearTraffic();
			byte[] incompressible = new byte[4096];
			new Random(7).NextBytes(incompressible);

			server.SendCommand("bulk", data: incompressible, isReliable: false);

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.True(packet.Reliable);
			Assert.Empty(Game.Multiplayer.Dropped);
		}

		[Fact]
		public void CompressionCanBringAnUnreliableSendUnderTheLimit()
		{
			// Compression runs before the engine's size check, so a compressible
			// payload that would otherwise be dropped now fits.
			Server server = GivenServer();
			Game.ClearTraffic();

			server.SendCommand("bulk", data: new byte[4096], isReliable: false);

			Assert.Single(Game.Sent);
			Assert.Empty(Game.Multiplayer.Dropped);
		}

		[Fact]
		public void CompressionIsSkippedWhenItWouldNotHelp()
		{
			// Random data does not compress; keeping the "compressed" copy would
			// make the packet bigger and cost the receiver a decompression.
			Server server = GivenServer();
			Game.ClearTraffic();
			byte[] incompressible = new byte[4096];
			new Random(11).NextBytes(incompressible);

			server.SendCommand("bulk", data: incompressible);

			Command cmd = TheOnlyCommandSent();
			Assert.False(cmd.IsCompressed);
			Assert.Equal(incompressible, cmd.Data);
		}

		[Fact]
		public void PayloadsOverTheThresholdAreCompressed()
		{
			Server server = GivenServer();
			Game.ClearTraffic();

			server.SendCommand("bulk", data: new byte[NetworkAPI.CompressionThreshold + 1]);

			Assert.True(StubSerializer.Deserialize<Command>(Game.Sent[0].Data).IsCompressed);
		}

		[Fact]
		public void UnreliableMessagesUnderTheEngineLimitGoThrough()
		{
			Server server = GivenServer();
			Game.ClearTraffic();

			server.SendCommand("small", data: new byte[64], isReliable: false);

			Assert.Single(Game.Sent);
			Assert.Empty(Game.Multiplayer.Dropped);
		}

		[Fact]
		public void RadiusSend_UsesSquaredDistance_SoTheBoundaryIsExclusive()
		{
			Server server = GivenServer();
			MoveHostOutOfRange();
			Game.Players.Add(201, new Vector3D(1000, 0, 0));
			Game.ClearTraffic();

			server.SendCommand("boom", Vector3D.Zero, 1000);

			Assert.Empty(Game.Sent);
		}
	}
}
