using System;
using System.Collections.Generic;
using System.Diagnostics;
using SEStubs;
using SENetworkAPI;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;

namespace SENetworkAPI.Benchmarks
{
	/// <summary>
	/// Measures the hot paths: bytes allocated per operation and time per
	/// operation. Run before and after a change and compare.
	/// </summary>
	internal static class Program
	{
		private const int Warmup = 2000;
		private const int Iterations = 200000;
		private const ushort ComId = 1234;
		private const ulong HostId = 100;
		private const ulong ClientId = 200;

		private class Session : MySessionComponentBase { }

		private static void Main()
		{
			Console.WriteLine($"SENetworkAPI hot path benchmark   ({(IsDebug() ? "DEBUG - rebuild with -c Release" : "release")})");
			Console.WriteLine(new string('-', 78));
			Console.WriteLine($"{"scenario",-46}{"bytes/op",12}{"ns/op",12}");
			Console.WriteLine(new string('-', 78));

			Measure("harness floor (one fake send, no API)", HarnessFloor);
			Measure("session property assign (client)", SessionPropertyAssign);
			Measure("entity property assign, 8 players in range", EntityPropertyAssignInRange);
			Measure("entity property assign, 64 players, 8 in range", EntityPropertyAssignManyPlayers);
			Measure("entity property assign, nobody in range", EntityPropertyAssignNobodyInRange);
			Measure("8 properties on a block, same frame, 64 players", BlockOfPropertiesPerFrame);
			Measure("  ... the same, coalesced", BlockOfPropertiesCoalesced);
			Measure("property fetch (client -> server)", PropertyFetch);
			Measure("server broadcast command, 32 byte payload", ServerBroadcast);
			Measure("server receives + relays a property update", ServerReceiveAndRelay);
			Measure("receive command packet, callback registered", ReceiveCommandPacket);
			Measure("chat line that is not ours", ChatMiss);
			Measure("chat line that is ours", ChatHit);

			Console.WriteLine(new string('-', 78));
		}

		private static bool IsDebug()
		{
#if DEBUG
			return true;
#else
			return false;
#endif
		}

		// -------------------------------------------------------------------

		private static void Measure(string name, Func<Action> setup)
		{
			Reset();
			Action op = setup();

			for (int i = 0; i < Warmup; i++)
			{
				op();
			}

			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();

			long before = GC.GetAllocatedBytesForCurrentThread();
			Stopwatch watch = Stopwatch.StartNew();
			for (int i = 0; i < Iterations; i++)
			{
				op();
			}

			watch.Stop();
			long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

			double bytesPerOp = (double)bytes / Iterations;
			double nsPerOp = watch.Elapsed.TotalMilliseconds * 1000000.0 / Iterations;
			Console.WriteLine($"{name,-46}{bytesPerOp,12:F1}{nsPerOp,12:F1}");
		}

		private static FakeGame _game;

		private static void Reset()
		{
			_game?.Dispose();
			NetworkAPI.Instance = null;
			NetworkAPI.LogNetworkTraffic = false;
			NetSync.ClearRegistries();
			VRage.Utils.MyLog.Default.Clear();
		}

		private static FakeGame Server(int players = 0, double spread = 10)
		{
			_game = FakeGame.StartServer(HostId);
			((FakePlayer)_game.Session.Player).Position = new Vector3D(1e9, 0, 0);
			for (int i = 0; i < players; i++)
			{
				_game.Players.Add((ulong)(1000 + i), new Vector3D(i * spread, 0, 0));
			}

			NetworkAPI.Init(ComId, "Bench");
			return _game;
		}

		private static FakeGame Client(string keyword = "/bench")
		{
			_game = FakeGame.StartClient(ClientId);
			NetworkAPI.Init(ComId, "Bench", keyword);
			return _game;
		}

		/// <summary>Drops everything the fake recorded so the harness cost stays flat.</summary>
		private static void Drain()
		{
			_game.Multiplayer.Sent.Clear();
			_game.Multiplayer.Dropped.Clear();
			_game.Utilities.ShownMessages.Clear();
		}

		// -------------------------------------------------------------------
		//  Scenarios
		// -------------------------------------------------------------------

		private static Action HarnessFloor()
		{
			FakeGame game = Server();
			byte[] payload = new byte[64];
			return () =>
			{
				game.Multiplayer.SendMessageToOthers(ComId, payload);
				Drain();
			};
		}

		private static Action SessionPropertyAssign()
		{
			Client();
			NetSync<int> property = new NetSync<int>(new Session(), TransferType.Both, 0, syncOnLoad: false);
			int i = 0;
			return () =>
			{
				property.Value = i++;
				Drain();
			};
		}

		private static Action EntityPropertyAssignInRange()
		{
			FakeGame game = Server(players: 8, spread: 10);
			MyEntity entity = game.CreateEntity(Vector3D.Zero);
			NetSync<int> property = new NetSync<int>(entity, TransferType.Both, 0, syncOnLoad: false);
			int i = 0;
			return () =>
			{
				property.Value = i++;
				Drain();
			};
		}

		private static Action EntityPropertyAssignManyPlayers()
		{
			// 64 connected players, only the first 8 near the entity: the range
			// query walks everybody on every single property change.
			FakeGame game = Server(players: 64, spread: 1000);
			MyEntity entity = game.CreateEntity(Vector3D.Zero);
			NetSync<int> property = new NetSync<int>(entity, TransferType.Both, 0, syncOnLoad: false);
			int i = 0;
			return () =>
			{
				property.Value = i++;
				Drain();
			};
		}

		/// <summary>
		/// What a block with several synced properties actually does: they all
		/// change together in one frame, on a busy server.
		/// </summary>
		private static Action BlockOfPropertiesPerFrame()
		{
			FakeGame game = Server(players: 64, spread: 1000);
			MyEntity entity = game.CreateEntity(Vector3D.Zero);
			NetSync<int>[] properties = new NetSync<int>[8];

			for (int i = 0; i < properties.Length; i++)
			{
				properties[i] = new NetSync<int>(entity, TransferType.Both, 0, syncOnLoad: false);
			}

			int tick = 0;
			return () =>
			{
				tick++;
				game.NextFrame();

				for (int i = 0; i < properties.Length; i++)
				{
					properties[i].Value = tick + i;
				}

				Drain();
			};
		}

		private static Action BlockOfPropertiesCoalesced()
		{
			FakeGame game = Server(players: 64, spread: 1000);
			MyEntity entity = game.CreateEntity(Vector3D.Zero);
			NetSync<int>[] properties = new NetSync<int>[8];

			for (int i = 0; i < properties.Length; i++)
			{
				properties[i] = new NetSync<int>(entity, TransferType.Both, 0, syncOnLoad: false).Coalesce();
			}

			int tick = 0;
			return () =>
			{
				tick++;

				for (int i = 0; i < properties.Length; i++)
				{
					properties[i].Value = tick + i;
				}

				game.NextFrame();
				Drain();
			};
		}

		/// <summary>
		/// The common case on a large world: a block syncing a property with no
		/// player anywhere near it.
		/// </summary>
		private static Action EntityPropertyAssignNobodyInRange()
		{
			FakeGame game = Server(players: 16);
			MyEntity entity = game.CreateEntity(Vector3D.Zero);

			// Every player a long way from the block, so the range test rejects
			// all of them and nothing needs encoding.
			for (int p = 0; p < game.Players.AllPlayers.Count; p++)
			{
				((FakePlayer)game.Players.AllPlayers[p]).Position = new Vector3D((p + 1) * 100000, 0, 0);
			}

			NetSync<int> property = new NetSync<int>(entity, TransferType.Both, 0, syncOnLoad: false);
			int i = 0;
			return () =>
			{
				property.Value = i++;
				Drain();
			};
		}

		private static Action PropertyFetch()
		{
			Client();
			NetSync<Payload> property = new NetSync<Payload>(new Session(), TransferType.Both, Payload.Big(), syncOnLoad: false);
			return () =>
			{
				property.Fetch();
				Drain();
			};
		}

		private static Action ServerBroadcast()
		{
			NetworkAPI api = NetworkApiFor(Server());
			byte[] payload = new byte[32];
			return () =>
			{
				api.SendCommand("update", data: payload);
				Drain();
			};
		}

		private static Action ServerReceiveAndRelay()
		{
			FakeGame game = Server(players: 8, spread: 10);
			NetSync<int> property = new NetSync<int>(new Session(), TransferType.Both, 0, syncOnLoad: false);
			byte[] packet = PropertyPacket(property.Id, 0, SyncType.Post, 7, ClientId);
			return () =>
			{
				game.Multiplayer.Deliver(ComId, packet);
				Drain();
			};
		}

		private static Action ReceiveCommandPacket()
		{
			FakeGame game = Client();
			NetworkAPI api = NetworkAPI.Instance;
			api.RegisterNetworkCommand("update", (s, c, d, t) => { });
			byte[] packet = CommandPacket("update", new byte[32]);
			return () =>
			{
				game.Multiplayer.Deliver(ComId, packet);
				Drain();
			};
		}

		private static Action ChatMiss()
		{
			FakeGame game = Client();
			NetworkAPI.Instance.RegisterChatCommand("help", _ => { });
			return () => game.Utilities.SimulateChat("just talking to my friends about the new reactor");
		}

		private static Action ChatHit()
		{
			FakeGame game = Client();
			NetworkAPI.Instance.RegisterChatCommand("help", _ => { });
			return () =>
			{
				game.Utilities.SimulateChat("/bench help me out here");
				game.Utilities.ShownMessages.Clear();
			};
		}

		// -------------------------------------------------------------------

		private static NetworkAPI NetworkApiFor(FakeGame game) => NetworkAPI.Instance;

		private static byte[] CommandPacket(string command, byte[] data)
		{
			Command cmd = new Command { CommandString = command, Data = data, SteamId = ClientId, Timestamp = DateTime.UtcNow.Ticks };
			return StubSerializer.Serialize(cmd);
		}

		private static byte[] PropertyPacket(long id, long entityId, SyncType type, int value, ulong from)
		{
			SyncData sync = new SyncData { Id = id, EntityId = entityId, SyncType = type, Data = StubSerializer.Serialize(value) };
			Command cmd = new Command { IsProperty = true, Data = StubSerializer.Serialize(sync), SteamId = from, Timestamp = DateTime.UtcNow.Ticks };
			return StubSerializer.Serialize(cmd);
		}

		[ProtoBuf.ProtoContract]
		public class Payload
		{
			[ProtoBuf.ProtoMember(1)] public string Name { get; set; }
			[ProtoBuf.ProtoMember(2)] public List<int> Values { get; set; }

			public static Payload Big()
			{
				List<int> values = new List<int>();
				for (int i = 0; i < 200; i++)
				{
					values.Add(i);
				}

				return new Payload { Name = "a configuration blob of the sort mods actually sync", Values = values };
			}
		}
	}
}
