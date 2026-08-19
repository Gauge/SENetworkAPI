using System;
using SEStubs;
using VRage.Game.Entity;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>Construction, Init(), Close()/Dispose() and handler registration.</summary>
	public class LifecycleTests : NetworkTestBase
	{
		private class UnloadTestComponent : VRage.Game.Components.MySessionComponentBase { }

		[Fact]
		public void TheVersionIsStampedIntoTheStartupLog()
		{
			// Mods embed these sources, so a bug report has no other way to say
			// which build it came from.
			GivenServer();

			Assert.False(string.IsNullOrWhiteSpace(NetworkAPI.Version));
			Assert.True(LoggedInfo($"Version: {NetworkAPI.Version}"));
		}

		[Fact]
		public void Init_OnServer_CreatesServerInstance()
		{
			NetworkAPI api = GivenServer();

			Assert.IsType<Server>(api);
			Assert.Same(api, NetworkAPI.Instance);
			Assert.True(NetworkAPI.IsInitialized);
		}

		[Fact]
		public void Init_OnClient_CreatesClientInstance()
		{
			NetworkAPI api = GivenClient();

			Assert.IsType<Client>(api);
			Assert.True(NetworkAPI.IsInitialized);
		}

		[Fact]
		public void Init_IsIdempotent_SecondCallIsIgnored()
		{
			NetworkAPI first = GivenServer();

			NetworkAPI.Init(9999, "OtherMod", "/other");

			Assert.Same(first, NetworkAPI.Instance);
			Assert.Equal(ComId, NetworkAPI.Instance.ComId);
			Assert.Equal(ModName, NetworkAPI.Instance.ModName);
		}

		[Fact]
		public void IsInitialized_IsFalseBeforeInit()
		{
			GivenUninitializedClient();

			Assert.False(NetworkAPI.IsInitialized);
			Assert.Null(NetworkAPI.Instance);
		}

		[Fact]
		public void Constructor_RegistersExactlyOneMessageHandler()
		{
			GivenServer();

			Assert.Equal(1, Game.Multiplayer.HandlerCount(ComId));
		}

		[Fact]
		public void Constructor_UnregistersBeforeRegistering_SoHandlersNeverStack()
		{
			GivenServer();

			// A second API on the same channel (e.g. a mod reloading) must not
			// leave two live handlers behind for its own delegate.
			new Server(ComId, "SecondMod");

			Assert.Equal(2, Game.Multiplayer.HandlerCount(ComId));
		}

		[Fact]
		public void Constructor_WithKeyword_SubscribesToChat()
		{
			GivenServer("/test");

			Assert.Equal(1, Game.Utilities.MessageEnteredSubscriberCount);
		}

		[Fact]
		public void Constructor_WithoutKeyword_DoesNotSubscribeToChat()
		{
			GivenServer();

			Assert.Equal(0, Game.Utilities.MessageEnteredSubscriberCount);
		}

		[Fact]
		public void Constructor_LowercasesKeyword()
		{
			NetworkAPI api = GivenServer("/TeSt");

			Assert.Equal("/test", api.Keyword);
		}

		[Fact]
		public void Constructor_NullModName_BecomesEmptyString()
		{
			Game = FakeGame.StartServer();
			Server api = new Server(ComId, null);

			Assert.Equal(string.Empty, api.ModName);
		}

		[Fact]
		public void Constructor_NullKeyword_StaysNull()
		{
			NetworkAPI api = GivenServer();

			Assert.Null(api.Keyword);
		}

		[Fact]
		public void NetworkType_ReportsClientServerOrDedicated()
		{
			Assert.Equal(NetworkTypes.Client, GivenClient().NetworkType);

			Restart();
			Assert.Equal(NetworkTypes.Server, GivenServer().NetworkType);

			Restart();
			Assert.Equal(NetworkTypes.Dedicated, GivenDedicatedServer().NetworkType);
		}

		[Fact]
		public void NetworkType_AgreesWithTheInstanceSoTheDocumentedCastIsSafe()
		{
			// The documented way to reach the server-only sends is:
			//     if (Network.NetworkType != NetworkTypes.Client)
			//         Server s = (Server)Network;
			// so anything but Client has to actually be a Server.
			foreach (Func<NetworkAPI> start in new Func<NetworkAPI>[] {
				() => GivenClient(), () => GivenServer(), () => GivenDedicatedServer() })
			{
				Restart();
				NetworkAPI api = start();

				if (api.NetworkType != NetworkTypes.Client)
				{
					Assert.IsType<Server>(api);
				}
				else
				{
					Assert.IsType<Client>(api);
				}
			}
		}

		[Fact]
		public void NetworkType_FollowsTheInstanceNotTheLiveSessionFlag()
		{
			// A client whose session later claims to be the server is still a
			// Client object, and every send it makes goes to the server. If
			// NetworkType read the live flag instead, a mod following the
			// documented pattern would cast a Client to Server and throw.
			NetworkAPI api = GivenClient();
			Game.Session.IsServer = true;
			Game.Multiplayer.IsServer = true;

			Assert.Equal(NetworkTypes.Client, api.NetworkType);
			Assert.IsType<Client>(NetworkAPI.Instance);
		}

		[Fact]
		public void NetworkType_OnAServerWithNoSessionStillReportsAServer()
		{
			NetworkAPI api = GivenServer();
			Game.DestroySession();

			Assert.NotEqual(NetworkTypes.Client, api.NetworkType);
		}

		[Fact]
		public void Close_UnregistersMessageAndChatHandlers()
		{
			NetworkAPI api = GivenServer("/test");

			api.Close();

			Assert.Equal(0, Game.Multiplayer.HandlerCount(ComId));
			Assert.Equal(0, Game.Utilities.MessageEnteredSubscriberCount);
		}

		[Fact]
		public void Dispose_ClosesAndClearsTheInstance()
		{
			GivenServer("/test");

			NetworkAPI.Dispose();

			Assert.Null(NetworkAPI.Instance);
			Assert.False(NetworkAPI.IsInitialized);
			Assert.Equal(0, Game.Multiplayer.HandlerCount(ComId));
		}

		[Fact]
		public void Dispose_WithoutInit_IsSafe()
		{
			GivenUninitializedClient();

			NetworkAPI.Dispose();

			Assert.Null(NetworkAPI.Instance);
		}

		[Fact]
		public void Dispose_ClearsThePropertyRegistries()
		{
			// These are static and survive a world unload, so without an explicit
			// clear the next session inherits the previous one's properties.
			GivenServer();
			MyEntity entity = Game.CreateEntity();
			new NetSync<int>(entity, TransferType.Both, syncOnLoad: false);
			new NetSync<int>(new UnloadTestComponent(), TransferType.Both, syncOnLoad: false);

			NetworkAPI.Dispose();

			Assert.Empty(NetSync.PropertiesByEntity);
			Assert.Empty(NetSync.PropertyById);
		}

		[Fact]
		public void Dispose_ResetsPropertyIdsSoTheNextSessionStartsFromOne()
		{
			GivenServer();
			new NetSync<int>(new UnloadTestComponent(), TransferType.Both, syncOnLoad: false);
			new NetSync<int>(new UnloadTestComponent(), TransferType.Both, syncOnLoad: false);

			NetworkAPI.Dispose();
			GivenServer();
			NetSync<int> first = new NetSync<int>(new UnloadTestComponent(), TransferType.Both, syncOnLoad: false);

			Assert.Equal(1, first.Id);
		}

		[Fact]
		public void SessionTools_UnloadData_DisposesTheApi()
		{
			GivenServer();

			new SessionTools().SimulateUnload();

			Assert.Null(NetworkAPI.Instance);
		}
	}
}
