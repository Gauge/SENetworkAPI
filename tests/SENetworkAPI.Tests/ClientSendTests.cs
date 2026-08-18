using System;
using System.Linq;
using SEStubs;
using VRage;
using VRageMath;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>Client.SendCommand -- everything a client sends goes to the server.</summary>
	public class ClientSendTests : NetworkTestBase
	{
		[Fact]
		public void SendCommand_GoesToTheServer()
		{
			Client client = GivenClient();

			client.SendCommand("update");

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Server, packet.Target);
			Assert.Equal(ComId, packet.ComId);
			Assert.Equal("update", DecodeCommand(packet).CommandString);
		}

		[Fact]
		public void SendCommand_StampsTheLocalPlayersSteamId()
		{
			Client client = GivenClient(steamId: 555);

			client.SendCommand("update");

			Assert.Equal(555UL, TheOnlyCommandSent().SteamId);
		}

		[Fact]
		public void SendCommand_CarriesMessageAndData()
		{
			Client client = GivenClient();

			client.SendCommand("update", "hello", new byte[] { 1, 2, 3 });

			Command cmd = TheOnlyCommandSent();
			Assert.Equal("hello", cmd.Message);
			Assert.Equal(new byte[] { 1, 2, 3 }, cmd.Data);
		}

		[Fact]
		public void SendCommand_WithoutAPlayer_SendsNothingAndWarns()
		{
			Client client = GivenClient();
			Game.Session.Player = null;

			client.SendCommand("update");

			Assert.Empty(Game.Sent);
			Assert.True(LoggedWarning("Session does not exist"));
		}

		[Fact]
		public void SendCommand_WithoutASession_SendsNothingAndWarns()
		{
			Client client = GivenClient();
			Game.DestroySession();

			client.SendCommand("update");

			Assert.Empty(Game.Sent);
			Assert.True(LoggedWarning("Session does not exist"));
		}

		[Fact]
		public void SendCommand_IgnoresTheSuppliedTimestamp()
		{
			// Documented quirk: the public overload accepts `sent`, but the
			// internal sender overwrites Timestamp with DateTime.UtcNow.Ticks.
			Client client = GivenClient();
			DateTime past = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			long before = DateTime.UtcNow.Ticks;

			client.SendCommand("update", sent: past);

			long stamp = TheOnlyCommandSent().Timestamp;
			Assert.NotEqual(past.Ticks, stamp);
			Assert.InRange(stamp, before, DateTime.UtcNow.Ticks);
		}

		[Fact]
		public void SendCommand_PropagatesTheReliableFlag()
		{
			Client client = GivenClient();

			client.SendCommand("a", isReliable: false);
			client.SendCommand("b", isReliable: true);

			Assert.False(Game.Sent[0].Reliable);
			Assert.True(Game.Sent[1].Reliable);
		}

		[Fact]
		public void SendCommand_BelowTheCompressionThreshold_SendsRawBytes()
		{
			Client client = GivenClient();

			client.SendCommand("bulk", data: new byte[NetworkAPI.CompressionThreshold]);

			Assert.False(TheOnlyCommandSent().IsCompressed);
			Assert.Equal(0, MyCompression.CompressCallCount);
		}

		[Fact]
		public void SendCommand_AboveTheCompressionThreshold_CompressesThePayload()
		{
			Client client = GivenClient();
			byte[] payload = new byte[NetworkAPI.CompressionThreshold + 1];

			client.SendCommand("bulk", data: payload);

			Assert.Equal(1, MyCompression.CompressCallCount);
			Command cmd = StubSerializer.Deserialize<Command>(Game.Sent[0].Data);
			Assert.True(cmd.IsCompressed);
			Assert.True(cmd.Data.Length < payload.Length);
			// DecodeCommand transparently reverses it.
			Assert.Equal(payload, DecodeCommand(Game.Sent[0]).Data);
		}

		[Fact]
		public void SendCommand_WithAPoint_BehavesExactlyLikeTheNormalOverload()
		{
			// Positional sends are a server concept; a client always sends to
			// the server, so point/radius are ignored.
			Client client = GivenClient();

			client.SendCommand("update", new Vector3D(1000, 2000, 3000), 50, "msg");

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Server, packet.Target);
			Command cmd = DecodeCommand(packet);
			Assert.Equal("update", cmd.CommandString);
			Assert.Equal("msg", cmd.Message);
		}

		[Fact]
		public void Say_SendsAMessageWithNoCommand()
		{
			Client client = GivenClient();

			client.Say("hello world");

			Command cmd = TheOnlyCommandSent();
			Assert.Null(cmd.CommandString);
			Assert.Equal("hello world", cmd.Message);
		}

		[Fact]
		public void SendCommand_DoesNotEchoTheMessageLocally()
		{
			// Unlike the server, a client waits for the server to broadcast the
			// line back before it appears in chat.
			Client client = GivenClient();

			client.Say("hello");

			Assert.Empty(Game.ShownMessages);
		}

		[Fact]
		public void SendCommand_TargetSteamIdIsIgnoredByClients()
		{
			// A client cannot address another client; the id only labels the packet.
			Client client = GivenClient(steamId: 555);

			client.SendCommand("update", steamId: 999);

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Server, packet.Target);
			Assert.Equal(555UL, DecodeCommand(packet).SteamId);
		}
	}
}
