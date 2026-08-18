// ---------------------------------------------------------------------------
//  Sandbox.ModAPI stand-ins -- the static gateway SENetworkAPI talks to.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using ProtoBuf;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace Sandbox.ModAPI
{
	public delegate void MessageEnteredDel(string messageText, ref bool sendToOthers);

	public interface IMyUtilities
	{
		bool IsDedicated { get; }
		event MessageEnteredDel MessageEntered;
		void ShowMessage(string sender, string messageText);
		byte[] SerializeToBinary<T>(T obj);
		T SerializeFromBinary<T>(byte[] data);
	}

	public interface IMyMultiplayer
	{
		bool IsServer { get; }
		void RegisterMessageHandler(ushort id, Action<byte[]> messageHandler);
		void UnregisterMessageHandler(ushort id, Action<byte[]> messageHandler);
		void SendMessageToServer(ushort id, byte[] message, bool reliable = true);
		void SendMessageToOthers(ushort id, byte[] message, bool reliable = true);
		void SendMessageTo(ushort id, byte[] message, ulong recipient, bool reliable = true);
	}

	public interface IMySessionSettings
	{
		int SyncDistance { get; }
	}

	public interface IMySession
	{
		IMyPlayer Player { get; }
		IMyPlayer LocalHumanPlayer { get; }
		IMySessionSettings SessionSettings { get; }
		MyOnlineModeEnum OnlineMode { get; }
	}

	public interface IMyPlayerCollection
	{
		void GetPlayers(List<IMyPlayer> players, Func<IMyPlayer, bool> collect = null);
	}

	public interface IMyEntities
	{
		IMyEntity GetEntityById(long entityId);
	}

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
