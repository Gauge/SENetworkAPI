using System;
using System.Collections.Generic;
using System.Linq;
using SEStubs;
using SENetworkAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;
using Xunit;

// SENetworkAPI keeps process-wide static state (NetworkAPI.Instance, the NetSync
// registries, MyAPIGateway itself). Tests must therefore not run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SENetworkAPI.Tests
{
	/// <summary>
	/// Base fixture: stands up a fake game session, wipes every piece of static
	/// state SENetworkAPI owns before each test, and tears it down afterwards.
	/// </summary>
	public abstract class NetworkTestBase : IDisposable
	{
		protected const ushort ComId = 1234;
		protected const string ModName = "TestMod";
		protected const ulong HostId = 100;
		protected const ulong ClientId = 200;

		protected FakeGame Game;

		protected NetworkTestBase()
		{
			ResetStaticState();
		}

		public virtual void Dispose()
		{
			Game?.Dispose();
			ResetStaticState();
		}

		/// <summary>
		/// SENetworkAPI's statics survive between tests unless explicitly cleared.
		/// NetSync.generatorId is reset so property ids are deterministic.
		/// </summary>
		protected static void ResetStaticState()
		{
			NetworkAPI.Instance = null;
			NetworkAPI.LogNetworkTraffic = false;
			NetworkAPI.CompressionThreshold = 1024;
			// The production reset, so the suite exercises it and cannot drift
			// from it (it also clears the pending coalesced batch).
			NetSync.ClearRegistries();
			MyLog.Default.Clear();
		}

		// -------------------------------------------------------------------
		//  Session setup helpers
		// -------------------------------------------------------------------

		/// <summary>Listen server (host plays in the world).</summary>
		protected Server GivenServer(string keyword = null, ulong hostSteamId = HostId)
		{
			Game = FakeGame.StartServer(hostSteamId);
			NetworkAPI.Init(ComId, ModName, keyword);
			return (Server)NetworkAPI.Instance;
		}

		/// <summary>Dedicated server (no local player, no chat UI).</summary>
		protected Server GivenDedicatedServer(string keyword = null)
		{
			Game = FakeGame.StartDedicatedServer();
			NetworkAPI.Init(ComId, ModName, keyword);
			return (Server)NetworkAPI.Instance;
		}

		/// <summary>Remote client.</summary>
		protected Client GivenClient(string keyword = null, ulong steamId = ClientId)
		{
			Game = FakeGame.StartClient(steamId);
			NetworkAPI.Init(ComId, ModName, keyword);
			return (Client)NetworkAPI.Instance;
		}

		/// <summary>
		/// Tears the current instance down completely so a fresh one can be
		/// started in a different role. Used by the end-to-end tests to play
		/// both sides of a conversation in sequence.
		/// </summary>
		protected void Restart()
		{
			Game?.Dispose();
			Game = null;
			ResetStaticState();
		}

		/// <summary>A fake game with no NetworkAPI instance initialized.</summary>
		protected FakeGame GivenUninitializedClient(ulong steamId = ClientId)
		{
			Game = FakeGame.StartClient(steamId);
			return Game;
		}

		// -------------------------------------------------------------------
		//  Packet inspection helpers
		// -------------------------------------------------------------------

		/// <summary>Decodes a captured packet back into the Command it carries.</summary>
		internal static Command DecodeCommand(SentPacket packet)
		{
			Command cmd = StubSerializer.Deserialize<Command>(packet.Data);
			if (cmd.IsCompressed && cmd.Data != null)
			{
				cmd.Data = MyCompression.Decompress(cmd.Data);
				cmd.IsCompressed = false;
			}

			return cmd;
		}

		/// <summary>Decodes the property payload carried by a captured packet.</summary>
		internal static SyncData DecodeSyncData(SentPacket packet)
		{
			List<SyncData> properties = DecodeSyncDataList(packet);
			Assert.Single(properties);
			return properties[0];
		}

		/// <summary>Decodes every property update in a captured packet, in either layout.</summary>
		internal static List<SyncData> DecodeSyncDataList(SentPacket packet)
		{
			Command cmd = DecodeCommand(packet);
			Assert.True(cmd.IsProperty, "Packet is not a property packet");

			if (cmd.Property != null)
			{
				return new List<SyncData> { cmd.Property };
			}

			if (cmd.Properties != null)
			{
				return cmd.Properties;
			}

			return new List<SyncData> { StubSerializer.Deserialize<SyncData>(cmd.Data) };
		}

		internal Command TheOnlyCommandSent()
		{
			Assert.Single(Game.Sent);
			return DecodeCommand(Game.Sent[0]);
		}

		internal SyncData TheOnlySyncDataSent()
		{
			Assert.Single(Game.Sent);
			return DecodeSyncData(Game.Sent[0]);
		}

		internal IEnumerable<Command> AllCommandsSent() => Game.Sent.Select(DecodeCommand);

		// -------------------------------------------------------------------
		//  Packet construction helpers (simulate the far side of the wire)
		// -------------------------------------------------------------------

		/// <summary>Builds the wire bytes for a command packet arriving from the network.</summary>
		protected static byte[] EncodeCommandPacket(string commandString = null, string message = null, byte[] data = null, ulong from = 0, long? timestamp = null, bool compress = false)
		{
			Command cmd = new Command {
				CommandString = commandString,
				Message = message,
				Data = data,
				SteamId = from,
				Timestamp = timestamp ?? DateTime.UtcNow.Ticks,
			};

			if (compress && cmd.Data != null)
			{
				cmd.Data = MyCompression.Compress(cmd.Data);
				cmd.IsCompressed = true;
			}

			return StubSerializer.Serialize(cmd);
		}

		/// <summary>Builds the wire bytes for a property packet arriving from the network.</summary>
		protected static byte[] EncodePropertyPacket(long id, long entityId, SyncType syncType, object value = null, ulong from = 0, long? timestamp = null)
		{
			SyncData sync = new SyncData {
				Id = id,
				EntityId = entityId,
				SyncType = syncType,
				Data = value == null ? null : SerializeValue(value),
			};

			Command cmd = new Command {
				IsProperty = true,
				Property = sync,
				SteamId = from,
				Timestamp = timestamp ?? DateTime.UtcNow.Ticks,
			};

			return StubSerializer.Serialize(cmd);
		}

		/// <summary>
		/// Builds a property packet in the original layout, where the update was
		/// a serialized SyncData in Command.Data.
		/// </summary>
		protected static byte[] EncodeLegacyPropertyPacket(long id, long entityId, SyncType syncType, object value = null, ulong from = 0)
		{
			SyncData sync = new SyncData {
				Id = id,
				EntityId = entityId,
				SyncType = syncType,
				Data = value == null ? null : SerializeValue(value),
			};

			Command cmd = new Command {
				IsProperty = true,
				Data = StubSerializer.Serialize(sync),
				SteamId = from,
				Timestamp = DateTime.UtcNow.Ticks,
			};

			return StubSerializer.Serialize(cmd);
		}

		private static byte[] SerializeValue(object value)
		{
			// Dispatch to the generic serializer using the value's runtime type,
			// mirroring how NetSync<T> serializes its payload.
			return (byte[])typeof(StubSerializer)
				.GetMethod(nameof(StubSerializer.Serialize))
				.MakeGenericMethod(value.GetType())
				.Invoke(null, new[] { value });
		}

		/// <summary>Pushes raw bytes into the mod's registered message handler.</summary>
		protected void Receive(byte[] packet) => Game.Multiplayer.Deliver(ComId, packet);

		// -------------------------------------------------------------------
		//  Log helpers
		// -------------------------------------------------------------------

		protected static bool LoggedError(string fragment) => MyLog.Default.Contains(LogSeverity.Error, fragment);
		protected static bool LoggedWarning(string fragment) => MyLog.Default.Contains(LogSeverity.Warning, fragment);
		protected static bool LoggedInfo(string fragment) => MyLog.Default.Contains(LogSeverity.Info, fragment);
	}
}
