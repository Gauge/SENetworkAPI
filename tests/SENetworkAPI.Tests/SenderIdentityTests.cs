using System;
using SEStubs;
using VRage.Game.Components;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>
	/// SENetworkAPI registers the game's *non-secure* message handler, which
	/// hands the mod nothing but the raw bytes. Every notion of "who sent this"
	/// therefore comes from a field inside the packet the sender wrote.
	///
	/// These tests pin that trust model down. They are not a demonstration of an
	/// exploit so much as documentation for mods that gate behaviour on the
	/// steam id they are handed. See docs/known-issues.md.
	/// </summary>
	public class SenderIdentityTests : NetworkTestBase
	{
		private class TestSessionComponent : MySessionComponentBase { }

		private const ulong SomeoneElse = 76561190000000009;

		[Fact]
		public void TheSenderIdIsWhateverThePacketSays()
		{
			NetworkAPI server = GivenServer();
			ulong reported = 0;
			server.RegisterNetworkCommand("admin", (s, c, d, t) => reported = s);

			// The engine never told us who this came from; the id is packet data.
			Receive(EncodeCommandPacket("admin", from: SomeoneElse));

			Assert.Equal(SomeoneElse, reported);
		}

		[Fact]
		public void OnCommandRecivedReportsTheSameUnverifiedId()
		{
			NetworkAPI server = GivenServer();
			ulong reported = 0;
			server.OnCommandRecived += (s, c, d, t) => reported = s;

			Receive(EncodeCommandPacket("anything", from: SomeoneElse));

			Assert.Equal(SomeoneElse, reported);
		}

		[Fact]
		public void ValueChangedByNetworkReportsTheSameUnverifiedId()
		{
			GivenServer();
			NetSync<int> property = new NetSync<int>(new TestSessionComponent(), TransferType.Both, 0, syncOnLoad: false);
			ulong reported = 0;
			property.ValueChangedByNetwork += (o, n, s) => reported = s;

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 5, from: SomeoneElse));

			Assert.Equal(SomeoneElse, reported);
		}

		[Fact]
		public void TransferTypeIsEnforcedOnlyBySender_SoAServerAcceptsAServerToClientUpdate()
		{
			// ServerToClient stops the *local* SendValue from transmitting. It is
			// not checked on arrival, so an update that reaches the server is
			// applied and then fanned back out to every client.
			GivenServer();
			NetSync<int> property = new NetSync<int>(new TestSessionComponent(), TransferType.ServerToClient, 1, syncOnLoad: false);
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 999, from: ClientId));

			Assert.Equal(999, property.Value);
			Assert.Single(Game.Sent, p => p.Target == PacketTarget.Others);
		}

		[Fact]
		public void AClientAppliesAnUpdateWithoutCheckingItCameFromTheServer()
		{
			// The non-secure handler carries no "from server" flag, so a client
			// applies any property packet that reaches it.
			GivenClient();
			NetSync<int> property = new NetSync<int>(new TestSessionComponent(), TransferType.ServerToClient, 1, syncOnLoad: false);

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 42, from: 12345));

			Assert.Equal(42, property.Value);
		}
	}
}
