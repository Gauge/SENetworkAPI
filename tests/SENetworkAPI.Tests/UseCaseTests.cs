using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;
using Sandbox.ModAPI;
using SEStubs;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>
	/// Tests modelled on how the mods next door actually use this API:
	/// BlinkDrive, GrappleHook, GridGarage, KingOfTheHill and the Zeppelin
	/// controller. Each one mirrors a real pattern rather than an isolated
	/// method, so a change that breaks those mods breaks a test here.
	/// </summary>
	public class UseCaseTests : NetworkTestBase
	{
		private class ModSession : MySessionComponentBase { }

		/// <summary>Stands in for the mods' Settings classes.</summary>
		[ProtoContract]
		public class ModSettings
		{
			[ProtoMember(1)] public float Multiplier { get; set; }
			[ProtoMember(2)] public bool Enabled { get; set; }
			[ProtoMember(3)] public string Name { get; set; }

			public static ModSettings Defaults() => new ModSettings { Multiplier = 1f, Enabled = true, Name = "default" };
		}

		/// <summary>Stands in for GrappleHook's ZiplineEntity request payload.</summary>
		[ProtoContract]
		public class ZiplineRequest
		{
			[ProtoMember(1)] public long IdentityId { get; set; }
			[ProtoMember(2)] public int Direction { get; set; }
		}

		// ===================================================================
		//  Server authoritative settings
		//  BlinkDrive Core.cs, GrappleHook WeaponControlLayer.cs, GridGarage
		// ===================================================================

		[Fact]
		public void AJoiningClientFetchesSettingsFromTheServer()
		{
			// new NetSync<Settings>(this, TransferType.ServerToClient, Settings.Load(), true)
			GivenClient();
			new NetSync<ModSettings>(new ModSession(), TransferType.ServerToClient, ModSettings.Defaults());
			Game.NextFrame();
			byte[] request = Assert.Single(Game.Sent).Data;

			Restart();
			GivenServer();
			new NetSync<ModSettings>(new ModSession(), TransferType.ServerToClient,
				new ModSettings { Multiplier = 2.5f, Enabled = false, Name = "server" });
			Game.ClearTraffic();

			Receive(request);
			Game.NextFrame();
			byte[] answer = Assert.Single(Game.Sent).Data;

			Restart();
			GivenClient();
			NetSync<ModSettings> clientSettings = new NetSync<ModSettings>(new ModSession(), TransferType.ServerToClient, ModSettings.Defaults());
			Game.ClearTraffic();

			Receive(answer);

			Assert.Equal(2.5f, clientSettings.Value.Multiplier);
			Assert.False(clientSettings.Value.Enabled);
			Assert.Equal("server", clientSettings.Value.Name);
		}

		[Fact]
		public void AClientCannotPushSettingsBackAtTheServer()
		{
			GivenClient();
			NetSync<ModSettings> settings = new NetSync<ModSettings>(new ModSession(), TransferType.ServerToClient, ModSettings.Defaults(), syncOnLoad: false);
			Game.ClearTraffic();

			settings.Value = new ModSettings { Multiplier = 99f };

			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void BlockSettingsReachPlayersOutsideSyncDistance()
		{
			// GrappleHook: new NetSync<Settings>(this, ServerToClient, ..., true, false)
			// Settings belong to the whole world, so distance must not gate them.
			GivenServer();
			MyEntity block = Game.CreateEntity(Vector3D.Zero);
			Game.Players.Add(201, new Vector3D(500000, 0, 0));
			NetSync<ModSettings> settings = new NetSync<ModSettings>(block, TransferType.ServerToClient,
				ModSettings.Defaults(), syncOnLoad: true, limitToSyncDistance: false);
			Game.ClearTraffic();

			settings.Value = new ModSettings { Multiplier = 3f };

			Assert.Equal(PacketTarget.Others, Assert.Single(Game.Sent).Target);
		}

		// ===================================================================
		//  A block carrying a lot of properties
		//  KingOfTheHill ZoneBlock.cs declares about thirty
		// ===================================================================

		private static NetSync<int>[] DeclareZoneBlockProperties(MyEntity block, int count = 30)
		{
			NetSync<int>[] properties = new NetSync<int>[count];

			for (int i = 0; i < count; i++)
			{
				properties[i] = new NetSync<int>(block, TransferType.Both, i);
			}

			return properties;
		}

		[Fact]
		public void AZoneBlockStreamingInAsksForEveryValueInOnePacket()
		{
			GivenClient();
			MyEntity zone = Game.CreateEntity();
			DeclareZoneBlockProperties(zone);
			Game.ClearTraffic();

			zone.AddToScene();
			Game.NextFrame();

			Assert.Equal(30, DecodeSyncDataList(Assert.Single(Game.Sent)).Count);
		}

		[Fact]
		public void TheServerAnswersAWholeZoneBlockInOnePacket()
		{
			GivenServer();
			MyEntity zone = Game.CreateEntity(Vector3D.Zero);
			NetSync<int>[] properties = DeclareZoneBlockProperties(zone);
			Game.ClearTraffic();

			for (int i = 0; i < properties.Length; i++)
			{
				Receive(EncodePropertyPacket(i, zone.EntityId, SyncType.Fetch, from: ClientId));
			}

			Game.NextFrame();

			SentPacket answer = Assert.Single(Game.Sent);
			Assert.Equal(ClientId, answer.Recipient);
			Assert.Equal(30, DecodeSyncDataList(answer).Count);
		}

		[Fact]
		public void ZoneBlockPropertiesLandOnTheirCounterpartsAcrossTheWire()
		{
			// Thirty properties addressed purely by declaration order.
			GivenServer();
			MyEntity zone = Game.CreateEntity(Vector3D.Zero);
			Game.Players.Add(201, Vector3D.Zero);
			NetSync<int>[] serverSide = DeclareZoneBlockProperties(zone);
			Game.ClearTraffic();

			serverSide[17].Value = 4242;
			byte[] wire = Assert.Single(Game.Sent).Data;

			Restart();
			GivenClient();
			MyEntity clientZone = Game.CreateEntity(Vector3D.Zero);
			NetSync<int>[] clientSide = DeclareZoneBlockProperties(clientZone);

			Receive(wire);

			Assert.Equal(4242, clientSide[17].Value);
			Assert.Equal(16, clientSide[16].Value);
			Assert.Equal(18, clientSide[18].Value);
		}

		[Fact]
		public void AZoneBlockUpdatingSeveralValuesAtOnceCanShareOnePacket()
		{
			GivenServer();
			MyEntity zone = Game.CreateEntity(Vector3D.Zero);
			Game.Players.Add(201, Vector3D.Zero);
			NetSync<float> progress = new NetSync<float>(zone, TransferType.Both, 0, syncOnLoad: false).Coalesce();
			NetSync<long> controlledBy = new NetSync<long>(zone, TransferType.Both, 0, syncOnLoad: false).Coalesce();
			NetSync<int> points = new NetSync<int>(zone, TransferType.Both, 0, syncOnLoad: false).Coalesce();
			Game.ClearTraffic();

			progress.Value = 0.5f;
			controlledBy.Value = 12345;
			points.Value = 3;
			Game.NextFrame();

			Assert.Equal(3, DecodeSyncDataList(Assert.Single(Game.Sent)).Count);
		}

		// ===================================================================
		//  Flags used as triggers
		//  BlinkDrive BlinkNextFrame, GrappleHook ResetIndicator
		// ===================================================================

		[Fact]
		public void ATriggerFlagCanBeFiredRepeatedly()
		{
			// BlinkDrive sets the flag to fire, then clears it locally with
			// SyncType.None once it has acted. The clear is what makes the next
			// press a change again.
			GivenClient();
			NetSync<bool> blinkNextFrame = new NetSync<bool>(new ModSession(), TransferType.Both, false, syncOnLoad: false);
			Game.ClearTraffic();

			blinkNextFrame.Value = true;
			Assert.Single(Game.Sent);

			blinkNextFrame.SetValue(false, SyncType.None);
			Assert.Single(Game.Sent);

			blinkNextFrame.Value = true;
			Assert.Equal(2, Game.Sent.Count);
		}

		[Fact]
		public void AToggledFlagAlwaysTravels()
		{
			// GrappleHook: ResetIndicator.Value = !ResetIndicator.Value
			GivenClient();
			NetSync<bool> reset = new NetSync<bool>(new ModSession(), TransferType.ClientToServer, false, syncOnLoad: false);
			Game.ClearTraffic();

			for (int i = 0; i < 4; i++)
			{
				reset.Value = !reset.Value;
			}

			Assert.Equal(4, Game.Sent.Count);
		}

		[Fact]
		public void AFlagSetToTheValueItAlreadyHoldsSendsNothing()
		{
			// The flip side, and the reason BlinkDrive's local clear matters.
			GivenClient();
			NetSync<bool> flag = new NetSync<bool>(new ModSession(), TransferType.Both, false, syncOnLoad: false);
			Game.ClearTraffic();

			flag.Value = true;
			flag.Value = true;
			flag.Value = true;

			Assert.Single(Game.Sent);
		}

		// ===================================================================
		//  A property used as a request channel
		//  GrappleHook RequestZiplineActivation / RequestZiplineDisconnect
		// ===================================================================

		[Fact]
		public void ARequestObjectTravelsEvenWhenItsContentsRepeat()
		{
			// new NetSync<ZiplineEntity>(this, ClientToServer, new ZiplineEntity(), false)
			// Each assignment is a fresh request, even for identical contents.
			GivenClient();
			NetSync<ZiplineRequest> request = new NetSync<ZiplineRequest>(new ModSession(),
				TransferType.ClientToServer, new ZiplineRequest(), syncOnLoad: false);
			Game.ClearTraffic();

			request.Value = new ZiplineRequest { IdentityId = 7, Direction = 1 };
			request.Value = new ZiplineRequest { IdentityId = 7, Direction = 1 };

			Assert.Equal(2, Game.Sent.Count);
		}

		[Fact]
		public void AZiplineRequestArrivesAtTheServerAndRaisesValueChanged()
		{
			GivenClient();
			NetSync<ZiplineRequest> clientSide = new NetSync<ZiplineRequest>(new ModSession(),
				TransferType.ClientToServer, new ZiplineRequest(), syncOnLoad: false);
			clientSide.Value = new ZiplineRequest { IdentityId = 99, Direction = -1 };
			byte[] wire = Assert.Single(Game.Sent).Data;

			Restart();
			GivenServer();
			NetSync<ZiplineRequest> serverSide = new NetSync<ZiplineRequest>(new ModSession(),
				TransferType.ClientToServer, new ZiplineRequest(), syncOnLoad: false);
			ZiplineRequest handled = null;
			serverSide.ValueChanged += (o, n) => handled = n;

			Receive(wire);

			Assert.NotNull(handled);
			Assert.Equal(99, handled.IdentityId);
			Assert.Equal(-1, handled.Direction);
		}

		// ===================================================================
		//  A collection property published by hand
		//  GridGarage GridNames
		// ===================================================================

		[Fact]
		public void AListPropertyPublishesItsContentsOnPush()
		{
			// GridNames.Push() after editing the list in place.
			GivenServer();
			MyEntity garage = Game.CreateEntity(Vector3D.Zero);
			Game.Players.Add(201, Vector3D.Zero);
			NetSync<List<string>> gridNames = new NetSync<List<string>>(garage,
				TransferType.ServerToClient, new List<string>(), syncOnLoad: false);
			Game.ClearTraffic();

			gridNames.Value.Add("Miner MkII");
			gridNames.Value.Add("Hauler");
			gridNames.Push();

			byte[] wire = Assert.Single(Game.Sent).Data;

			Restart();
			GivenClient();
			MyEntity clientGarage = Game.CreateEntity(Vector3D.Zero);
			NetSync<List<string>> clientNames = new NetSync<List<string>>(clientGarage,
				TransferType.ServerToClient, new List<string>(), syncOnLoad: false);

			Receive(wire);

			Assert.Equal(new[] { "Miner MkII", "Hauler" }, clientNames.Value.ToArray());
		}

		[Fact]
		public void AListPropertyReassignedToTheSameInstanceStillTravels()
		{
			// Its contents can differ even when the reference does not.
			GivenClient();
			List<string> names = new List<string> { "one" };
			NetSync<List<string>> gridNames = new NetSync<List<string>>(new ModSession(),
				TransferType.Both, names, syncOnLoad: false);
			Game.ClearTraffic();

			names.Add("two");
			gridNames.Value = names;

			Assert.Single(Game.Sent);
		}

		// ===================================================================
		//  Command request and reply
		//  GridGarage Command_Settings, KingOfTheHill "score"
		// ===================================================================

		[Fact]
		public void AClientAsksForConfigAndOnlyThatClientIsAnswered()
		{
			NetworkAPI client = GivenClient();
			client.SendCommand("settings");
			byte[] request = Assert.Single(Game.Sent).Data;

			Restart();
			NetworkAPI server = GivenServer();
			ModSettings config = new ModSettings { Multiplier = 7f, Name = "live" };
			server.RegisterNetworkCommand("settings", (steamId, command, data, sent) =>
				server.SendCommand("settings", data: MyAPIGateway.Utilities.SerializeToBinary(config), steamId: steamId));
			Game.ClearTraffic();

			Receive(request);

			SentPacket reply = Assert.Single(Game.Sent);
			Assert.Equal(PacketTarget.Direct, reply.Target);
			Assert.Equal(ClientId, reply.Recipient);

			Restart();
			NetworkAPI back = GivenClient();
			ModSettings received = null;
			back.RegisterNetworkCommand("settings", (steamId, command, data, sent) =>
				received = MyAPIGateway.Utilities.SerializeFromBinary<ModSettings>(data));

			Receive(reply.Data);

			Assert.NotNull(received);
			Assert.Equal(7f, received.Multiplier);
			Assert.Equal("live", received.Name);
		}

		// ===================================================================
		//  Chat driven commands
		//  KingOfTheHill Core.cs registers the same words on both sides
		// ===================================================================

		[Fact]
		public void AChatCommandOnAClientBecomesANetworkCommandOnTheServer()
		{
			NetworkAPI client = GivenClient("/koth");
			client.RegisterChatCommand("score", args => client.SendCommand("score"));

			Game.Utilities.SimulateChat("/koth score");
			byte[] wire = Assert.Single(Game.Sent).Data;

			Restart();
			NetworkAPI server = GivenServer("/koth");
			ulong asked = 0;
			server.RegisterNetworkCommand("score", (steamId, command, data, sent) => asked = steamId);

			Receive(wire);

			Assert.Equal(ClientId, asked);
		}

		[Fact]
		public void TheSameChatCommandActsLocallyOnAListenServer()
		{
			NetworkAPI server = GivenServer("/koth");
			bool shown = false;
			server.RegisterChatCommand("score", args => shown = true);
			Game.ClearTraffic();

			Game.Utilities.SimulateChat("/koth score");

			Assert.True(shown);
			Assert.Empty(Game.Sent);
		}

		[Fact]
		public void AChatCommandNobodyRegisteredTellsThePlayer()
		{
			NetworkAPI client = GivenClient("/koth");
			client.RegisterChatCommand("score", args => { });

			Game.Utilities.SimulateChat("/koth explode");

			Assert.Equal("Command not recognized.", Assert.Single(Game.ShownMessages).Text);
		}

		[Fact]
		public void ACommandNameThatDoesNotMatchTheOtherSideIsSilentlyDropped()
		{
			// KingOfTheHill sends "force-load" from the client and listens for
			// "force_load" on the server. Nothing throws and nothing arrives.
			NetworkAPI client = GivenClient("/koth");
			client.RegisterChatCommand("force-load", args => client.SendCommand("force-load"));

			Game.Utilities.SimulateChat("/koth force-load");
			byte[] wire = Assert.Single(Game.Sent).Data;

			Restart();
			NetworkAPI server = GivenServer("/koth");
			bool handled = false;
			server.RegisterNetworkCommand("force_load", (steamId, command, data, sent) => handled = true);

			Receive(wire);

			Assert.False(handled);
		}

		// ===================================================================
		//  Chat output
		//  KingOfTheHill Network.Say and its one-player messages
		// ===================================================================

		[Fact]
		public void SayReachesEveryClientAndShowsOnTheHost()
		{
			NetworkAPI server = GivenServer();
			Game.ClearTraffic();

			server.Say("PLAYER captured the hill");

			Command sent = TheOnlyCommandSent();
			Assert.Equal("PLAYER captured the hill", sent.Message);
			Assert.Null(sent.CommandString);
			Assert.Single(Game.ShownMessages);
		}

		[Fact]
		public void AMessageAddressedAtOnePlayerShowsOnlyForThem()
		{
			// KingOfTheHill: SendCommand("blank_message", "KotH Saved.", steamId: id)
			NetworkAPI server = GivenServer();
			Game.ClearTraffic();

			server.SendCommand("blank_message", "KotH Saved.", steamId: ClientId);
			SentPacket packet = Assert.Single(Game.Sent);
			Assert.Equal(ClientId, packet.Recipient);

			Restart();
			GivenClient();

			Receive(packet.Data);

			Assert.Equal("KotH Saved.", Assert.Single(Game.ShownMessages).Text);
		}

		[Fact]
		public void AMessageArrivesEvenThoughNothingHandlesItsCommand()
		{
			// "blank_message" is never registered anywhere; the text is the point.
			NetworkAPI client = GivenClient();
			bool handled = false;
			client.OnCommandRecived += (s, c, d, t) => handled = true;

			Receive(EncodeCommandPacket("blank_message", message: "Requires admin rights", from: HostId));

			Assert.Equal("Requires admin rights", Assert.Single(Game.ShownMessages).Text);
			Assert.True(handled);
		}
	}
}
