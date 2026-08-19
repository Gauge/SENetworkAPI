using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SENetworkAPI
{
	public class Server : NetworkAPI
	{
		// The positional send runs on every update of every property that limits
		// itself to sync distance. Asking the engine for the player list each
		// time meant walking its player dictionary and calling GetPosition on
		// every player, per property, per frame - fifty properties on a hundred
		// player server is five thousand position lookups a frame for an answer
		// that does not change within the frame. The list is snapshotted once
		// per frame instead, after which the range test is pure arithmetic over
		// a struct list: no interface calls, and nothing for the GC to scan.
		// Parallel arrays rather than a list of structs: the range test reads
		// only the position for the players it rejects, and never copies a
		// wider struct than it needs.
		private Vector3D[] m_snapshotPositions = new Vector3D[16];
		private ulong[] m_snapshotIds = new ulong[16];
		private int m_snapshotCount;
		private readonly List<IMyPlayer> m_snapshotSource = new List<IMyPlayer>();
		private int m_snapshotFrame = int.MinValue;

		// Still needed for a send addressed at one player, which does not use
		// the snapshot: a player who joined this frame must not be missed.
		private readonly Func<IMyPlayer, bool> m_singleRecipientFilter;
		private ulong m_filterWanted;


		/// <summary>
		/// Server class contains a few server only feature beond what is inharited from the NetworkAPI
		/// </summary>
		/// <param name="comId">Identifies the channel to pass information to and from this mod</param>
		/// <param name="keyword">identifies what chat entries should be captured and sent to the server</param>
		public Server(ushort comId, string modName, string keyword = null) : base(comId, modName, keyword)
		{
			m_singleRecipientFilter = IsWantedRecipient;
		}

		private bool IsWantedRecipient(IMyPlayer player)
		{
			return player.SteamUserId == m_filterWanted;
		}

		/// <summary>
		/// Rebuilds the player snapshot if it was taken on an earlier frame.
		/// Being a frame out of date only means a player who joined this frame
		/// waits one more before their first update.
		/// </summary>
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

			// Do not hold the players alive until the next frame.
			m_snapshotSource.Clear();
		}

		/// <summary>
		/// Sends a command packet to the client(s)
		/// </summary>
		/// <param name="commandString">The command to be executed</param>
		/// <param name="message">Text that will be displayed in client chat</param>
		/// <param name="data">A serialized object to be sent across the network</param>
		/// <param name="sent">The date timestamp this command was sent</param>
		/// <param name="steamId">The client reciving this packet (if 0 it sends to all clients)</param>
		/// <param name="isReliable">Ensure delivery of the packet</param>
		public override void SendCommand(string commandString, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			SendCommand(new Command() { SteamId = steamId, CommandString = commandString, Message = message, Data = data, Timestamp = (sent == null) ? DateTime.UtcNow.Ticks : sent.Value.Ticks }, steamId, isReliable);
		}

		/// <summary>
		/// Sends a command packet to every client within a radius of a point.
		/// </summary>
		/// <param name="commandString">The command to be executed</param>
		/// <param name="point">the center of the sync location</param>
		/// <param name="radius">the distance the message reaches (defaults to sync distance)</param>
		/// <param name="message">Text that will be displayed in client chat</param>
		/// <param name="data">A serialized object to be sent across the network</param>
		/// <param name="sent">The date timestamp this command was sent</param>
		/// <param name="steamId">The client reciving this packet (if 0 it sends to all clients)</param>
		/// <param name="isReliable">Ensure delivery of the packet</param>
		public override void SendCommand(string commandString, Vector3D point, double radius = 0, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true)
		{
			SendCommand(new Command() { SteamId = steamId, CommandString = commandString, Message = message, Data = data, Timestamp = (sent == null) ? DateTime.UtcNow.Ticks : sent.Value.Ticks }, point, radius, steamId, isReliable);
		}

		/// <summary>
		/// Sends a command packet to a list of clients
		/// </summary>
		/// <param name="steamIds">The players to send to; each gets their own copy</param>
		/// <param name="commandString">The command to be executed</param>
		/// <param name="message">Text that will be displayed in client chat</param>
		/// <param name="data">A serialized object to be sent across the network</param>
		/// <param name="sent">The date timestamp this command was sent</param>
		/// <param name="isReliable">Ensure delivery of the packet</param>
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

			// Echo once, here, rather than once per recipient: the per-packet
			// send path would otherwise print the same line for every player.
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

		/// <summary>
		/// Sends a command packet to the client(s)
		/// </summary>
		/// <param name="cmd">The object to be sent to the client</param>
		/// <param name="steamId">The players steam id</param>
		/// <param name="isReliable">Make sure the data arrives</param>
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
				// No session means no sync distance to fall back on; sending
				// nothing beats a null reference in the middle of a send.
				IMySession session = MyAPIGateway.Session;
				radius = (session != null) ? session.SessionSettings.SyncDistance : 0;
			}

			ShowLocally(cmd.Message);

			if (steamId != ulong.MinValue)
			{
				// Addressed at one player: ask the engine directly rather than
				// trusting a snapshot that may predate them joining.
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

			// Encoded on first use. A block that limits itself to sync distance
			// usually has nobody near it, and serializing a packet for an empty
			// recipient list is the most expensive thing this method can do.
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

		/// <summary>
		/// Prints a command's message in the host's own chat. The server has to
		/// do this itself: it is not a recipient of its own broadcast.
		/// </summary>
		private void ShowLocally(string message)
		{
			if (!string.IsNullOrWhiteSpace(message) && MyAPIGateway.Multiplayer.IsServer && MyAPIGateway.Session != null)
			{
				MyAPIGateway.Utilities.ShowMessage(ModName, message);
			}
		}

		/// <summary>Stamps the command if it needs it and encodes it.</summary>
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

		/// <summary>
		/// Broadcasts a line of chat to every client, and shows it on the host.
		/// </summary>
		public override void Say(string message)
		{
			SendCommand(null, message);
		}
	}
}
