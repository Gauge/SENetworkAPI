using System;
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

			Assert.Single(Game.Sent);
			Assert.Equal(SyncType.Post, DecodeSyncData(Game.Sent[0]).SyncType);
		}

		// -------------------------------------------------------------------
		//  Fetch requests going out
		// -------------------------------------------------------------------

		[Fact]
		public void Fetch_SendsThePropertyAddressToTheServer()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> property = new NetSync<int>(entity, TransferType.Both, syncOnLoad: false);
			Game.ClearTraffic();

			property.Fetch();

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Server, packet.Target);
			SyncData sync = DecodeSyncData(packet);
			Assert.Equal(SyncType.Fetch, sync.SyncType);
			Assert.Equal(entity.EntityId, sync.EntityId);
			Assert.Equal(0, sync.Id);
		}
	}
}
