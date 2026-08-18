using System;
using System.Linq;
using SEStubs;
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
			Game.Session.SessionSettings = new FakeSessionSettings { SyncDistance = 2000 };
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
		public void RadiusSend_OverwritesTheSuppliedTimestamp()
		{
			Server server = GivenServer();
			Game.Players.Add(201, Vector3D.Zero);
			Game.ClearTraffic();
			DateTime past = new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc);
			long before = DateTime.UtcNow.Ticks;

			server.SendCommand("boom", Vector3D.Zero, 1000, sent: past);

			long stamp = DecodeCommand(Game.Sent[0]).Timestamp;
			Assert.NotEqual(past.Ticks, stamp);
			Assert.InRange(stamp, before, DateTime.UtcNow.Ticks);
		}

		[Fact]
		public void RadiusSend_EchoesTheMessageLocally()
		{
			Server server = GivenServer();
			Game.Players.Add(201, Vector3D.Zero);
			Game.ClearTraffic();

			server.SendCommand("boom", Vector3D.Zero, 1000, message: "kaboom");

			Assert.Single(Game.ShownMessages);
			Assert.Equal("kaboom", Game.ShownMessages[0].Text);
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
