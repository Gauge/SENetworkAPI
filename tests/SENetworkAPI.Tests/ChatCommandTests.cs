using System;
using System.Collections.Generic;
using Xunit;

namespace SENetworkAPI.Tests
{
	/// <summary>
	/// Chat command parsing: "&lt;keyword&gt; &lt;command&gt; &lt;arguments...&gt;".
	/// </summary>
	public class ChatCommandTests : NetworkTestBase
	{
		private const string Keyword = "/test";

		[Fact]
		public void NonKeywordMessage_IsIgnoredAndStillSentToChat()
		{
			NetworkAPI api = GivenServer(Keyword);
			bool invoked = false;
			api.RegisterChatCommand("help", _ => invoked = true);

			bool sendToOthers = Game.Utilities.SimulateChat("hello everyone");

			Assert.True(sendToOthers);
			Assert.False(invoked);
			Assert.Empty(Game.ShownMessages);
		}

		[Fact]
		public void KeywordMessage_IsSwallowedFromGlobalChat()
		{
			NetworkAPI api = GivenServer(Keyword);
			api.RegisterChatCommand("help", _ => { });

			bool sendToOthers = Game.Utilities.SimulateChat("/test help");

			Assert.False(sendToOthers);
		}

		[Fact]
		public void BareKeyword_InvokesTheEmptyStringCommand()
		{
			NetworkAPI api = GivenServer(Keyword);
			string received = null;
			bool called = false;
			api.RegisterChatCommand(string.Empty, args => { called = true; received = args; });

			Game.Utilities.SimulateChat("/test");

			Assert.True(called);
			Assert.Equal(string.Empty, received);
		}

		[Fact]
		public void BareKeywordWithTrailingSpace_StillReachesTheEmptyStringCommand()
		{
			NetworkAPI api = GivenServer(Keyword);
			string received = "not called";
			api.RegisterChatCommand(string.Empty, args => received = args);

			Game.Utilities.SimulateChat("/test ");

			Assert.Equal(string.Empty, received);
		}

		[Fact]
		public void NamedCommand_ReceivesTheRemainingArguments()
		{
			NetworkAPI api = GivenServer(Keyword);
			string received = null;
			api.RegisterChatCommand("give", args => received = args);

			Game.Utilities.SimulateChat("/test give steel 100");

			Assert.Equal("steel 100", received);
		}

		[Fact]
		public void NamedCommand_WithNoArguments_ReceivesEmptyString()
		{
			NetworkAPI api = GivenServer(Keyword);
			string received = null;
			api.RegisterChatCommand("help", args => received = args);

			Game.Utilities.SimulateChat("/test help");

			Assert.Equal(string.Empty, received);
		}

		[Fact]
		public void CommandMatching_IsCaseInsensitive_ButArgumentsKeepTheirCase()
		{
			NetworkAPI api = GivenServer(Keyword);
			string received = null;
			api.RegisterChatCommand("say", args => received = args);

			Game.Utilities.SimulateChat("/TEST SAY Hello World");

			Assert.Equal("Hello World", received);
		}

		[Fact]
		public void ExtraWhitespaceBetweenTokens_IsPreservedInsideArguments()
		{
			NetworkAPI api = GivenServer(Keyword);
			string received = null;
			api.RegisterChatCommand("say", args => received = args);

			Game.Utilities.SimulateChat("/test say  double  spaced ");

			// Only the outer padding is trimmed; inner spacing is left intact.
			Assert.Equal("double  spaced", received);
		}

		[Fact]
		public void UnknownCommand_TellsThePlayerOnAClient()
		{
			GivenServer(Keyword);

			Game.Utilities.SimulateChat("/test nonsense");

			Assert.Single(Game.ShownMessages);
			Assert.Equal(ModName, Game.ShownMessages[0].Sender);
			Assert.Equal("Command not recognized.", Game.ShownMessages[0].Text);
		}

		[Fact]
		public void UnknownCommand_StaysSilentOnADedicatedServer()
		{
			GivenDedicatedServer(Keyword);

			Game.Utilities.SimulateChat("/test nonsense");

			Assert.Empty(Game.ShownMessages);
		}

		[Fact]
		public void BareKeyword_WithNoEmptyCommandRegistered_ReportsUnknownCommand()
		{
			NetworkAPI api = GivenServer(Keyword);
			api.RegisterChatCommand("help", _ => { });

			Game.Utilities.SimulateChat("/test");

			Assert.Single(Game.ShownMessages);
			Assert.Equal("Command not recognized.", Game.ShownMessages[0].Text);
		}

		[Fact]
		public void NullCallback_IsToleratedAtDispatchTime()
		{
			NetworkAPI api = GivenServer(Keyword);
			api.RegisterChatCommand("crash", null);

			Exception thrown = Record.Exception(() => Game.Utilities.SimulateChat("/test crash"));

			Assert.Null(thrown);
			// A registered-but-null command is still "known", so no error message.
			Assert.Empty(Game.ShownMessages);
		}

		[Fact]
		public void KeywordPrefixOfAnotherWord_DoesNotTrigger()
		{
			NetworkAPI api = GivenServer(Keyword);
			bool invoked = false;
			api.RegisterChatCommand(string.Empty, _ => invoked = true);

			bool sendToOthers = Game.Utilities.SimulateChat("/testing the waters");

			Assert.True(sendToOthers);
			Assert.False(invoked);
		}

		[Fact]
		public void MultipleCommands_DispatchIndependently()
		{
			NetworkAPI api = GivenServer(Keyword);
			List<string> calls = new List<string>();
			api.RegisterChatCommand("a", args => calls.Add($"a:{args}"));
			api.RegisterChatCommand("b", args => calls.Add($"b:{args}"));

			Game.Utilities.SimulateChat("/test a one");
			Game.Utilities.SimulateChat("/test b two");

			Assert.Equal(new[] { "a:one", "b:two" }, calls);
		}

		[Fact]
		public void KeywordMatchingIsOrdinalSoItDoesNotDependOnTheClientLocale()
		{
			// The old implementation lower-cased with the current culture, which
			// mangles "I" in Turkish locales. Ordinal comparison does not.
			NetworkAPI api = GivenServer("/HI");
			string received = null;
			api.RegisterChatCommand("there", args => received = args);

			Game.Utilities.SimulateChat("/HI there you");

			Assert.Equal("you", received);
		}

		[Fact]
		public void AThrowingChatCommand_DoesNotEscapeIntoTheGamesChatEvent()
		{
			// MessageEntered is a multicast delegate shared with every other mod.
			// An exception escaping here would stop the mods behind us in the
			// invocation list from seeing the message at all.
			NetworkAPI api = GivenServer(Keyword);
			api.RegisterChatCommand("boom", _ => { throw new InvalidOperationException("mod bug"); });

			Exception thrown = Record.Exception(() => Game.Utilities.SimulateChat("/test boom"));

			Assert.Null(thrown);
			Assert.True(LoggedError("threw"));
		}

		[Fact]
		public void UnregisterChatCommand_StopsDispatch()
		{
			NetworkAPI api = GivenServer(Keyword);
			bool invoked = false;
			api.RegisterChatCommand("help", _ => invoked = true);

			api.UnregisterChatCommand("help");
			Game.Utilities.SimulateChat("/test help");

			Assert.False(invoked);
			Assert.Single(Game.ShownMessages);
		}
	}
}
