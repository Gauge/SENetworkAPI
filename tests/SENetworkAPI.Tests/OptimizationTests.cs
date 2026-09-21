using System;
using System.Collections.Generic;
using System.Linq;
using SEStubs;
using VRage;
using VRage.Game.Components;
using VRageMath;
using Xunit;

namespace SENetworkAPI.Tests
{
	public class OptimizationTests : NetworkTestBase
	{
		private class Session : MySessionComponentBase { }

		[Fact]
		public void ReentrantPushCancelsTheLaterQueuedGroupAndKeepsRecipientsStable()
		{
			GivenServer();
			Game.Players.Add(ClientId, Vector3D.Zero);
			var a = new NetSync<int>(Game.CreateEntity(), TransferType.Both, syncOnLoad: false).Coalesce();
			var b = new NetSync<int>(Game.CreateEntity(), TransferType.Both, syncOnLoad: false).Coalesce();
			a.Value = 1;
			b.Value = 2;
			bool pushed = false;
			Game.Multiplayer.OnPacketSent = packet =>
			{
				if (pushed) return;
				pushed = true;
				b.Push();
			};
			Game.NextFrame();
			Assert.Equal(2, Game.Sent.Count);
			Assert.All(Game.Sent, packet => Assert.Equal(ClientId, packet.Recipient));
			Assert.Equal(new[] { a.Entity.EntityId, b.Entity.EntityId }, Game.Sent.Select(p => DecodeSyncData(p).EntityId));
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void NoRecipientsMeansNoValueSerialization(bool coalesced)
		{
			GivenServer();
			var entity = Game.CreateEntity(new Vector3D(1e9, 0, 0));
			var property = new NetSync<string>(entity, TransferType.Both, "", syncOnLoad: false).Coalesce(coalesced);
			Game.ClearTraffic();
			property.Value = new string('x', 10000);
			Game.NextFrame();
			Assert.Empty(Game.Sent);
			Assert.Equal(0, Game.Utilities.SerializeCallCount);
		}

		[Fact]
		public void ClientsBatchAcrossEntitiesAndSessionProperties()
		{
			GivenClient();
			var a = new NetSync<int>(Game.CreateEntity(), TransferType.Both, syncOnLoad: false).Coalesce();
			var b = new NetSync<int>(Game.CreateEntity(), TransferType.Both, syncOnLoad: false, limitToSyncDistance: false).Coalesce();
			var c = new NetSync<int>(new Session(), TransferType.Both, syncOnLoad: false).Coalesce();
			a.Value = 1; b.Value = 2; c.Value = 3;
			Game.NextFrame();
			var values = DecodeSyncDataList(Assert.Single(Game.Sent));
			Assert.Equal(new[] { 1, 2, 3 }, values.Select(v => StubSerializer.Deserialize<int>(v.Data)));
			Assert.Equal(new[] { a.Entity.EntityId, b.Entity.EntityId, 0 }, values.Select(v => v.EntityId));
		}

		[Fact]
		public void ServerCanBatchUnlimitedEntitiesTogether()
		{
			GivenServer();
			var a = new NetSync<int>(Game.CreateEntity(), TransferType.Both, syncOnLoad: false, limitToSyncDistance: false).Coalesce();
			var b = new NetSync<int>(Game.CreateEntity(), TransferType.Both, syncOnLoad: false, limitToSyncDistance: false).Coalesce();
			a.Value = 1; b.Value = 2;
			Game.NextFrame();
			Assert.Equal(2, DecodeSyncDataList(Assert.Single(Game.Sent)).Count);
		}

		[Fact]
		public void CoalescedRelaysKeepOnlyTheLatestValuesAndNotifyImmediately()
		{
			GivenServer();
			var a = new NetSync<int>(new Session(), TransferType.Both, syncOnLoad: false).Coalesce();
			var b = new NetSync<int>(new Session(), TransferType.Both, syncOnLoad: false).Coalesce();
			int callbacks = 0;
			a.ValueChangedByNetwork += (oldValue, newValue, sender) => { Assert.Equal(ClientId, sender); callbacks++; };
			Receive(EncodePropertyPacket(a.Id, 0, SyncType.Post, 1));
			Receive(EncodePropertyPacket(b.Id, 0, SyncType.Post, 2));
			Receive(EncodePropertyPacket(a.Id, 0, SyncType.Post, 3));
			Assert.Equal(2, callbacks);
			Assert.Empty(Game.Sent);
			Game.NextFrame();
			Assert.Equal(new[] { 3, 2 }, DecodeSyncDataList(Assert.Single(Game.Sent)).Select(v => StubSerializer.Deserialize<int>(v.Data)));
		}

		[Theory]
		[InlineData(false, 2048, 30)]
		[InlineData(true, 240, 30)]
		public void BatchesRespectByteBudgetsAndDeliverEveryValue(bool lossy, int length, int count)
		{
			GivenClient(steamId: ulong.MaxValue);
			for (int i = 0; i < count; i++)
			{
				var property = new NetSync<byte[]>(new Session(), TransferType.Both, syncOnLoad: false).Coalesce().Lossy(lossy);
				byte[] value = new byte[length];
				new Random(i).NextBytes(value);
				property.Value = value;
			}
			Game.NextFrame();
			Assert.True(Game.Sent.Count > 1);
			int limit = lossy ? NetworkAPI.UnreliableMessageLimit : NetSync.ReliableBatchByteLimit;
			Assert.All(Game.Sent, p => { Assert.InRange(p.Data.Length, 1, limit); Assert.Equal(!lossy, p.Reliable); });
			var updates = Game.Sent.SelectMany(DecodeSyncDataList).ToList();
			Assert.Equal(count, updates.Count);
			for (int i = 0; i < count; i++)
			{
				byte[] expected = new byte[length];
				new Random(i).NextBytes(expected);
				Assert.Equal(i + 1, updates[i].Id);
				Assert.Equal(expected, StubSerializer.Deserialize<byte[]>(updates[i].Data));
			}
		}

		[Fact]
		public void OversizeLossyValueFallsBackToReliableWithoutTruncation()
		{
			GivenClient();
			byte[] value = new byte[30000];
			new Random(1).NextBytes(value);
			var property = new NetSync<byte[]>(new Session(), TransferType.Both, syncOnLoad: false).Coalesce().Lossy();
			property.Value = value;
			Game.NextFrame();
			var packet = Assert.Single(Game.Sent);
			Assert.True(packet.Reliable);
			Assert.Equal(value, StubSerializer.Deserialize<byte[]>(DecodeSyncData(packet).Data));
		}

		[Fact]
		public void LargePropertyUsesCompressedLegacyLayoutAndRoundTrips()
		{
			GivenServer();
			string value = new string('a', 10000);
			var source = new NetSync<string>(new Session(), TransferType.ServerToClient, value, syncOnLoad: false);
			source.Push();
			var packet = Assert.Single(Game.Sent);
			var envelope = StubSerializer.Deserialize<Command>(packet.Data);
			Assert.True(envelope.IsCompressed);
			Assert.Null(envelope.Property);
			Assert.Null(envelope.Properties);
			Assert.True(packet.Data.Length < 1000);
			// This is precisely the existing legacy receive layout, without new fields.
			var legacy = StubSerializer.Deserialize<SyncData>(MyCompression.Decompress(envelope.Data));
			Assert.Equal(value, StubSerializer.Deserialize<string>(legacy.Data));
			Restart();
			GivenClient();
			var target = new NetSync<string>(new Session(), TransferType.ServerToClient, "", syncOnLoad: false);
			Receive(packet.Data);
			Assert.Equal(value, target.Value);
		}

		[Fact]
		public void IncompressiblePropertyKeepsInlineLayout()
		{
			GivenClient();
			byte[] bytes = new byte[10000];
			new Random(123).NextBytes(bytes);
			new NetSync<byte[]>(new Session(), TransferType.Both, bytes, syncOnLoad: false).Push();
			Command envelope = TheOnlyCommandSent();
			Assert.False(envelope.IsCompressed);
			Assert.NotNull(envelope.Property);
			Assert.Equal(bytes, StubSerializer.Deserialize<byte[]>(envelope.Property.Data));
		}

		[Fact]
		public void FetchRepliesToTransportSenderRatherThanForgedRecipient()
		{
			GivenServer();
			var property = new NetSync<int>(new Session(), TransferType.ServerToClient, 42, syncOnLoad: false);
			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, from: 999), senderId: ClientId);
			Game.NextFrame();
			Assert.Equal(ClientId, Assert.Single(Game.Sent).Recipient);
		}

		[Fact]
		public void ClientRejectsNonServerCommandsBeforeDecoding()
		{
			var api = GivenClient();
			bool called = false;
			api.RegisterNetworkCommand("admin", (s, c, d, t) => called = true);
			Receive(EncodeCommandPacket("admin", message: "spoof"), senderId: 999, fromServer: false);
			Assert.False(called);
			Assert.Empty(Game.ShownMessages);
		}

		[Fact]
		public void ClientRejectsServerWritesToClientToServerProperties()
		{
			GivenClient();
			var property = new NetSync<int>(new Session(), TransferType.ClientToServer, 1, syncOnLoad: false);
			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 2));
			Assert.Equal(1, property.Value);
		}
	}
}
