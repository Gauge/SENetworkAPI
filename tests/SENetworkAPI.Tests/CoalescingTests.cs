using System;
using System.Collections.Generic;
using System.Linq;
using SEStubs;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>
	/// Opt-in batching: properties that change in the same frame travel in one
	/// packet instead of one each.
	/// </summary>
	public class CoalescingTests : NetworkTestBase
	{
		private class TestSessionComponent : MySessionComponentBase { }

		private NetSync<int> Coalesced(MyEntity entity = null, int start = 0)
		{
			NetSync<int> property = entity == null
				? new NetSync<int>(new TestSessionComponent(), TransferType.Both, start, syncOnLoad: false)
				: new NetSync<int>(entity, TransferType.Both, start, syncOnLoad: false);

			return property.Coalesce();
		}

		[Fact]
		public void NothingIsSentUntilTheFrameEnds()
		{
			GivenClient();
			NetSync<int> property = Coalesced();
			Game.ClearTraffic();

			property.Value = 42;

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void TheUpdateGoesOutOnTheNextFrame()
		{
			GivenClient();
			NetSync<int> property = Coalesced();
			Game.ClearTraffic();

			property.Value = 42;
			Game.NextFrame();

			SyncData sync = TheOnlySyncDataSent();
			Assert.Equal(property.Id, sync.Id);
			Assert.Equal(42, StubSerializer.Deserialize<int>(sync.Data));
		}

		[Fact]
		public void PropertiesOnTheSameEntityShareOnePacket()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> a = Coalesced(entity);
			NetSync<int> b = Coalesced(entity);
			NetSync<int> c = Coalesced(entity);
			Game.ClearTraffic();

			a.Value = 1;
			b.Value = 2;
			c.Value = 3;
			Game.NextFrame();

			SentPacket packet = Assert.Single(Game.Sent);
			List<SyncData> updates = DecodeSyncDataList(packet);
			Assert.Equal(3, updates.Count);
			Assert.Equal(new[] { 1, 2, 3 }, updates.Select(u => StubSerializer.Deserialize<int>(u.Data)).ToArray());
		}

		[Fact]
		public void EachEntityGetsItsOwnPacket()
		{
			GivenClient();
			MyEntity first = Game.CreateEntity();
			MyEntity second = Game.CreateEntity();
			NetSync<int> a = Coalesced(first);
			NetSync<int> b = Coalesced(first);
			NetSync<int> c = Coalesced(second);
			Game.ClearTraffic();

			a.Value = 1;
			b.Value = 2;
			c.Value = 3;
			Game.NextFrame();

			// Two entities are in two places, so they cannot share a packet
			// that is limited by distance from one of them.
			Assert.Equal(2, Game.Sent.Count);
			Assert.Equal(2, DecodeSyncDataList(Game.Sent[0]).Count);
			Assert.Single(DecodeSyncDataList(Game.Sent[1]));
		}

		[Fact]
		public void SessionAndEntityPropertiesDoNotShareAPacket()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> onEntity = Coalesced(entity);
			NetSync<int> onSession = Coalesced();
			Game.ClearTraffic();

			onEntity.Value = 1;
			onSession.Value = 2;
			Game.NextFrame();

			Assert.Equal(2, Game.Sent.Count);
		}

		[Fact]
		public void DistanceLimitedAndUnlimitedPropertiesDoNotShareAPacket()
		{
			// One goes to everyone, the other only to players near the entity.
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> limited = new NetSync<int>(entity, TransferType.Both, 0, syncOnLoad: false).Coalesce();
			NetSync<int> unlimited = new NetSync<int>(entity, TransferType.Both, 0, syncOnLoad: false, limitToSyncDistance: false).Coalesce();
			Game.ClearTraffic();

			limited.Value = 1;
			unlimited.Value = 2;
			Game.NextFrame();

			Assert.Equal(2, Game.Sent.Count);
		}

		[Fact]
		public void APropertyStillAtNullIsLeftOutOfTheBatch()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<string> empty = new NetSync<string>(entity, TransferType.Both, syncOnLoad: false).Coalesce();
			NetSync<string> filled = new NetSync<string>(entity, TransferType.Both, string.Empty, syncOnLoad: false).Coalesce();
			Game.ClearTraffic();

			empty.Value = null;
			filled.Value = "here";
			Game.NextFrame();

			SyncData sync = TheOnlySyncDataSent();
			Assert.Equal("here", StubSerializer.Deserialize<string>(sync.Data));
		}

		[Fact]
		public void AFlushAfterTheEntityClosedIsHarmless()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> property = Coalesced(entity);
			property.Value = 42;

			entity.Close();
			Exception thrown = Record.Exception(() => Game.NextFrame());

			Assert.Null(thrown);
		}

		[Fact]
		public void AlwaysSendStillBatches()
		{
			// The two switches are independent: AlwaysSend turns deduplication
			// off, it does not turn batching off.
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> a = Coalesced(entity).AlwaysSend();
			NetSync<int> b = Coalesced(entity).AlwaysSend();
			Game.ClearTraffic();

			a.Value = 0;
			b.Value = 0;
			Game.NextFrame();

			Assert.Single(Game.Sent);
			Assert.Equal(2, DecodeSyncDataList(Game.Sent[0]).Count);
		}

		[Fact]
		public void LossyAndReliablePropertiesDoNotShareAPacket()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> reliable = Coalesced(entity);
			NetSync<int> lossy = Coalesced(entity).Lossy();
			Game.ClearTraffic();

			reliable.Value = 1;
			lossy.Value = 2;
			Game.NextFrame();

			Assert.Equal(2, Game.Sent.Count);
			Assert.Single(Game.Sent, p => p.Reliable);
			Assert.Single(Game.Sent, p => !p.Reliable);
		}

		[Fact]
		public void OnlyTheLatestValueIsSent()
		{
			GivenClient();
			NetSync<int> property = Coalesced();
			Game.ClearTraffic();

			property.Value = 1;
			property.Value = 2;
			property.Value = 3;
			Game.NextFrame();

			Assert.Equal(3, StubSerializer.Deserialize<int>(TheOnlySyncDataSent().Data));
		}

		[Fact]
		public void ASinglePropertyStillUsesTheCompactLayout()
		{
			// Batching must not make the common case bigger.
			GivenClient();
			NetSync<int> property = Coalesced();
			Game.ClearTraffic();

			property.Value = 42;
			Game.NextFrame();

			Command cmd = TheOnlyCommandSent();
			Assert.NotNull(cmd.Property);
			Assert.Null(cmd.Properties);
		}

		[Fact]
		public void ValueChangedStillFiresImmediately()
		{
			// Only the network send waits; local logic must not.
			GivenClient();
			NetSync<int> property = Coalesced();
			bool raised = false;
			property.ValueChanged += (o, n) => raised = true;

			property.Value = 42;

			Assert.True(raised);
		}

		[Fact]
		public void PushBypassesTheBatch()
		{
			GivenClient();
			NetSync<int> property = Coalesced();
			Game.ClearTraffic();

			property.Push();

			Assert.Single(Game.Sent);
		}

		[Fact]
		public void PushClearsAPendingUpdateSoItIsNotSentTwice()
		{
			GivenClient();
			NetSync<int> property = Coalesced();
			Game.ClearTraffic();

			property.Value = 42;
			property.Push();
			Game.NextFrame();

			Assert.Single(Game.Sent);
		}

		[Fact]
		public void FetchIsNotBatched()
		{
			GivenClient();
			NetSync<int> property = Coalesced();
			Game.ClearTraffic();

			property.Fetch();

			Assert.Equal(SyncType.Fetch, TheOnlySyncDataSent().SyncType);
		}

		[Fact]
		public void AnUnchangedValueNeverEntersTheBatch()
		{
			GivenClient();
			NetSync<int> property = Coalesced(start: 42);
			Game.ClearTraffic();

			property.Value = 42;
			Game.NextFrame();

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void TheDirectionCheckStillApplies()
		{
			GivenClient();
			NetSync<int> property = new NetSync<int>(new TestSessionComponent(), TransferType.ServerToClient, 0, syncOnLoad: false).Coalesce();
			Game.ClearTraffic();

			property.Value = 42;
			Game.NextFrame();

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void OnlyOneFlushIsScheduledPerFrame()
		{
			GivenClient();
			NetSync<int> a = Coalesced();
			NetSync<int> b = Coalesced();

			a.Value = 1;
			b.Value = 2;

			Assert.Single(Game.Utilities.Scheduled);
		}

		[Fact]
		public void BatchingResumesOnTheFollowingFrame()
		{
			GivenClient();
			NetSync<int> property = Coalesced();
			Game.ClearTraffic();

			property.Value = 1;
			Game.NextFrame();
			property.Value = 2;
			Game.NextFrame();

			Assert.Equal(2, Game.Sent.Count);
			Assert.Equal(2, StubSerializer.Deserialize<int>(DecodeSyncData(Game.Sent[1]).Data));
		}

		[Fact]
		public void ABatchedPacketIsRoutedToEveryPropertyOnArrival()
		{
			// The other half: a receiver has to unpack all of them.
			GivenServer();
			Game.Players.Add(201, Vector3D.Zero);
			MyEntity entity = Game.CreateEntity();
			NetSync<int> a = Coalesced(entity);
			NetSync<int> b = Coalesced(entity);
			a.Value = 7;
			b.Value = 9;
			Game.NextFrame();
			byte[] wire = Assert.Single(Game.Sent).Data;

			Restart();
			GivenClient();
			MyEntity clientEntity = Game.CreateEntity();
			NetSync<int> clientA = new NetSync<int>(clientEntity, TransferType.Both, 0, syncOnLoad: false);
			NetSync<int> clientB = new NetSync<int>(clientEntity, TransferType.Both, 0, syncOnLoad: false);

			Receive(wire);

			Assert.Equal(7, clientA.Value);
			Assert.Equal(9, clientB.Value);
		}

		[Fact]
		public void DisposeDropsPendingUpdates()
		{
			GivenClient();
			NetSync<int> property = Coalesced();
			property.Value = 42;

			NetworkAPI.Dispose();
			Exception thrown = Record.Exception(() => Game.NextFrame());

			Assert.Null(thrown);
		}

		[Fact]
		public void AFlushWithoutAnInitialisedApiIsHarmless()
		{
			// Queue while initialised, tear the API down, then let the frame end.
			GivenClient();
			NetSync<int> property = Coalesced();
			property.Value = 42;
			NetworkAPI.Instance = null;

			Exception thrown = Record.Exception(() => Game.NextFrame());

			Assert.Null(thrown);
			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void CoalescingIsOffByDefault()
		{
			GivenClient();
			NetSync<int> property = new NetSync<int>(new TestSessionComponent(), TransferType.Both, 0, syncOnLoad: false);
			Game.ClearTraffic();

			property.Value = 42;

			Assert.Single(Game.Sent);
		}
	}
}
