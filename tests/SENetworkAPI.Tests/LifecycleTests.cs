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
