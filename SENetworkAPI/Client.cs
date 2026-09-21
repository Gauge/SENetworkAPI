using Sandbox.ModAPI;
using System;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SENetworkAPI
{
	/// <summary>Client side of the API. All traffic is addressed to the server.</summary>
	public class Client : NetworkAPI
	{
		/// <summary>Use <see cref="NetworkAPI.Init"/> instead of constructing this directly.</summary>
		/// <param name="comId">The communication channel this mod sends and listens on</param>
		/// <param name="modName">Sender name used for chat messages the API prints</param>
		/// <param name="keyword">Chat command prefix, or null to disable chat commands</param>
		public Client(ushort comId, string modName, string keyword = null) : base(comId, modName, keyword)
		{
		}

		/// <summary>
		/// Sends a command to the server, stamped with the local player's steam
		/// id. Does nothing when there is no session.
		/// </summary>
		/// <param name="commandString">Command name, plus any arguments delimited with spaces</param>
		/// <param name="message">Text to display in chat on arrival</param>
		/// <param name="data">Serialized payload</param>
		/// <param name="sent">Send timestamp. Defaults to now</param>
		/// <param name="steamId">Ignored: clients can only address the server</param>
		/// <param name="isReliable">False permits the unreliable channel for small packets</param>
		public override void SendCommand(string commandString, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			IMyPlayer player = MyAPIGateway.Session?.Player;

			if (player != null)
			{
				ulong steamUserId = player.SteamUserId;
				SendCommand(new Command() { CommandString = commandString, Message = message, Data = data, Timestamp = (sent == null) ? DateTime.UtcNow.Ticks : sent.Value.Ticks, SteamId = steamUserId }, steamUserId, isReliable);
			}
			else
			{
				MyLog.Default.Warning($"[NetworkAPI] ComID: {ComId} | Failed to send command. Session does not exist.");
			}
		}

		internal override void SendCommand(Command cmd, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			Compress(cmd);

			if (cmd.Timestamp == 0)
			{
				cmd.Timestamp = DateTime.UtcNow.Ticks;
			}

			byte[] packet = MyAPIGateway.Utilities.SerializeToBinary(cmd);

			isReliable = ResolveReliability(packet, isReliable);

			if (LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] TRANSMITTING Bytes: {packet.Length}  Command: {cmd.CommandString}  User: {steamId}");
			}

			MyAPIGateway.Multiplayer.SendMessageToServer(ComId, packet, isReliable);
		}

		/// <summary>Sends a command to the server. Position is ignored on a client.</summary>
		/// <param name="commandString">Command name, plus any arguments delimited with spaces</param>
		/// <param name="point">Ignored on a client</param>
		/// <param name="radius">Ignored on a client</param>
		/// <param name="message">Text to display in chat on arrival</param>
		/// <param name="data">Serialized payload</param>
		/// <param name="sent">Send timestamp. Defaults to now</param>
		/// <param name="steamId">Ignored: clients can only address the server</param>
		/// <param name="isReliable">False permits the unreliable channel for small packets</param>
		public override void SendCommand(string commandString, Vector3D point, double radius = 0, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = 0, bool isReliable = true)
		{
			SendCommand(commandString, message, data, sent, steamId, isReliable);
		}

		internal override void SendCommand(Command cmd, Vector3D point, double radius = 0, ulong steamId = 0, bool isReliable = true)
		{
			SendCommand(cmd, steamId, isReliable);
		}

		/// <summary>
		/// Sends a line of chat to the server, which relays it. Not shown locally
		/// until that relay arrives.
		/// </summary>
		/// <param name="message">The text to post</param>
		public override void Say(string message)
		{
			SendCommand(null, message);
		}
	}
}
