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
	/// Receiving property traffic: RouteMessage, SetNetworkValue and fetch
	/// request handling.
	/// </summary>
	public class NetSyncNetworkTests : NetworkTestBase
	{
		private class TestSessionComponent : MySessionComponentBase { }

		private NetSync<int> SessionProperty(TransferType transfer = TransferType.Both, int start = 0)
			=> new NetSync<int>(new TestSessionComponent(), transfer, start, syncOnLoad: false);

		// -------------------------------------------------------------------
		//  Applying incoming values
		// -------------------------------------------------------------------

		[Fact]
		public void AnIncomingUpdate_ReplacesTheValue()
		{
			GivenClient();
			NetSync<int> property = SessionProperty(start: 1);

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 42, from: HostId));

			Assert.Equal(42, property.Value);
		}

		[Fact]
		public void AnIncomingUpdate_RaisesBothChangeEvents()
		{
			GivenClient();
			NetSync<int> property = SessionProperty(start: 1);
			int changedOld = -1, changedNew = -1;
			int networkOld = -1, networkNew = -1;
			ulong networkSender = 0;
			property.ValueChanged += (o, n) => { changedOld = o; changedNew = n; };
			property.ValueChangedByNetwork += (o, n, s) => { networkOld = o; networkNew = n; networkSender = s; };

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 42, from: HostId));

			Assert.Equal(1, changedOld);
			Assert.Equal(42, changedNew);
			Assert.Equal(1, networkOld);
			Assert.Equal(42, networkNew);
			Assert.Equal(HostId, networkSender);
		}

		[Fact]
		public void AnIncomingUpdate_RecordsTheMessageTimestamp()
		{
			GivenClient();
			NetSync<int> property = SessionProperty();
			long ticks = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks;

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 42, timestamp: ticks));

			Assert.Equal(ticks, property.LastMessageTimestamp);
		}

		[Fact]
		public void AnIncomingUpdate_ForAnEntityProperty_IsRoutedByEntityIdAndIndex()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> first = new NetSync<int>(entity, TransferType.Both, syncOnLoad: false);
			NetSync<int> second = new NetSync<int>(entity, TransferType.Both, syncOnLoad: false);

			Receive(EncodePropertyPacket(1, entity.EntityId, SyncType.Post, 42));

			Assert.Equal(0, first.Value);
			Assert.Equal(42, second.Value);
		}

		[Fact]
		public void TheServer_RebroadcastsValuesItReceives()
		{
			GivenServer();
			NetSync<int> property = SessionProperty();
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 42, from: ClientId));

			Assert.Equal(42, property.Value);
			SentPacket relay = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Others, relay.Target);
			Assert.Equal(42, StubSerializer.Deserialize<int>(DecodeSyncData(relay).Data));
		}

		[Fact]
		public void AClient_DoesNotEchoValuesItReceives()
		{
			GivenClient();
			NetSync<int> property = SessionProperty();
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 42, from: HostId));

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void AnUndeserialisablePayload_LeavesTheValueAloneAndLogs()
		{
			GivenClient();
			NetSync<int> property = SessionProperty(start: 7);
			Game.Utilities.FailDeserializeFor = t => t == typeof(int);

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Post, 42));

			Assert.Equal(7, property.Value);
			Assert.True(LoggedError("Failed to deserialize network property data"));
		}

		[Fact]
		public void APacketInTheOriginalLayoutIsStillUnderstood()
		{
			// Builds from before the flattened layout put the update in
			// Command.Data as an encoded SyncData. Those packets still route.
			GivenClient();
			NetSync<int> property = SessionProperty(start: 1);

			Receive(EncodeLegacyPropertyPacket(property.Id, 0, SyncType.Post, 42, from: HostId));

			Assert.Equal(42, property.Value);
		}

		[Fact]
		public void OutgoingPacketsUseTheFlattenedLayout()
		{
			GivenClient();
			NetSync<int> property = SessionProperty();
			Game.ClearTraffic();

			property.Value = 42;

			Command cmd = TheOnlyCommandSent();
			Assert.NotNull(cmd.Property);
			Assert.Null(cmd.Data);
			Assert.Equal(42, StubSerializer.Deserialize<int>(cmd.Property.Data));
		}

		[Fact]
		public void ABadEntryInABatchDoesNotStopTheRest()
		{
			// Batches make one packet responsible for several properties, so a
			// single unroutable entry must not take the others down with it.
			GivenClient();
			NetSync<int> first = SessionProperty();
			NetSync<int> second = SessionProperty();

			Command cmd = new Command {
				IsProperty = true,
				SteamId = HostId,
				Timestamp = DateTime.UtcNow.Ticks,
				Properties = new List<SyncData> {
					new SyncData { Id = first.Id, SyncType = SyncType.Post, Data = StubSerializer.Serialize(11) },
					new SyncData { Id = 9999, SyncType = SyncType.Post, Data = StubSerializer.Serialize(22) },
					new SyncData { Id = second.Id, SyncType = SyncType.Post, Data = StubSerializer.Serialize(33) },
				},
			};

			Receive(StubSerializer.Serialize(cmd));

			Assert.Equal(11, first.Value);
			Assert.Equal(33, second.Value);
			Assert.True(LoggedInfo("id not registered in dictionary"));
		}

		[Fact]
		public void AnEmptyBatchIsHarmless()
		{
			GivenClient();

			Exception thrown = Record.Exception(() => Receive(StubSerializer.Serialize(
				new Command { IsProperty = true, Properties = new List<SyncData>(), Timestamp = DateTime.UtcNow.Ticks })));

			Assert.Null(thrown);
		}

		// -------------------------------------------------------------------
		//  Routing failures
		// -------------------------------------------------------------------

		[Fact]
		public void NullPropertyData_IsLoggedAndIgnored()
		{
			GivenClient();

			NetSync<object>.RouteMessage(null, 0, 0);

			Assert.True(LoggedError("Property data is null"));
		}

		[Fact]
		public void AnUnknownPropertyId_IsIgnored()
		{
			GivenClient();
			SessionProperty();

			Exception thrown = Record.Exception(() => Receive(EncodePropertyPacket(9999, 0, SyncType.Post, 42)));

			Assert.Null(thrown);
			Assert.True(LoggedInfo("id not registered in dictionary"));
		}

		[Fact]
		public void AnUnknownEntityId_IsIgnored()
		{
			GivenClient();

			Receive(EncodePropertyPacket(0, 123456, SyncType.Post, 42));

			Assert.True(LoggedInfo("Failed to get entity by id"));
		}

		[Fact]
		public void AnEntityThatIsNotAMyEntity_IsIgnored()
		{
			// GetEntityById hands back an IMyEntity; a few of them are not
			// MyEntity, and a hard cast would take down the whole packet.
			GivenClient();
			Game.Entities.AddForeign(4242);

			Exception thrown = Record.Exception(() => Receive(EncodePropertyPacket(0, 4242, SyncType.Post, 1)));

			Assert.Null(thrown);
			Assert.True(LoggedInfo("Failed to get entity by id"));
			Assert.False(LoggedError("Failure in message processing"));
		}

		[Fact]
		public void AThrowingFetchHookStillAnswersTheFetch()
		{
			GivenServer();
			NetSync<int> property = SessionProperty(start: 77);
			property.BeforeFetchRequestResponse += id => { throw new InvalidOperationException("mod bug"); };
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, from: ClientId));
			Game.NextFrame();

			Assert.Single(Game.Sent);
			Assert.Equal(77, StubSerializer.Deserialize<int>(DecodeSyncData(Game.Sent[0]).Data));
			Assert.True(LoggedError("BeforeFetchRequestResponse"));
		}

		[Fact]
		public void AThrowingFetchHookInABatchDoesNotStopTheOtherUpdates()
		{
			GivenServer();
			NetSync<int> broken = SessionProperty(start: 1);
			NetSync<int> other = SessionProperty(start: 2);
			broken.BeforeFetchRequestResponse += id => { throw new InvalidOperationException("mod bug"); };

			Command cmd = new Command {
				IsProperty = true,
				SteamId = ClientId,
				Timestamp = DateTime.UtcNow.Ticks,
				Properties = new List<SyncData> {
					new SyncData { Id = broken.Id, SyncType = SyncType.Fetch },
					new SyncData { Id = other.Id, SyncType = SyncType.Post, Data = StubSerializer.Serialize(99) },
				},
			};

			Receive(StubSerializer.Serialize(cmd));
			Game.NextFrame();

			Assert.Equal(99, other.Value);
		}

		[Fact]
		public void AnEntityWithNoPropertiesOnIt_IsIgnored()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();

			Receive(EncodePropertyPacket(0, entity.EntityId, SyncType.Post, 42));

			Assert.True(LoggedInfo("Entity not registered in dictionary"));
		}

		[Fact]
		public void APropertyIndexPastTheEndOfTheList_IsIgnored()
		{
			// Happens when client and server disagree on declaration order, e.g.
			// after a mod update that adds a property.
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			new NetSync<int>(entity, TransferType.Both, syncOnLoad: false);

			Receive(EncodePropertyPacket(5, entity.EntityId, SyncType.Post, 42));

			Assert.True(LoggedInfo("property index out of range"));
		}

		// -------------------------------------------------------------------
		//  Fetch requests
		// -------------------------------------------------------------------

		[Fact]
		public void AFetchRequest_IsAnsweredWithTheCurrentValue()
		{
			GivenServer();
			NetSync<int> property = SessionProperty(start: 99);
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, from: ClientId));
			Game.NextFrame();

			SentPacket reply = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Direct, reply.Target);
			Assert.Equal(ClientId, reply.Recipient);
			SyncData sync = DecodeSyncData(reply);
			Assert.Equal(SyncType.Post, sync.SyncType);
			Assert.Equal(99, StubSerializer.Deserialize<int>(sync.Data));
		}

		[Fact]
		public void AFetchRequest_RunsBeforeFetchRequestResponseFirst()
		{
			GivenServer();
			NetSync<int> property = SessionProperty(start: 1);
			ulong requester = 0;
			property.BeforeFetchRequestResponse += id => { requester = id; property.SetValue(500); };
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, from: ClientId));
			Game.NextFrame();

			Assert.Equal(ClientId, requester);
			// The hook's edit is visible in the reply.
			Assert.Equal(500, StubSerializer.Deserialize<int>(DecodeSyncData(Game.Sent[0]).Data));
		}

		[Fact]
		public void AFetchRequest_DoesNotOverwriteTheLocalValue()
		{
			GivenServer();
			NetSync<int> property = SessionProperty(start: 99);

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, value: 1, from: ClientId));

			Assert.Equal(99, property.Value);
		}

		[Fact]
		public void AFetchRequest_DoesNotRaiseChangeEvents()
		{
			GivenServer();
			NetSync<int> property = SessionProperty(start: 99);
			bool raised = false;
			property.ValueChanged += (o, n) => raised = true;
			property.ValueChangedByNetwork += (o, n, s) => raised = true;

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, from: ClientId));

			Assert.False(raised);
		}

		[Fact]
		public void AFetchRequest_ForAnEntityProperty_RepliesToTheRequesterOnly()
		{
			GivenServer();
			MyEntity entity = Game.CreateEntity(new Vector3D(100, 0, 0));
			Game.Players.Add(ClientId, new Vector3D(1000000, 0, 0));  // far away, still answered
			NetSync<int> property = new NetSync<int>(entity, TransferType.Both, 33, syncOnLoad: false);
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, entity.EntityId, SyncType.Fetch, from: ClientId));
			Game.NextFrame();

			SentPacket reply = Assert.Single(Game.Sent);
			Assert.Equal(ClientId, reply.Recipient);
			Assert.Equal(33, StubSerializer.Deserialize<int>(DecodeSyncData(reply).Data));
		}

		[Fact]
		public void AServerToClientProperty_CanStillAnswerAFetch()
		{
			GivenServer();
			NetSync<int> property = SessionProperty(TransferType.ServerToClient, start: 12);
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, from: ClientId));
			Game.NextFrame();

			Assert.Single(Game.Sent);
		}

		[Fact]
		public void AClientReceivingAFetchRequest_AlsoAnswersIt()
		{
			// RouteMessage does not check the local role, so a client that is
			// asked for a value will reply. Only reachable if a mod sends a
			// fetch packet from the server side.
			GivenClient();
			NetSync<int> property = SessionProperty(start: 5);
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, from: HostId));
			Game.NextFrame();

			Assert.Single(Game.Sent);
			Assert.Equal(SyncType.Post, DecodeSyncData(Game.Sent[0]).SyncType);
		}

		// -------------------------------------------------------------------
		//  Fetch requests going out
		// -------------------------------------------------------------------

		// -------------------------------------------------------------------
		//  Fetch batching
		// -------------------------------------------------------------------

		[Fact]
		public void EveryFetchInAFrameSharesOnePacket()
		{
			// Joining a world means every property on every block that streams
			// in asks for its value at once.
			GivenClient();
			MyEntity first = Game.CreateEntity();
			MyEntity second = Game.CreateEntity();
			NetSync<int>[] properties = new NetSync<int>[6] {
				new NetSync<int>(first, TransferType.Both, 0, syncOnLoad: false),
				new NetSync<int>(first, TransferType.Both, 0, syncOnLoad: false),
				new NetSync<int>(first, TransferType.Both, 0, syncOnLoad: false),
				new NetSync<int>(second, TransferType.Both, 0, syncOnLoad: false),
				new NetSync<int>(second, TransferType.Both, 0, syncOnLoad: false),
				new NetSync<int>(second, TransferType.Both, 0, syncOnLoad: false),
			};
			Game.ClearTraffic();

			foreach (NetSync<int> property in properties)
			{
				property.Fetch();
			}

			Game.NextFrame();

			SentPacket packet = Assert.Single(Game.Sent);
			List<SyncData> requests = DecodeSyncDataList(packet);
			Assert.Equal(6, requests.Count);
			Assert.All(requests, r => Assert.Equal(SyncType.Fetch, r.SyncType));
		}

		[Fact]
		public void SyncOnLoadFetchesShareOnePacketAsAGridStreamsIn()
		{
			GivenClient();
			MyEntity[] blocks = new MyEntity[10];

			for (int b = 0; b < blocks.Length; b++)
			{
				blocks[b] = Game.CreateEntity();
				new NetSync<int>(blocks[b], TransferType.Both);
				new NetSync<int>(blocks[b], TransferType.Both);
			}

			Game.ClearTraffic();

			foreach (MyEntity block in blocks)
			{
				block.AddToScene();
			}

			Game.NextFrame();

			Assert.Equal(20, DecodeSyncDataList(Assert.Single(Game.Sent)).Count);
		}

		[Fact]
		public void AskingTwiceInOneFrameOnlyAsksOnce()
		{
			GivenClient();
			NetSync<int> property = SessionProperty();
			Game.ClearTraffic();

			property.Fetch();
			property.Fetch();
			property.Fetch();
			Game.NextFrame();

			Assert.Single(DecodeSyncDataList(Assert.Single(Game.Sent)));
		}

		[Fact]
		public void AnswersToOnePlayerShareOnePacket()
		{
			GivenServer();
			NetSync<int> first = SessionProperty(start: 1);
			NetSync<int> second = SessionProperty(start: 2);
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(first.Id, 0, SyncType.Fetch, from: ClientId));
			Receive(EncodePropertyPacket(second.Id, 0, SyncType.Fetch, from: ClientId));
			Game.NextFrame();

			SentPacket reply = Assert.Single(Game.Sent);
			Assert.Equal(ClientId, reply.Recipient);
			List<SyncData> answers = DecodeSyncDataList(reply);
			Assert.Equal(new[] { 1, 2 }, answers.Select(a => StubSerializer.Deserialize<int>(a.Data)).ToArray());
		}

		[Fact]
		public void EachPlayerGetsTheirOwnAnswerPacket()
		{
			GivenServer();
			NetSync<int> property = SessionProperty(start: 7);
			Game.ClearTraffic();

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, from: 201));
			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, from: 202));
			Game.NextFrame();

			Assert.Equal(2, Game.Sent.Count);
			Assert.Equal(new ulong[] { 201, 202 }, Game.Sent.Select(p => p.Recipient).OrderBy(id => id).ToArray());
		}

		[Fact]
		public void ABatchIsSplitSoOnePacketNeverGrowsWithoutBound()
		{
			GivenClient();
			int count = NetSync.MaxUpdatesPerPacket + 10;

			for (int i = 0; i < count; i++)
			{
				SessionProperty().Fetch();
			}

			Game.ClearTraffic();
			Game.NextFrame();

			Assert.Equal(2, Game.Sent.Count);
			Assert.Equal(NetSync.MaxUpdatesPerPacket, DecodeSyncDataList(Game.Sent[0]).Count);
			Assert.Equal(10, DecodeSyncDataList(Game.Sent[1]).Count);
		}

		[Fact]
		public void TheFetchHookRunsWhenTheAnswerIsBuiltNotWhenTheRequestArrives()
		{
			// The point of the hook is to refresh the value before it is read.
			GivenServer();
			NetSync<int> property = SessionProperty(start: 1);
			bool answered = false;
			property.BeforeFetchRequestResponse += id => { answered = true; property.SetValue(42); };

			Receive(EncodePropertyPacket(property.Id, 0, SyncType.Fetch, from: ClientId));
			Assert.False(answered);

			Game.ClearTraffic();
			Game.NextFrame();

			Assert.True(answered);
			Assert.Equal(42, StubSerializer.Deserialize<int>(DecodeSyncData(Assert.Single(Game.Sent)).Data));
		}

		[Fact]
		public void Fetch_SendsThePropertyAddressToTheServer()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> property = new NetSync<int>(entity, TransferType.Both, syncOnLoad: false);
			Game.ClearTraffic();

			property.Fetch();
			Game.NextFrame();

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Server, packet.Target);
			SyncData sync = DecodeSyncData(packet);
			Assert.Equal(SyncType.Fetch, sync.SyncType);
			Assert.Equal(entity.EntityId, sync.EntityId);
			Assert.Equal(0, sync.Id);
		}
	}
}
