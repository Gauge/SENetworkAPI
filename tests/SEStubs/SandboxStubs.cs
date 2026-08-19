// ---------------------------------------------------------------------------
//  The ModAPI service interfaces and the static gateway that hands them out.
//
//  Namespaces match the shipped game exactly: only MyAPIGateway itself lives in
//  Sandbox.ModAPI (Sandbox.Common.dll); every interface it exposes is declared
//  in VRage.Game.dll under VRage.Game.ModAPI or VRage.ModAPI.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.ModAPI;

namespace VRage.Game.ModAPI
{
	public delegate void MessageEnteredDel(string messageText, ref bool sendToOthers);

	public interface IMyUtilities
	{
		bool IsDedicated { get; }
		event MessageEnteredDel MessageEntered;
		void ShowMessage(string sender, string messageText);
		void InvokeOnGameThread(Action action, string invokerName = "Mod", int StartAt = 1, int RepeatTimes = 0);
		byte[] SerializeToBinary<T>(T obj);
		T SerializeFromBinary<T>(byte[] data);
	}

	public interface IMyMultiplayer
	{
		bool IsServer { get; }

		// Note: the non-secure pair is [Obsolete] in the shipped game, which is
		// what SENetworkAPI still uses. See docs/known-issues.md.
		void RegisterMessageHandler(ushort id, Action<byte[]> messageHandler);
		void UnregisterMessageHandler(ushort id, Action<byte[]> messageHandler);

		// The secure pair, for reference: the engine supplies a verified sender
		// id and a "came from the server" flag that no packet can forge.
		void RegisterSecureMessageHandler(ushort id, Action<ushort, byte[], ulong, bool> messageHandler);
		void UnregisterSecureMessageHandler(ushort id, Action<ushort, byte[], ulong, bool> messageHandler);

		bool SendMessageToServer(ushort id, byte[] message, bool reliable = true);
		bool SendMessageToOthers(ushort id, byte[] message, bool reliable = true);
		bool SendMessageTo(ushort id, byte[] message, ulong recipient, bool reliable = true);
	}

	public interface IMySession
	{
		IMyPlayer Player { get; }
		IMyPlayer LocalHumanPlayer { get; }
		MyObjectBuilder_SessionSettings SessionSettings { get; }
		MyOnlineModeEnum OnlineMode { get; }
		int GameplayFrameCounter { get; }
	}

	public interface IMyPlayerCollection
	{
		void GetPlayers(List<IMyPlayer> players, Func<IMyPlayer, bool> collect = null);
	}
}

namespace VRage.ModAPI
{
	public interface IMyEntities
	{
		IMyEntity GetEntityById(long entityId);
	}
}

namespace Sandbox.ModAPI
{
	using VRage.Game.ModAPI;

	/// <summary>
	/// The game's static service locator. In the real game these are populated
	/// during session load; here the test fixture assigns fakes.
	/// </summary>
	public static class MyAPIGateway
	{
		public static IMyUtilities Utilities;
		public static IMyMultiplayer Multiplayer;
		public static IMySession Session;
		public static IMyPlayerCollection Players;
		public static IMyEntities Entities;
	}
}
