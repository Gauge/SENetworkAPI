using System;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>Registering / unregistering network and chat command callbacks.</summary>
	public class CommandRegistrationTests : NetworkTestBase
	{
		[Fact]
		public void RegisterNetworkCommand_WithNull_Throws()
		{
			NetworkAPI api = GivenServer();

			Exception ex = Assert.Throws<Exception>(() => api.RegisterNetworkCommand(null, (a, b, c, d) => { }));

			Assert.Contains("null is reserved for chat messages", ex.Message);
		}

		[Fact]
		public void RegisterNetworkCommand_Duplicate_Throws()
		{
			NetworkAPI api = GivenServer();
			api.RegisterNetworkCommand("update", (a, b, c, d) => { });

			Exception ex = Assert.Throws<Exception>(() => api.RegisterNetworkCommand("update", (a, b, c, d) => { }));

			Assert.Contains("already added", ex.Message);
		}

		[Fact]
		public void RegisterNetworkCommand_DuplicateDetection_IsCaseInsensitive()
		{
			NetworkAPI api = GivenServer();
			api.RegisterNetworkCommand("Update", (a, b, c, d) => { });

			Assert.Throws<Exception>(() => api.RegisterNetworkCommand("UPDATE", (a, b, c, d) => { }));
		}

		[Fact]
		public void RegisterNetworkCommand_KeepsTheCallersSpellingAndMatchesAnyCasing()
		{
			NetworkAPI api = GivenServer();

			api.RegisterNetworkCommand("MixedCase", (a, b, c, d) => { });

			Assert.True(api.NetworkCommands.ContainsKey("MixedCase"));
			Assert.True(api.NetworkCommands.ContainsKey("mixedcase"));
			Assert.True(api.NetworkCommands.ContainsKey("MIXEDCASE"));
		}

		[Fact]
		public void UnregisterNetworkCommand_RemovesTheCallback()
		{
			NetworkAPI api = GivenServer();
			api.RegisterNetworkCommand("update", (a, b, c, d) => { });

			api.UnregisterNetworkCommand("update");

			Assert.False(api.NetworkCommands.ContainsKey("update"));
			// ... and the name is free again.
			api.RegisterNetworkCommand("update", (a, b, c, d) => { });
		}

		[Fact]
		public void UnregisterNetworkCommand_MatchesAnyCasing()
		{
			NetworkAPI api = GivenServer();
			api.RegisterNetworkCommand("Update", (a, b, c, d) => { });

			api.UnregisterNetworkCommand("UPDATE");

			Assert.Empty(api.NetworkCommands);
		}

		[Fact]
		public void UnregisterNetworkCommand_WithNull_IsSafe()
		{
			NetworkAPI api = GivenServer();
			api.RegisterNetworkCommand("update", (a, b, c, d) => { });

			Exception thrown = Record.Exception(() => api.UnregisterNetworkCommand(null));

			Assert.Null(thrown);
			Assert.Single(api.NetworkCommands);
		}

		[Fact]
		public void UnregisterChatCommand_WithNull_RemovesTheEmptyCommand()
		{
			NetworkAPI api = GivenServer("/test");
			api.RegisterChatCommand(null, _ => { });

			api.UnregisterChatCommand(null);

			Assert.Empty(api.ChatCommands);
		}

		[Fact]
		public void UnregisterNetworkCommand_ThatWasNeverRegistered_IsSafe()
		{
			NetworkAPI api = GivenServer();

			Exception thrown = Record.Exception(() => api.UnregisterNetworkCommand("ghost"));

			Assert.Null(thrown);
		}

		[Fact]
		public void RegisterChatCommand_WithNull_RegistersTheEmptyCommand()
		{
			NetworkAPI api = GivenServer("/test");

			api.RegisterChatCommand(null, _ => { });

			Assert.True(api.ChatCommands.ContainsKey(string.Empty));
		}

		[Fact]
		public void RegisterChatCommand_Duplicate_Throws()
		{
			NetworkAPI api = GivenServer("/test");
			api.RegisterChatCommand("help", _ => { });

			Assert.Throws<Exception>(() => api.RegisterChatCommand("HELP", _ => { }));
		}

		[Fact]
		public void RegisterChatCommand_KeepsTheCallersSpellingAndMatchesAnyCasing()
		{
			NetworkAPI api = GivenServer("/test");

			api.RegisterChatCommand("Help", _ => { });

			Assert.True(api.ChatCommands.ContainsKey("Help"));
			Assert.True(api.ChatCommands.ContainsKey("help"));
			Assert.True(api.ChatCommands.ContainsKey("HELP"));
		}

		[Fact]
		public void UnregisterChatCommand_ThatWasNeverRegistered_IsSafe()
		{
			NetworkAPI api = GivenServer("/test");

			Exception thrown = Record.Exception(() => api.UnregisterChatCommand("ghost"));

			Assert.Null(thrown);
		}

		[Fact]
		public void ChatCommands_CanBeRegisteredWithoutAKeyword_ButNeverFire()
		{
			// No keyword means MessageEntered is never hooked, so chat commands
			// registered on such an instance are unreachable.
			NetworkAPI api = GivenServer();
			bool invoked = false;
			api.RegisterChatCommand("help", _ => invoked = true);

			Game.Utilities.SimulateChat("/test help");

			Assert.False(invoked);
			Assert.False(api.UsingTextCommands);
		}
	}
}
