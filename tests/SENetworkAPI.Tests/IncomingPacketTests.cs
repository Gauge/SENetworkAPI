using System;
using System.Collections.Generic;
using SEStubs;
using VRage;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>
	/// HandleIncomingPacket: the single entry point for everything arriving on
	/// the mod's communication channel.
	/// </summary>
	public class IncomingPacketTests : NetworkTestBase
	{
		[Fact]
		public void CommandPacket_InvokesTheRegisteredCallback()
		{
			NetworkAPI api = GivenClient();
			ulong sender = 0;
			string command = null;
			byte[] payload = null;
			api.RegisterNetworkCommand("update", (s, c, d, t) => { sender = s; command = c; payload = d; });

			Receive(EncodeCommandPacket("update", data: new byte[] { 4, 5, 6 }, from: 77));

			Assert.Equal(77UL, sender);
			Assert.Equal("update", command);
			Assert.Equal(new byte[] { 4, 5, 6 }, payload);
		}

		[Fact]
		public void CommandPacket_MatchesOnTheFirstWord_ButPassesTheWholeString()
		{
			NetworkAPI api = GivenClient();
			string received = null;
			api.RegisterNetworkCommand("give", (s, c, d, t) => received = c);

			Receive(EncodeCommandPacket("give steel 100"));

			Assert.Equal("give steel 100", received);
		}

		[Fact]
		public void CommandPacket_LookupIsCaseSensitive_SoMixedCaseSendsNeverMatch()
		{
			// Registration lowercases the key, but the incoming lookup does not
			// lowercase the wire value. "Update" therefore never resolves.
			NetworkAPI api = GivenClient();
			bool invoked = false;
			api.RegisterNetworkCommand("update", (s, c, d, t) => invoked = true);

			Receive(EncodeCommandPacket("Update"));

			Assert.False(invoked);
		}

		[Fact]
		public void CommandPacket_ForAnUnregisteredCommand_IsIgnoredWithoutThrowing()
		{
			GivenClient();

			Exception thrown = Record.Exception(() => Receive(EncodeCommandPacket("nobody-listens")));

			Assert.Null(thrown);
		}

		[Fact]
		public void CommandPacket_RaisesOnCommandRecived()
		{
			NetworkAPI api = GivenClient();
			long ticks = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
			ulong sender = 0;
			string command = null;
			DateTime stamp = default(DateTime);
			api.OnCommandRecived += (s, c, d, t) => { sender = s; command = c; stamp = t; };

			Receive(EncodeCommandPacket("ping", from: 42, timestamp: ticks));

			Assert.Equal(42UL, sender);
			Assert.Equal("ping", command);
			Assert.Equal(ticks, stamp.Ticks);
		}

		[Fact]
		public void OnCommandRecived_FiresEvenWhenNoCallbackIsRegistered()
		{
			NetworkAPI api = GivenClient();
			bool raised = false;
			api.OnCommandRecived += (s, c, d, t) => raised = true;

			Receive(EncodeCommandPacket("unknown"));

			Assert.True(raised);
		}

		[Fact]
		public void MessageOnlyPacket_IsShownInChatOnAClient()
		{
			GivenClient();

			Receive(EncodeCommandPacket(message: "server says hi"));

			Assert.Single(Game.ShownMessages);
			Assert.Equal(ModName, Game.ShownMessages[0].Sender);
			Assert.Equal("server says hi", Game.ShownMessages[0].Text);
		}

		[Fact]
		public void MessageOnlyPacket_DoesNotRaiseOnCommandRecived()
		{
			NetworkAPI api = GivenClient();
			bool raised = false;
			api.OnCommandRecived += (s, c, d, t) => raised = true;

			Receive(EncodeCommandPacket(message: "chatter"));

			Assert.False(raised);
		}

		[Fact]
		public void MessageOnlyPacket_IsRelayedToEveryClientByTheServer()
		{
			GivenServer();
			Game.ClearTraffic();

			Receive(EncodeCommandPacket(message: "player said something", from: ClientId));

			SentPacket relay = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Others, relay.Target);
			Command cmd = DecodeCommand(relay);
			Assert.Equal("player said something", cmd.Message);
			Assert.Null(cmd.CommandString);
		}

		[Fact]
		public void MessageOnlyPacket_OnAListenServer_IsShownTwice()
		{
			// Documented quirk: the receive path shows the message, and the relay
			// through Server.SendCommand shows it again on the host.
			GivenServer();
			Game.ClearTraffic();

			Receive(EncodeCommandPacket(message: "double vision", from: ClientId));

			Assert.Equal(2, Game.ShownMessages.Count);
		}

		[Fact]
		public void MessageOnlyPacket_IsNotShownOnADedicatedServerReceivePath()
		{
			GivenDedicatedServer();
			Game.ClearTraffic();

			Receive(EncodeCommandPacket(message: "relay me", from: ClientId));

			// Still relayed to the clients...
			Assert.Single(Game.Sent);
			// ...and the receive path itself skips ShowMessage because IsDedicated.
			// The single remaining call comes from the relay inside Server.SendCommand,
			// which is a no-op in the real game (a DS has no chat window).
			Assert.Single(Game.ShownMessages);
		}

		[Fact]
		public void ClientDoesNotRelayMessages()
		{
			GivenClient();
			Game.ClearTraffic();

			Receive(EncodeCommandPacket(message: "hello"));

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void WhitespaceOnlyMessage_IsNeitherShownNorRelayed()
		{
			GivenServer();
			Game.ClearTraffic();

			Receive(EncodeCommandPacket(message: "   "));

			Assert.Empty(Game.ShownMessages);
			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void PacketWithBothMessageAndCommand_DoesBoth()
		{
			NetworkAPI api = GivenClient();
			bool invoked = false;
			api.RegisterNetworkCommand("update", (s, c, d, t) => invoked = true);

			Receive(EncodeCommandPacket("update", message: "updating"));

			Assert.True(invoked);
			Assert.Single(Game.ShownMessages);
		}

		[Fact]
		public void APacketWithNoCommandString_DeliversNothingEvenWhenItCarriesData()
		{
			// `null` command strings are reserved for chat relays, and there is
			// no way to register a handler for them -- RegisterNetworkCommand
			// rejects null. Data sent this way is unreachable.
			NetworkAPI api = GivenClient();
			bool anyCallback = false;
			bool anyEvent = false;
			api.RegisterNetworkCommand("update", (s, c, d, t) => anyCallback = true);
			api.OnCommandRecived += (s, c, d, t) => anyEvent = true;

			Receive(EncodeCommandPacket(null, data: new byte[] { 1, 2, 3 }));

			Assert.False(anyCallback);
			Assert.False(anyEvent);
		}

		[Fact]
		public void CompressedPacket_IsDecompressedBeforeDispatch()
		{
			NetworkAPI api = GivenClient();
			byte[] big = new byte[200000];
			for (int i = 0; i < big.Length; i++)
			{
				big[i] = (byte)(i % 251);
			}

			byte[] received = null;
			api.RegisterNetworkCommand("bulk", (s, c, d, t) => received = d);

			Receive(EncodeCommandPacket("bulk", data: big, compress: true));

			Assert.Equal(big, received);
			Assert.Equal(1, MyCompression.DecompressCallCount);
		}

		[Fact]
		public void MalformedPacket_IsSwallowedAndLogged()
		{
			GivenClient();
			Game.Utilities.FailDeserializeFor = t => t == typeof(Command);

			Exception thrown = Record.Exception(() => Receive(new byte[] { 1, 2, 3 }));

			Assert.Null(thrown);
			Assert.True(LoggedError("Failure in message processing"));
		}

		[Fact]
		public void ThrowingCallback_IsContainedByTheReceiveHandler()
		{
			// A mod callback that throws must not take down the message pump.
			NetworkAPI api = GivenClient();
			api.RegisterNetworkCommand("boom", (s, c, d, t) => { throw new InvalidOperationException("mod bug"); });

			Exception thrown = Record.Exception(() => Receive(EncodeCommandPacket("boom")));

			Assert.Null(thrown);
			Assert.True(LoggedError("Failure in message processing"));
		}

		[Fact]
		public void PacketsOnAnotherComId_AreNotDelivered()
		{
			NetworkAPI api = GivenClient();
			bool invoked = false;
			api.RegisterNetworkCommand("update", (s, c, d, t) => invoked = true);

			Game.Multiplayer.Deliver(9999, EncodeCommandPacket("update"));

			Assert.False(invoked);
		}

		[Fact]
		public void LogNetworkTraffic_RecordsTheTransmissionEnvelope()
		{
			GivenClient();
			NetworkAPI.LogNetworkTraffic = true;

			Receive(EncodeCommandPacket("ping", from: 5));

			Assert.True(LoggedInfo("TRANSMISSION RECIEVED"));
			Assert.True(LoggedInfo("Command ID: ping"));
			Assert.True(LoggedInfo("END"));
		}
	}
}
