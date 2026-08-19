using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SEStubs
{
	public enum PacketTarget
	{
		/// <summary>Client -> server (SendMessageToServer).</summary>
		Server,
		/// <summary>Server -> every client except the local one (SendMessageToOthers).</summary>
		Others,
		/// <summary>Server -> one specific client (SendMessageTo).</summary>
		Direct,
	}

	/// <summary>One packet handed to the multiplayer layer.</summary>
	public sealed class SentPacket
	{
		public ushort ComId;
		public byte[] Data;
		public PacketTarget Target;
		public ulong Recipient;
		public bool Reliable;

		public override string ToString() => $"{Target} comId:{ComId} to:{Recipient} bytes:{Data?.Length} reliable:{Reliable}";
	}

	/// <summary>One line pushed to the in-game chat via ShowMessage.</summary>
	public sealed class ShownMessage
	{
		public string Sender;
		public string Text;

		public override string ToString() => $"{Sender}: {Text}";
	}

	public sealed class FakePlayer : IMyPlayer
	{
		public ulong SteamUserId { get; set; }
		public string DisplayName { get; set; } = "Player";
		public Vector3D Position { get; set; }

		public Vector3D GetPosition() => Position;
	}

	public sealed class FakeSession : IMySession
	{
		/// <summary>Null on a dedicated server -- there is no local player.</summary>
		public IMyPlayer Player { get; set; }
		public IMyPlayer LocalHumanPlayer { get; set; }
		public MyObjectBuilder_SessionSettings SessionSettings { get; set; } = new MyObjectBuilder_SessionSettings();
		public MyOnlineModeEnum OnlineMode { get; set; } = MyOnlineModeEnum.PUBLIC;

		/// <summary>Advanced by <see cref="FakeGame.NextFrame"/>.</summary>
		public int GameplayFrameCounter { get; set; }
	}

	public sealed class FakeUtilities : IMyUtilities
	{
		public bool IsDedicated { get; set; }

		public event MessageEnteredDel MessageEntered;

		public readonly List<ShownMessage> ShownMessages = new List<ShownMessage>();

		/// <summary>When set and it returns true for the requested type, deserialization throws.</summary>
		public Predicate<Type> FailDeserializeFor;

		/// <summary>Work scheduled for the next update, drained by FakeGame.NextFrame.</summary>
		public readonly List<Action> Scheduled = new List<Action>();

		public void InvokeOnGameThread(Action action, string invokerName = "Mod", int StartAt = 1, int RepeatTimes = 0)
		{
			Scheduled.Add(action);
		}

		public void ShowMessage(string sender, string messageText)
		{
			ShownMessages.Add(new ShownMessage { Sender = sender, Text = messageText });
		}

		public byte[] SerializeToBinary<T>(T obj) => StubSerializer.Serialize(obj);

		public T SerializeFromBinary<T>(byte[] data)
		{
			if (FailDeserializeFor != null && FailDeserializeFor(typeof(T)))
			{
				throw new InvalidOperationException($"Injected deserialization failure for {typeof(T).Name}");
			}

			return StubSerializer.Deserialize<T>(data);
		}

		/// <summary>Types a chat line as the local player. Returns sendToOthers.</summary>
		public bool SimulateChat(string messageText)
		{
			bool sendToOthers = true;
			MessageEntered?.Invoke(messageText, ref sendToOthers);
			return sendToOthers;
		}

		public int MessageEnteredSubscriberCount => MessageEntered?.GetInvocationList().Length ?? 0;
	}

	/// <summary>
	/// Reproduces the parts of MyMultiplayerBase's ModAPI implementation that a
	/// mod can observe, including the two rules that bite in production:
	///
	///   * an unreliable message longer than 1024 bytes is dropped and the call
	///     returns false (the engine does this before touching the network);
	///   * a message addressed to the local player is delivered straight back
	///     into the local handlers (HandleMessageClient: recipient == Sync.MyId).
	/// </summary>
	public sealed class FakeMultiplayer : IMyMultiplayer
	{
		/// <summary>The engine's cut-off for unreliable messages.</summary>
		public const int UnreliableSizeLimit = 1024;

		private const int MaxLoopbackDepth = 8;

		public bool IsServer { get; set; }

		/// <summary>Packets accepted by the transport, oldest first.</summary>
		public readonly List<SentPacket> Sent = new List<SentPacket>();

		/// <summary>Packets the transport refused (unreliable and over the size limit).</summary>
		public readonly List<SentPacket> Dropped = new List<SentPacket>();

		/// <summary>
		/// Steam id of the local player, used to decide whether a directed send
		/// loops straight back. 0 for a dedicated server, which is also the
		/// engine's server id.
		/// </summary>
		public ulong LocalSteamId;

		/// <summary>Set false to stop modelling the engine's self-delivery.</summary>
		public bool DeliverToSelf = true;

		private readonly Dictionary<ushort, List<Action<byte[]>>> _handlers = new Dictionary<ushort, List<Action<byte[]>>>();
		private int _loopbackDepth;

		/// <summary>
		/// When set, every accepted packet is also handed to this callback.
		/// </summary>
		public Action<SentPacket> OnPacketSent;

		public void RegisterMessageHandler(ushort id, Action<byte[]> messageHandler)
		{
			if (!_handlers.ContainsKey(id))
			{
				_handlers.Add(id, new List<Action<byte[]>>());
			}

			_handlers[id].Add(messageHandler);
		}

		public void UnregisterMessageHandler(ushort id, Action<byte[]> messageHandler)
		{
			if (_handlers.ContainsKey(id))
			{
				_handlers[id].Remove(messageHandler);
			}
		}

		// SENetworkAPI does not use the secure pair; they exist so the stub
		// mirrors the real interface.
		public void RegisterSecureMessageHandler(ushort id, Action<ushort, byte[], ulong, bool> messageHandler) { }
		public void UnregisterSecureMessageHandler(ushort id, Action<ushort, byte[], ulong, bool> messageHandler) { }

		public int HandlerCount(ushort id) => _handlers.ContainsKey(id) ? _handlers[id].Count : 0;

		/// <summary>Pushes a packet into the registered handlers, as the game does on receive.</summary>
		public void Deliver(ushort id, byte[] message)
		{
			if (!_handlers.ContainsKey(id))
			{
				return;
			}

			if (++_loopbackDepth > MaxLoopbackDepth)
			{
				_loopbackDepth = 0;
				throw new InvalidOperationException(
					$"Loopback depth exceeded {MaxLoopbackDepth}: the code under test keeps echoing packets to itself.");
			}

			try
			{
				foreach (Action<byte[]> handler in _handlers[id].ToArray())
				{
					handler(message);
				}
			}
			finally
			{
				_loopbackDepth--;
			}
		}

		public bool SendMessageToServer(ushort id, byte[] message, bool reliable = true)
		{
			// A listen server host is its own server, so this comes straight back.
			return Record(new SentPacket { ComId = id, Data = message, Target = PacketTarget.Server, Reliable = reliable }, IsServer);
		}

		public bool SendMessageToOthers(ushort id, byte[] message, bool reliable = true)
		{
			return Record(new SentPacket { ComId = id, Data = message, Target = PacketTarget.Others, Reliable = reliable }, false);
		}

		public bool SendMessageTo(ushort id, byte[] message, ulong recipient, bool reliable = true)
		{
			SentPacket packet = new SentPacket { ComId = id, Data = message, Target = PacketTarget.Direct, Recipient = recipient, Reliable = reliable };
			return Record(packet, recipient == LocalSteamId);
		}

		private bool Record(SentPacket packet, bool addressedToSelf)
		{
			if (!packet.Reliable && packet.Data.Length > UnreliableSizeLimit)
			{
				Dropped.Add(packet);
				return false;
			}

			Sent.Add(packet);
			OnPacketSent?.Invoke(packet);

			if (addressedToSelf && DeliverToSelf)
			{
				Deliver(packet.ComId, packet.Data);
			}

			return true;
		}
	}

	public sealed class FakePlayerCollection : IMyPlayerCollection
	{
		public readonly List<IMyPlayer> AllPlayers = new List<IMyPlayer>();

		public void GetPlayers(List<IMyPlayer> players, Func<IMyPlayer, bool> collect = null)
		{
			foreach (IMyPlayer player in AllPlayers)
			{
				if (collect == null || collect(player))
				{
					players.Add(player);
				}
			}
		}

		public FakePlayer Add(ulong steamId, Vector3D? position = null)
		{
			FakePlayer player = new FakePlayer { SteamUserId = steamId, Position = position ?? Vector3D.Zero, DisplayName = $"Player{steamId}" };
			AllPlayers.Add(player);
			return player;
		}
	}

	public sealed class FakeEntities : IMyEntities
	{
		public readonly Dictionary<long, IMyEntity> Registered = new Dictionary<long, IMyEntity>();

		public IMyEntity GetEntityById(long entityId)
			=> Registered.ContainsKey(entityId) ? Registered[entityId] : null;

		public T Add<T>(T entity) where T : MyEntity
		{
			Registered[entity.EntityId] = entity;
			return entity;
		}

		public void Remove(long entityId) => Registered.Remove(entityId);
	}

	/// <summary>
	/// Wires a complete fake game session into MyAPIGateway. Construct one per
	/// test; disposing tears the gateway back down.
	/// </summary>
	public sealed class FakeGame : IDisposable
	{
		public readonly FakeUtilities Utilities = new FakeUtilities();
		public readonly FakeMultiplayer Multiplayer = new FakeMultiplayer();
		public readonly FakeSession Session = new FakeSession();
		public readonly FakePlayerCollection Players = new FakePlayerCollection();
		public readonly FakeEntities Entities = new FakeEntities();

		public MyLog Log => MyLog.Default;

		/// <summary>Packets handed to the multiplayer layer, oldest first.</summary>
		public List<SentPacket> Sent => Multiplayer.Sent;

		/// <summary>Chat lines produced with ShowMessage.</summary>
		public List<ShownMessage> ShownMessages => Utilities.ShownMessages;

		private FakeGame()
		{
			MyAPIGateway.Utilities = Utilities;
			MyAPIGateway.Multiplayer = Multiplayer;
			MyAPIGateway.Session = Session;
			MyAPIGateway.Players = Players;
			MyAPIGateway.Entities = Entities;

			MyCompression.CompressCallCount = 0;
			MyCompression.DecompressCallCount = 0;
			MyLog.Default.Clear();
			MyEntity.ResetIdCounter();
		}

		/// <summary>A listen server: is the host and has a local player.</summary>
		public static FakeGame StartServer(ulong hostSteamId = 100)
		{
			FakeGame game = new FakeGame();
			game.Multiplayer.IsServer = true;
			game.Utilities.IsDedicated = false;
			FakePlayer host = game.Players.Add(hostSteamId);
			game.Session.Player = host;
			game.Session.LocalHumanPlayer = host;
			game.Multiplayer.LocalSteamId = hostSteamId;
			return game;
		}

		/// <summary>A dedicated server: is the host, has no local player.</summary>
		public static FakeGame StartDedicatedServer()
		{
			FakeGame game = new FakeGame();
			game.Multiplayer.IsServer = true;
			game.Utilities.IsDedicated = true;
			game.Session.Player = null;
			game.Session.LocalHumanPlayer = null;
			return game;
		}

		/// <summary>A remote client connected to someone else's server.</summary>
		public static FakeGame StartClient(ulong steamId = 200)
		{
			FakeGame game = new FakeGame();
			game.Multiplayer.IsServer = false;
			game.Utilities.IsDedicated = false;
			FakePlayer me = game.Players.Add(steamId);
			game.Session.Player = me;
			game.Session.LocalHumanPlayer = me;
			game.Multiplayer.LocalSteamId = steamId;
			return game;
		}

		/// <summary>Flips the instance between server and client roles mid-test.</summary>
		public void BecomeServer(bool isServer) => Multiplayer.IsServer = isServer;

		/// <summary>Simulates the session being torn down (MyAPIGateway.Session goes null).</summary>
		public void DestroySession() => MyAPIGateway.Session = null;

		public MyEntity CreateEntity(Vector3D? position = null, string subtypeId = null)
		{
			MyEntity entity = new MyEntity();
			entity.PositionComp.SetPosition(position ?? Vector3D.Zero);
			if (subtypeId != null)
			{
				entity.DefinitionId = new MyDefinitionId("MyObjectBuilder_UpgradeModule", subtypeId);
			}

			return Entities.Add(entity);
		}

		/// <summary>
		/// Advances the frame counter and runs anything scheduled with
		/// InvokeOnGameThread, as the game's update loop would.
		/// </summary>
		public void NextFrame()
		{
			Session.GameplayFrameCounter++;

			if (Utilities.Scheduled.Count == 0)
			{
				return;
			}

			Action[] due = Utilities.Scheduled.ToArray();
			Utilities.Scheduled.Clear();

			foreach (Action action in due)
			{
				action();
			}
		}

		public void ClearTraffic()
		{
			Multiplayer.Sent.Clear();
			Multiplayer.Dropped.Clear();
			Utilities.ShownMessages.Clear();
			MyLog.Default.Clear();
		}

		/// <summary>Packets sent to the given steam id.</summary>
		public IEnumerable<SentPacket> SentTo(ulong steamId)
			=> Multiplayer.Sent.Where(p => p.Target == PacketTarget.Direct && p.Recipient == steamId);

		public void Dispose()
		{
			MyAPIGateway.Utilities = null;
			MyAPIGateway.Multiplayer = null;
			MyAPIGateway.Session = null;
			MyAPIGateway.Players = null;
			MyAPIGateway.Entities = null;
		}
	}
}
