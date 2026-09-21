using System;
using SEStubs;
using VRage.Game.Components;
using Xunit;

namespace SENetworkAPI.Tests
{
	public class SenderIdentityTests : NetworkTestBase
	{
		private class TestSessionComponent : MySessionComponentBase { }

		private const ulong SomeoneElse = 76561190000000009;

		[Fact]
		public void CommandsUseTheAuthenticatedSender()
		{
			NetworkAPI server = GivenServer();
			ulong reported = 0;
			server.RegisterNetworkCommand("admin", (s, c, d, t) => reported = s);

			// A forged envelope cannot override the transport identity.
			Receive(EncodeCommandPacket("admin", from: SomeoneElse));

			Assert.Equal(ClientId, reported);
		}

		[Fact]
		public void CommandEventsUseTheAuthenticatedSender()
		{
			NetworkAPI server = GivenServer();
			ulong reported = 0;
			server.OnCommandRecived += (s, c, d, t) => reported = s;

			Receive(EncodeCommandPacket("anything", from: SomeoneElse));

			Assert.Equal(ClientId, reported);
		}

		[Fact]
		public void PropertyEventsUseTheAuthenticatedSender()
		{
			GivenServer();
			NetSync<int> property = new NetSync<int>(new TestSessionComponent(), TransferType.Both, 0, syncOnLoad: false);
			ulong reported = 0;
			property.ValueChangedByNetwork += (o, n, s) => reported = s;

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 5, from: SomeoneElse));

			Assert.Equal(ClientId, reported);
		}

		[Fact]
		public void ServerRejectsClientWritesToServerOwnedProperties()
		{
			GivenServer();
			NetSync<int> property = new NetSync<int>(new TestSessionComponent(), TransferType.ServerToClient, 1, syncOnLoad: false);
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 999, from: ClientId));

			Assert.Equal(1, property.Value);
			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void ClientRejectsUpdatesThatDidNotComeFromTheServer()
		{
			GivenClient();
			NetSync<int> property = new NetSync<int>(new TestSessionComponent(), TransferType.ServerToClient, 1, syncOnLoad: false);

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 42, from: 12345), senderId: 12345, fromServer: false);

			Assert.Equal(1, property.Value);
		}
	}
}
