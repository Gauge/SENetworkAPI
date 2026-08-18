using System;
using System.Collections.Generic;
using SEStubs;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>
	/// Setting values: change events, sync types, and the guards inside SendValue.
	/// </summary>
	public class NetSyncValueTests : NetworkTestBase
	{
		private class TestSessionComponent : MySessionComponentBase { }

		private NetSync<int> Property(TransferType transfer = TransferType.Both, int start = 0)
			=> new NetSync<int>(new TestSessionComponent(), transfer, start, syncOnLoad: false);

		// -------------------------------------------------------------------
		//  Value + events
		// -------------------------------------------------------------------

		[Fact]
		public void AssigningValue_UpdatesTheStoredValue()
		{
			GivenClient();
			NetSync<int> property = Property();

			property.Value = 42;

			Assert.Equal(42, property.Value);
		}

		[Fact]
		public void AssigningValue_RaisesValueChangedWithOldAndNew()
		{
			GivenClient();
			NetSync<int> property = Property(start: 7);
			int oldValue = -1, newValue = -1;
			property.ValueChanged += (o, n) => { oldValue = o; newValue = n; };

			property.Value = 42;

			Assert.Equal(7, oldValue);
			Assert.Equal(42, newValue);
		}

		[Fact]
		public void AssigningValue_DoesNotRaiseValueChangedByNetwork()
		{
			GivenClient();
			NetSync<int> property = Property();
			bool raised = false;
			property.ValueChangedByNetwork += (o, n, s) => raised = true;

			property.Value = 42;

			Assert.False(raised);
		}

		[Fact]
		public void ValueChanged_FiresEvenWhenTheValueIsUnchanged()
		{
			// NetSync does not compare old and new; every assignment is a change.
			GivenClient();
			NetSync<int> property = Property(start: 5);
			int calls = 0;
			property.ValueChanged += (o, n) => calls++;

			property.Value = 5;
			property.Value = 5;

			Assert.Equal(2, calls);
		}

		[Fact]
		public void AssigningValue_BroadcastsAcrossTheNetwork()
		{
			GivenClient();
			NetSync<int> property = Property();
			Game.ClearTraffic();

			property.Value = 42;

			SyncData sync = TheOnlySyncDataSent();
			Assert.Equal(SyncType.Broadcast, sync.SyncType);
			Assert.Equal(42, StubSerializer.Deserialize<int>(sync.Data));
		}

		[Fact]
		public void SetValue_DefaultsToNotSyncing()
		{
			GivenClient();
			NetSync<int> property = Property();
			Game.ClearTraffic();

			property.SetValue(42);

			Assert.Equal(42, property.Value);
			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void SetValue_WithNone_StillRaisesValueChanged()
		{
			GivenClient();
			NetSync<int> property = Property();
			bool raised = false;
			property.ValueChanged += (o, n) => raised = true;

			property.SetValue(42, SyncType.None);

			Assert.True(raised);
		}

		[Fact]
		public void SetValue_WithBroadcast_Syncs()
		{
			GivenClient();
			NetSync<int> property = Property();
			Game.ClearTraffic();

			property.SetValue(42, SyncType.Broadcast);

			Assert.Equal(SyncType.Broadcast, TheOnlySyncDataSent().SyncType);
		}

		[Fact]
		public void Push_SendsTheCurrentValueToEveryone()
		{
			GivenClient();
			NetSync<int> property = Property();
			property.SetValue(42);
			Game.ClearTraffic();

			property.Push();

			SyncData sync = TheOnlySyncDataSent();
			Assert.Equal(SyncType.Broadcast, sync.SyncType);
			Assert.Equal(42, StubSerializer.Deserialize<int>(sync.Data));
		}

		[Fact]
		public void Push_ToASingleUser_UsesTheDirectedPostSyncType()
		{
			GivenServer();
			NetSync<int> property = Property();
			property.SetValue(42);
			Game.ClearTraffic();

			property.Push(ClientId);

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Direct, packet.Target);
			Assert.Equal(ClientId, packet.Recipient);
			Assert.Equal(SyncType.Post, DecodeSyncData(packet).SyncType);
		}

		[Fact]
		public void Push_DoesNotRaiseValueChanged()
		{
			GivenClient();
			NetSync<int> property = Property();
			bool raised = false;
			property.ValueChanged += (o, n) => raised = true;

			property.Push();

			Assert.False(raised);
		}

		[Fact]
		public void ReferenceTypes_SyncTheirCurrentContentsOnPush()
		{
			GivenClient();
			NetSync<List<int>> property = new NetSync<List<int>>(new TestSessionComponent(), TransferType.Both, new List<int>(), syncOnLoad: false);
			property.Value.Add(1);
			property.Value.Add(2);
			Game.ClearTraffic();

			property.Push();

			Assert.Equal(new List<int> { 1, 2 }, StubSerializer.Deserialize<List<int>>(TheOnlySyncDataSent().Data));
		}

		[Fact]
		public void SetValue_OnANullValuedProperty_Works()
		{
			// This used to throw: SetValue locked on the value it was replacing,
			// and Monitor.Enter(null) throws. The lock protected nothing (it
			// boxed a fresh object for value types and the getter was unlocked),
			// so it is gone.
			GivenClient();
			NetSync<string> property = new NetSync<string>(new TestSessionComponent(), TransferType.Both, syncOnLoad: false);
			Game.ClearTraffic();

			property.Value = "hello";

			Assert.Equal("hello", property.Value);
			Assert.Single(Game.Sent);
		}

		[Fact]
		public void AssigningAValueTypeDoesNotBoxOnEveryAssignment()
		{
			// Regression guard for the same lock: `lock (_value)` boxed T on
			// every single assignment.
			GivenClient();
			NetSync<int> property = Property();
			property.SetValue(1);

			long before = GC.GetAllocatedBytesForCurrentThread();
			for (int i = 0; i < 1000; i++)
			{
				property.SetValue(i);
			}

			long perAssignment = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
			Assert.Equal(0, perAssignment);
		}

		[Fact]
		public void SetValue_OnAnInitialisedStringProperty_Works()
		{
			GivenClient();
			NetSync<string> property = new NetSync<string>(new TestSessionComponent(), TransferType.Both, string.Empty, syncOnLoad: false);

			property.Value = "hello";

			Assert.Equal("hello", property.Value);
		}

		// -------------------------------------------------------------------
		//  SendValue guards
		// -------------------------------------------------------------------

		[Fact]
		public void WithoutAnInitialisedNetworkApi_NothingIsSentAndAnErrorIsLogged()
		{
			GivenUninitializedClient();
			NetSync<int> property = Property();

			property.Value = 42;

			Assert.Empty(Game.Sent);
			Assert.Equal(42, property.Value);
			Assert.True(LoggedError("has not been initialized"));
		}

		[Fact]
		public void InOfflineMode_NothingIsSent()
		{
			GivenServer();
			Game.Session.OnlineMode = MyOnlineModeEnum.OFFLINE;
			NetSync<int> property = Property();
			Game.ClearTraffic();

			property.Value = 42;

			Assert.Empty(Game.Sent);
			Assert.Equal(42, property.Value);
		}

		[Fact]
		public void InPrivateMode_DataIsStillSent()
		{
			GivenServer();
			Game.Session.OnlineMode = MyOnlineModeEnum.PRIVATE;
			NetSync<int> property = Property();
			Game.ClearTraffic();

			property.Value = 42;

			Assert.Single(Game.Sent);
		}

		[Fact]
		public void ANullValue_IsNeverTransmitted()
		{
			GivenServer();
			NetSync<string> property = new NetSync<string>(new TestSessionComponent(), TransferType.Both, "start", syncOnLoad: false);
			NetworkAPI.LogNetworkTraffic = true;
			Game.ClearTraffic();

			property.SetValue(null, SyncType.Broadcast);

			Assert.Empty(Game.Sent);
			Assert.True(LoggedError("Cannot transmit null value"));
		}

		[Fact]
		public void ANullValuedProperty_CanStillFetch()
		{
			// A fetch is a request; the null guard only applies to sync types
			// that actually carry a value, so a property sitting at its default
			// can still ask the server for one.
			GivenClient();
			NetSync<string> property = new NetSync<string>(new TestSessionComponent(), TransferType.Both, syncOnLoad: false);
			Game.ClearTraffic();

			property.Fetch();

			Assert.Equal(SyncType.Fetch, TheOnlySyncDataSent().SyncType);
		}

		[Fact]
		public void AFetchCarriesNoPayload()
		{
			// The receiver ignores a fetch's data, so nothing is encoded for it.
			GivenClient();
			NetSync<string> property = new NetSync<string>(new TestSessionComponent(), TransferType.Both, "a value worth several bytes", syncOnLoad: false);
			Game.ClearTraffic();

			property.Fetch();

			Assert.Null(TheOnlySyncDataSent().Data);
		}

		// -------------------------------------------------------------------
		//  Transfer direction gating
		// -------------------------------------------------------------------

		[Theory]
		[InlineData(TransferType.Both, true, true)]
		[InlineData(TransferType.Both, false, true)]
		[InlineData(TransferType.ServerToClient, true, true)]
		[InlineData(TransferType.ServerToClient, false, false)]
		[InlineData(TransferType.ClientToServer, true, false)]
		[InlineData(TransferType.ClientToServer, false, true)]
		public void BroadcastRespectsTheTransferDirection(TransferType transfer, bool isServer, bool expectSend)
		{
			if (isServer)
			{
				GivenServer();
			}
			else
			{
				GivenClient();
			}

			NetSync<int> property = Property(transfer);
			Game.ClearTraffic();

			property.Value = 42;

			Assert.Equal(expectSend ? 1 : 0, Game.Sent.Count);
		}

		[Fact]
		public void AClientMayFetchAServerToClientProperty()
		{
			// Fetch is explicitly exempted from the direction check so that a
			// read-only client can still ask for the authoritative value.
			GivenClient();
			NetSync<int> property = Property(TransferType.ServerToClient);
			Game.ClearTraffic();

			property.Fetch();

			Assert.Equal(SyncType.Fetch, TheOnlySyncDataSent().SyncType);
		}

		[Fact]
		public void AServerNeverFetches()
		{
			GivenServer();
			NetSync<int> property = Property(TransferType.Both);
			Game.ClearTraffic();

			property.Fetch();

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void AServerCannotAnswerAFetchForAClientToServerProperty()
		{
			// Operator precedence in the direction guard means the Fetch
			// exemption does not apply to the ClientToServer branch. Harmless
			// today because servers never originate a fetch, but it also blocks
			// a server from replying to one for such a property.
			GivenServer();
			NetSync<int> property = Property(TransferType.ClientToServer);
			Game.ClearTraffic();

			property.Push(SyncType.Fetch, ClientId);

			Assert.Empty(Game.Sent);
		}

		// -------------------------------------------------------------------
		//  Packet shape
		// -------------------------------------------------------------------

		[Fact]
		public void PropertyPackets_AreFlaggedAsProperties()
		{
			GivenClient();
			NetSync<int> property = Property();
			Game.ClearTraffic();

			property.Value = 42;

			Assert.True(TheOnlyCommandSent().IsProperty);
			Assert.Null(TheOnlyCommandSent().CommandString);
		}

		[Fact]
		public void PropertyPackets_CarryTheLocalPlayerAsSender()
		{
			GivenClient(steamId: 777);
			NetSync<int> property = Property();
			Game.ClearTraffic();

			property.Value = 42;

			Assert.Equal(777UL, TheOnlyCommandSent().SteamId);
		}

		[Fact]
		public void EntityPropertyPackets_CarryTheEntityId()
		{
			GivenClient();
			MyEntity entity = Game.CreateEntity();
			NetSync<int> property = new NetSync<int>(entity, TransferType.Both, syncOnLoad: false);
			Game.ClearTraffic();

			property.Value = 42;

			SyncData sync = TheOnlySyncDataSent();
			Assert.Equal(entity.EntityId, sync.EntityId);
			Assert.Equal(0, sync.Id);
		}

		[Fact]
		public void SessionPropertyPackets_CarryEntityIdZero()
		{
			GivenClient();
			NetSync<int> property = Property();
			Game.ClearTraffic();

			property.Value = 42;

			Assert.Equal(0, TheOnlySyncDataSent().EntityId);
		}

		[Fact]
		public void LimitToSyncDistance_SendsPositionally()
		{
			GivenServer();
			((FakePlayer)Game.Session.Player).Position = new Vector3D(1e9, 0, 0);
			MyEntity entity = Game.CreateEntity(new Vector3D(500, 0, 0));
			Game.Players.Add(201, new Vector3D(520, 0, 0));    // near the entity
			Game.Players.Add(202, new Vector3D(90000, 0, 0));  // far away
			NetSync<int> property = new NetSync<int>(entity, TransferType.Both, syncOnLoad: false);
			Game.ClearTraffic();

			property.Value = 42;

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Direct, packet.Target);
			Assert.Equal(201UL, packet.Recipient);
		}

		[Fact]
		public void WithoutLimitToSyncDistance_TheUpdateIsBroadcastToEverybody()
		{
			GivenServer();
			MyEntity entity = Game.CreateEntity(new Vector3D(500, 0, 0));
			Game.Players.Add(202, new Vector3D(90000, 0, 0));
			NetSync<int> property = new NetSync<int>(entity, TransferType.Both, syncOnLoad: false, limitToSyncDistance: false);
			Game.ClearTraffic();

			property.Value = 42;

			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Others, packet.Target);
		}

		[Fact]
		public void SessionProperties_AlwaysBroadcast_EvenWithLimitToSyncDistance()
		{
			// There is no position to test against without an entity.
			GivenServer();
			NetSync<int> property = new NetSync<int>(new TestSessionComponent(), TransferType.Both, 0, syncOnLoad: false, limitToSyncDistance: true);
			Game.ClearTraffic();

			property.Value = 42;

			Assert.Equal(PacketTarget.Others, Assert.Single(Game.Sent).Target);
		}

		[Fact]
		public void SendingToYourself_IsLoggedAsAnErrorButStillTransmitted()
		{
			// Documented defect: the self-send check logs "data will not be sent"
			// and then falls through and sends anyway. The engine delivers the
			// packet back to the host, whose server-side receive path then
			// re-broadcasts it -- so a push aimed at one player reaches everyone.
			GivenServer(hostSteamId: 100);
			NetSync<int> property = Property();
			Game.ClearTraffic();

			property.Push(100);

			Assert.True(LoggedError("sender id is the same as the recievers id"));
			Assert.Equal(2, Game.Sent.Count);
			Assert.Equal(PacketTarget.Direct, Game.Sent[0].Target);
			Assert.Equal(PacketTarget.Others, Game.Sent[1].Target);
		}
	}
}
