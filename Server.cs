using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SENetworkAPI
{
	public class Server : NetworkAPI
	{
		// The positional send runs on every update of every property that limits
		// itself to sync distance, so its range filters are cached delegates
		// reading their parameters from fields rather than lambdas that capture
		// them - a closure allocation per send otherwise. The fields are only
		// read while GetPlayers is running, which cannot call back into a mod.
		private readonly Func<IMyPlayer, bool> m_inRangeFilter;
		private readonly Func<IMyPlayer, bool> m_singleRecipientFilter;
		private Vector3D m_filterPoint;
		private double m_filterRadiusSquared;
		private ulong m_filterExcluded;
		private ulong m_filterWanted;


		/// <summary>
		/// Server class contains a few server only feature beond what is inharited from the NetworkAPI
		/// </summary>
		/// <param name="comId">Identifies the channel to pass information to and from this mod</param>
		/// <param name="keyword">identifies what chat entries should be captured and sent to the server</param>
		public Server(ushort comId, string modName, string keyword = null) : base(comId, modName, keyword)
		{
			m_inRangeFilter = IsInRange;
			m_singleRecipientFilter = IsWantedRecipient;
		}

		private bool IsInRange(IMyPlayer player)
		{
			// Distance first: it rejects most players with a single interface
			// call, where testing the steam id first costs an extra call for
			// every player that is out of range anyway.
			return (player.GetPosition() - m_filterPoint).LengthSquared() < m_filterRadiusSquared && player.SteamUserId != m_filterExcluded;
		}

		private bool IsWantedRecipient(IMyPlayer player)
		{
			return player.SteamUserId == m_filterWanted;
		}

		/// <summary>
		/// Sends a command packet to the client(s)
		/// </summary>
		/// <param name="commandString">The command to be executed</param>
		/// <param name="message">Text that will be displayed in client chat</param>
		/// <param name="data">A serialized object to be sent across the network</param>
		/// <param name="sent">The date timestamp this command was sent</param>
		/// <param name="steamId">The client reciving this packet (if 0 it sends to all clients)</param>
		/// <param name="isReliable">Enture delivery of the packet</param>
		public override void SendCommand(string commandString, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			SendCommand(new Command() { SteamId = steamId, CommandString = commandString, Message = message, Data = data, Timestamp = (sent == null) ? DateTime.UtcNow.Ticks : sent.Value.Ticks }, steamId, isReliable);
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="commandString">Sends a command packet to the client(s)</param>
		/// <param name="point">the center of the sync location</param>
		/// <param name="radius">the distance the message reaches (defaults to sync distance)</param>
		/// <param name="message">Text that will be displayed in client chat</param>
		/// <param name="data">A serialized object to be sent across the network</param>
		/// <param name="sent">The date timestamp this command was sent</param>
		/// <param name="steamId">The client reciving this packet (if 0 it sends to all clients)</param>
		/// <param name="isReliable">Enture delivery of the packet</param>
		public override void SendCommand(string commandString, Vector3D point, double radius = 0, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			SendCommand(new Command() { SteamId = steamId, CommandString = commandString, Message = message, Data = data, Timestamp = (sent == null) ? DateTime.UtcNow.Ticks : sent.Value.Ticks }, point, radius, steamId, isReliable);
		}

		/// <summary>
		/// Sends a command packet to a list of clients
		/// </summary>
		/// <param name="steamIds"></param>
		/// <param name="commandString">The command to be executed</param>
		/// <param name="message">Text that will be displayed in client chat</param>
		/// <param name="data">A serialized object to be sent across the network</param>
		/// <param name="sent">The date timestamp this command was sent</param>
		/// <param name="isReliable">Enture delivery of the packet</param>
		public void SendCommandTo(ulong[] steamIds, string commandString, string message = null, byte[] data = null, DateTime? sent = null, bool isReliable = true)
		{
			if (steamIds == null || steamIds.Length == 0)
			{
				return;
			}

			// One command object, compressed once, re-addressed per recipient.
			// Building it inside the loop used to re-compress a large payload
			// for every player it was sent to.
			Command cmd = new Command() { CommandString = commandString, Message = message, Data = data, Timestamp = (sent == null) ? DateTime.UtcNow.Ticks : sent.Value.Ticks };
			Compress(cmd);

			for (int i = 0; i < steamIds.Length; i++)
			{
				cmd.SteamId = steamIds[i];
				SendCommand(cmd, steamIds[i], isReliable);
			}
		}

		/// <summary>
		/// Sends a command packet to the client(s)
		/// </summary>
		/// <param name="cmd">The object to be sent to the client</param>
		/// <param name="steamId">The players steam id</param>
		/// <param name="isReliable">Make sure the data arrives</param>
		internal override void SendCommand(Command cmd, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			Compress(cmd);

			if (!string.IsNullOrWhiteSpace(cmd.Message) && MyAPIGateway.Multiplayer.IsServer && MyAPIGateway.Session != null)
			{
				MyAPIGateway.Utilities.ShowMessage(ModName, cmd.Message);
			}

			byte[] packet = MyAPIGateway.Utilities.SerializeToBinary(cmd);

			// The engine silently drops unreliable messages over its size limit
			// and reports the failure through a return value nobody reads. Send
			// those reliably instead of losing them.
			if (!isReliable && packet.Length > UnreliableMessageLimit)
			{
				isReliable = true;
			}

			if (LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] TRANSMITTING Bytes: {packet.Length}  Command: {cmd.CommandString}  User: {steamId}");
			}

			if (steamId == ulong.MinValue)
			{
				MyAPIGateway.Multiplayer.SendMessageToOthers(ComId, packet, isReliable);
			}
			else
			{
				MyAPIGateway.Multiplayer.SendMessageTo(ComId, packet, steamId, isReliable);
			}
		}

		/// <summary>
		/// Sends a command packet to the client(s)
		/// </summary>
		/// <param name="cmd">The object to be sent to the client</param>
		/// <param name="point">the center of the sync location</param>
		/// <param name="radius">the distance the message reaches (defaults to sync distance)</param>
		/// <param name="steamId">The players steam id</param>
		/// <param name="isReliable">Make sure the data arrives</param>
		internal override void SendCommand(Command cmd, Vector3D point, double radius = 0, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			Compress(cmd);

			if (radius == 0)
			{
				radius = MyAPIGateway.Session.SessionSettings.SyncDistance;
			}

			List<IMyPlayer> players = new List<IMyPlayer>();

			if (steamId == ulong.MinValue)
			{
				m_filterPoint = point;
				m_filterRadiusSquared = radius * radius;
				m_filterExcluded = cmd.SteamId;
				MyAPIGateway.Players.GetPlayers(players, m_inRangeFilter);
			}
			else
			{
				m_filterWanted = steamId;
				MyAPIGateway.Players.GetPlayers(players, m_singleRecipientFilter);
			}

			Transmit(cmd, players, radius, isReliable);
		}

		/// <summary>
		/// Serializes the command once and hands a copy to each recipient.
		/// </summary>
		private void Transmit(Command cmd, List<IMyPlayer> players, double radius, bool isReliable)
		{
			if (!string.IsNullOrWhiteSpace(cmd.Message) && MyAPIGateway.Multiplayer.IsServer && MyAPIGateway.Session != null)
			{
				MyAPIGateway.Utilities.ShowMessage(ModName, cmd.Message);
			}

			// Only stamp commands that do not carry one already; see Client.cs.
			if (cmd.Timestamp == 0)
			{
				cmd.Timestamp = DateTime.UtcNow.Ticks;
			}

			byte[] packet = MyAPIGateway.Utilities.SerializeToBinary(cmd);

			// The engine silently drops unreliable messages over its size limit
			// and reports the failure through a return value nobody reads. Send
			// those reliably instead of losing them.
			if (!isReliable && packet.Length > UnreliableMessageLimit)
			{
				isReliable = true;
			}

			if (LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] _TRANSMITTING_ Bytes: {packet.Length}  Command: {cmd.CommandString}  To: {players.Count} Users within {radius}m");
			}

			for (int i = 0; i < players.Count; i++)
			{
				MyAPIGateway.Multiplayer.SendMessageTo(ComId, packet, players[i].SteamUserId, isReliable);
			}
		}

		public override void Say(string message)
		{
			SendCommand(null, message);
		}
	}
}
