using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SENetworkAPI
{
	/// <summary>
	/// Server side of the API. Adds sends addressed at one client, a list of
	/// clients, or every client near a point in the world.
	/// </summary>
	public class Server : NetworkAPI
	{
		private Vector3D[] m_snapshotPositions = new Vector3D[16];
		private ulong[] m_snapshotIds = new ulong[16];
		private int m_snapshotCount;
		private readonly List<IMyPlayer> m_snapshotSource = new List<IMyPlayer>();
		private int m_snapshotFrame = int.MinValue;

		private readonly Func<IMyPlayer, bool> m_singleRecipientFilter;
		private ulong m_filterWanted;

		/// <summary>Use <see cref="NetworkAPI.Init"/> instead of constructing this directly.</summary>
		/// <param name="comId">The communication channel this mod sends and listens on</param>
		/// <param name="modName">Sender name used for chat messages the API prints</param>
		/// <param name="keyword">Chat command prefix, or null to disable chat commands</param>
		public Server(ushort comId, string modName, string keyword = null) : base(comId, modName, keyword)
		{
			m_singleRecipientFilter = IsWantedRecipient;
		}

		private bool IsWantedRecipient(IMyPlayer player)
		{
			return player.SteamUserId == m_filterWanted;
		}

		private void RefreshSnapshot()
		{
			IMySession session = MyAPIGateway.Session;
			int frame = (session != null) ? session.GameplayFrameCounter : int.MinValue;

			if (frame == m_snapshotFrame && frame != int.MinValue)
			{
				return;
			}

			m_snapshotFrame = frame;
			m_snapshotSource.Clear();

			MyAPIGateway.Players.GetPlayers(m_snapshotSource);

			int count = m_snapshotSource.Count;

			if (count > m_snapshotPositions.Length)
			{
				m_snapshotPositions = new Vector3D[count];
				m_snapshotIds = new ulong[count];
			}

			for (int i = 0; i < count; i++)
			{
				IMyPlayer player = m_snapshotSource[i];
				m_snapshotPositions[i] = player.GetPosition();
				m_snapshotIds[i] = player.SteamUserId;
			}

			m_snapshotCount = count;

			m_snapshotSource.Clear();
		}

		/// <summary>Sends a command to one client, or to all of them.</summary>
		/// <param name="commandString">Command name, plus any arguments delimited with spaces</param>
		/// <param name="message">Text to display in chat on arrival, and on the host</param>
		/// <param name="data">Serialized payload</param>
		/// <param name="sent">Send timestamp. Defaults to now</param>
		/// <param name="steamId">Recipient, or 0 for all clients</param>
		/// <param name="isReliable">False permits the unreliable channel for small packets</param>
		public override void SendCommand(string commandString, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			SendCommand(new Command() { SteamId = steamId, CommandString = commandString, Message = message, Data = data, Timestamp = (sent == null) ? DateTime.UtcNow.Ticks : sent.Value.Ticks }, steamId, isReliable);
		}

		/// <summary>
		/// Sends a command to clients within a radius of a point. The player
		/// identified by the packet's steam id is excluded.
		/// </summary>
		/// <param name="commandString">Command name, plus any arguments delimited with spaces</param>
		/// <param name="point">Center of the send sphere, in world space</param>
		/// <param name="radius">Radius of the send sphere. 0 uses the world's sync distance</param>
		/// <param name="message">Text to display in chat on arrival, and on the host</param>
		/// <param name="data">Serialized payload</param>
		/// <param name="sent">Send timestamp. Defaults to now</param>
		/// <param name="steamId">Recipient, ignoring the radius, or 0 for everyone in range</param>
		/// <param name="isReliable">False permits the unreliable channel for small packets</param>
		public override void SendCommand(string commandString, Vector3D point, double radius = 0, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			SendCommand(new Command() { SteamId = steamId, CommandString = commandString, Message = message, Data = data, Timestamp = (sent == null) ? DateTime.UtcNow.Ticks : sent.Value.Ticks }, point, radius, steamId, isReliable);
		}

		/// <summary>Sends one command to each of several clients.</summary>
		/// <param name="steamIds">The recipients</param>
		/// <param name="commandString">Command name, plus any arguments delimited with spaces</param>
		/// <param name="message">Text to display in chat on arrival, and once on the host</param>
		/// <param name="data">Serialized payload</param>
		/// <param name="sent">Send timestamp. Defaults to now</param>
		/// <param name="isReliable">False permits the unreliable channel for small packets</param>
		public void SendCommandTo(ulong[] steamIds, string commandString, string message = null, byte[] data = null, DateTime? sent = null, bool isReliable = true)
		{
			if (steamIds == null || steamIds.Length == 0)
			{
				return;
			}

			Command cmd = new Command() { CommandString = commandString, Message = message, Data = data, Timestamp = (sent == null) ? DateTime.UtcNow.Ticks : sent.Value.Ticks };
			Compress(cmd);

			ShowLocally(cmd.Message);

			for (int i = 0; i < steamIds.Length; i++)
			{
				cmd.SteamId = steamIds[i];
				byte[] packet = Encode(cmd);

				if (LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] TRANSMITTING Bytes: {packet.Length}  Command: {cmd.CommandString}  User: {steamIds[i]}");
				}

				Send(packet, steamIds[i], isReliable);
			}
		}

		internal override void SendCommand(Command cmd, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			Compress(cmd);
			ShowLocally(cmd.Message);

			byte[] packet = MyAPIGateway.Utilities.SerializeToBinary(cmd);

			isReliable = ResolveReliability(packet, isReliable);

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

		internal override void SendCommand(Command cmd, Vector3D point, double radius = 0, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			Compress(cmd);

			if (radius == 0)
			{
				IMySession session = MyAPIGateway.Session;
				radius = (session != null) ? session.SessionSettings.SyncDistance : 0;
			}

			ShowLocally(cmd.Message);

			if (steamId != ulong.MinValue)
			{
				List<IMyPlayer> recipients = new List<IMyPlayer>();
				m_filterWanted = steamId;
				MyAPIGateway.Players.GetPlayers(recipients, m_singleRecipientFilter);

				byte[] addressed = (recipients.Count > 0) ? Encode(cmd) : null;

				for (int i = 0; i < recipients.Count; i++)
				{
					Send(addressed, recipients[i].SteamUserId, isReliable);
				}

				if (LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] _TRANSMITTING_ Bytes: {addressed?.Length ?? 0}  Command: {cmd.CommandString}  To: {recipients.Count} Users");
				}

				return;
			}

			RefreshSnapshot();

			double radiusSquared = radius * radius;
			ulong sender = cmd.SteamId;
			double px = point.X, py = point.Y, pz = point.Z;
			Vector3D[] positions = m_snapshotPositions;
			ulong[] ids = m_snapshotIds;
			int count = m_snapshotCount;
			int sent = 0;

			byte[] packet = null;

			for (int i = 0; i < count; i++)
			{
				Vector3D position = positions[i];
				double dx = position.X - px;
				double dy = position.Y - py;
				double dz = position.Z - pz;

				if ((dx * dx) + (dy * dy) + (dz * dz) >= radiusSquared)
				{
					continue;
				}

				ulong recipient = ids[i];

				if (recipient == sender)
				{
					continue;
				}

				if (packet == null)
				{
					packet = Encode(cmd);
				}

				sent++;
				Send(packet, recipient, isReliable);
			}

			if (LogNetworkTraffic)
			{
				MyLog.Default.Info($"[NetworkAPI] _TRANSMITTING_ Bytes: {packet?.Length ?? 0}  Command: {cmd.CommandString}  To: {sent} Users within {radius}m");
			}
		}

		private void ShowLocally(string message)
		{
			if (!string.IsNullOrWhiteSpace(message) && MyAPIGateway.Multiplayer.IsServer && MyAPIGateway.Session != null)
			{
				MyAPIGateway.Utilities.ShowMessage(ModName, message);
			}
		}

		private static byte[] Encode(Command cmd)
		{
			if (cmd.Timestamp == 0)
			{
				cmd.Timestamp = DateTime.UtcNow.Ticks;
			}

			return MyAPIGateway.Utilities.SerializeToBinary(cmd);
		}

		private void Send(byte[] packet, ulong steamId, bool isReliable)
		{
			MyAPIGateway.Multiplayer.SendMessageTo(ComId, packet, steamId, ResolveReliability(packet, isReliable));
		}

		/// <summary>Posts a line of chat to every client, and shows it on the host.</summary>
		/// <param name="message">The text to post</param>
		public override void Say(string message)
		{
			SendCommand(null, message);
		}
	}
}
