using Sandbox.ModAPI;
using System;
using VRage.Utils;

namespace SENetworkAPI
{
	public class Client : NetworkAPI
	{

		/// <summary>
		/// Handles communication with the server
		/// </summary>
		/// <param name="comId">Identifies the channel to pass information to and from this mod</param>
		/// <param name="keyword">identifies what chat entries should be captured and sent to the server</param>
		public Client(ushort comId, string modName, string keyword = null) : base(comId, modName, keyword)
		{
		}

		/// <summary>
		/// Sends a command packet to the server
		/// </summary>
		/// <param name="commandString">The command to be executed</param>
		/// <param name="message">Text that will be displayed in client chat</param>
		/// <param name="data">A serialized object to be sent across the network</param>
		/// <param name="sent">The date timestamp this command was sent</param>
		/// <param name="steamId">The client reciving this packet (if 0 it sends to all clients)</param>
		/// <param name="isReliable">Enture delivery of the packet</param>
		public override void SendCommand(string commandString, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			if (MyAPIGateway.Session?.Player != null)
			{
				byte[] packet = MyAPIGateway.Utilities.SerializeToBinary(new Command() { CommandString = commandString, Message = message, Data = data, Timestamp = (sent == null) ? DateTime.UtcNow.Ticks : sent.Value.Ticks, SteamId = MyAPIGateway.Session.Player.SteamUserId });

				if (LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] TRANSMITTING Bytes: {packet.Length}  Command: {commandString}  User: {steamId}");
				}

				MyAPIGateway.Multiplayer.SendMessageToServer(ComId, packet, isReliable);
			}
			else
			{
				MyLog.Default.Warning($"[NetworkAPI] ComID: {ComId} | Failed to send command. Session does not exist.");
			}
		}

		/// <summary>
		/// Sends a command packet to the server
		/// </summary>
		/// <param name="cmd">The object to be sent to the client</param>
		internal override void SendCommand(Command cmd, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			byte[] packet = MyAPIGateway.Utilities.SerializeToBinary(cmd);

			if (LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] TRANSMITTING Bytes: {packet.Length}  Command: {cmd.CommandString}  User: {steamId}");
			}

			MyAPIGateway.Multiplayer.SendMessageToServer(ComId, packet, isReliable);
		}
	}
}
