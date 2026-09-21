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

		// A sorted projection narrows sphere queries without allocating cells or
		// rebuilding a tree. Small populations and broad queries use the linear path.
		// Algorithm crossover heuristics, never limits on players or recipients.
		private const int SpatialIndexMinimumPlayers = 16;
		private const int SpatialIndexWarmupQueries = 16;
		private double[] m_sortedCoordinates = new double[16];
		private int[] m_sortedIndices = new int[16];
		private int[] m_matches = new int[16];
		private int m_queryCount;
		private int m_previousMatchCount;
		private int m_indexAxis;
		private bool m_indexBuilt;
		private bool m_indexValid;
		private bool m_boundsBuilt;
		private bool m_boundsValid;
		private Vector3D m_boundsMin;
		private Vector3D m_boundsMax;

		private readonly Stack<List<ulong>> m_recipientPool = new Stack<List<ulong>>();

		/// <summary>Use <see cref="NetworkAPI.Init"/> instead of constructing this directly.</summary>
		/// <param name="comId">The communication channel this mod sends and listens on</param>
		/// <param name="modName">Sender name used for chat messages the API prints</param>
		/// <param name="keyword">Chat command prefix, or null to disable chat commands</param>
		public Server(ushort comId, string modName, string keyword = null) : base(comId, modName, keyword)
		{
		}

		// Each caller owns its list until release, including during reentrant sends.
		internal List<ulong> CollectRecipients(Vector3D point, double radius, ulong sendTo, ulong excluded)
		{
			List<ulong> recipients = m_recipientPool.Count == 0 ? new List<ulong>() : m_recipientPool.Pop();
			try
			{
				// Directed sends must see players joining after this frame's snapshot.
				if (sendTo != 0)
				{
					m_snapshotSource.Clear();
					MyAPIGateway.Players.GetPlayers(m_snapshotSource);
					for (int i = 0; i < m_snapshotSource.Count; i++)
						if (m_snapshotSource[i].SteamUserId == sendTo) recipients.Add(sendTo);
					m_snapshotSource.Clear();
					return recipients;
				}
				RefreshSnapshot();
				if (radius == 0) radius = MyAPIGateway.Session?.SessionSettings.SyncDistance ?? 0;
				m_queryCount++;
				double squared = radius * radius;
				// Crowded grids often contain every player. Prove that using the
				// snapshot bounds, then copy IDs without repeating distance tests.
				if (m_snapshotCount >= SpatialIndexMinimumPlayers && m_queryCount >= SpatialIndexWarmupQueries &&
					m_previousMatchCount >= m_snapshotCount - 1 && AllPlayersInRange(point, squared))
				{
					for (int i = 0; i < m_snapshotCount; i++)
						if (m_snapshotIds[i] != excluded) recipients.Add(m_snapshotIds[i]);
					m_previousMatchCount = recipients.Count;
					return recipients;
				}

				// Amortize sorting only across repeated, selective queries. A scan
				// is cheaper for the common case of players clustered around a grid.
				bool selective = m_previousMatchCount < m_snapshotCount / 4;
				if (m_snapshotCount >= SpatialIndexMinimumPlayers && m_queryCount >= SpatialIndexWarmupQueries && selective && !m_indexBuilt)
					BuildSpatialIndex();

				double center = Coordinate(point, m_indexAxis);
				if (m_indexValid && selective && IsFinite(center) && IsFinite(radius))
				{
					double extent = Math.Abs(radius);
					int start = LowerBound(center - extent);
					int end = UpperBound(center + extent);
					// Use contiguous snapshot arrays when the projection cannot prune much.
					if (end - start < m_snapshotCount / 2)
					{
						int matches = 0;
						for (int i = start; i < end; i++)
						{
							int index = m_sortedIndices[i];
							if (InRange(index, point, squared, excluded)) m_matches[matches++] = index;
						}
						// Preserve snapshot recipient order, including during nested sends.
						Array.Sort(m_matches, 0, matches);
						for (int i = 0; i < matches; i++) recipients.Add(m_snapshotIds[m_matches[i]]);
						m_previousMatchCount = matches;
						return recipients;
					}
				}

				for (int i = 0; i < m_snapshotCount; i++)
					if (InRange(i, point, squared, excluded)) recipients.Add(m_snapshotIds[i]);
				m_previousMatchCount = recipients.Count;
				return recipients;
			}
			catch
			{
				ReleaseRecipients(recipients);
				throw;
			}
		}

		private bool InRange(int index, Vector3D point, double squared, ulong excluded)
		{
			Vector3D position = m_snapshotPositions[index];
			double dx = position.X - point.X, dy = position.Y - point.Y, dz = position.Z - point.Z;
			return m_snapshotIds[index] != excluded && dx * dx + dy * dy + dz * dz < squared;
		}

		private static double Coordinate(Vector3D point, int axis)
		{
			return axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;
		}

		private static bool IsFinite(double value)
		{
			return !double.IsNaN(value) && !double.IsInfinity(value);
		}

		private bool EnsureBounds()
		{
			if (m_boundsBuilt) return m_boundsValid;
			m_boundsBuilt = true;
			Vector3D min = m_snapshotPositions[0], max = min;
			for (int i = 0; i < m_snapshotCount; i++)
			{
				Vector3D p = m_snapshotPositions[i];
				if (!IsFinite(p.X) || !IsFinite(p.Y) || !IsFinite(p.Z)) return false;
				min.X = Math.Min(min.X, p.X); max.X = Math.Max(max.X, p.X);
				min.Y = Math.Min(min.Y, p.Y); max.Y = Math.Max(max.Y, p.Y);
				min.Z = Math.Min(min.Z, p.Z); max.Z = Math.Max(max.Z, p.Z);
			}
			m_boundsMin = min;
			m_boundsMax = max;
			m_boundsValid = true;
			return true;
		}

		private bool AllPlayersInRange(Vector3D point, double squared)
		{
			if (!EnsureBounds()) return false;
			double dx = Math.Max(Math.Abs(m_boundsMin.X - point.X), Math.Abs(m_boundsMax.X - point.X));
			double dy = Math.Max(Math.Abs(m_boundsMin.Y - point.Y), Math.Abs(m_boundsMax.Y - point.Y));
			double dz = Math.Max(Math.Abs(m_boundsMin.Z - point.Z), Math.Abs(m_boundsMax.Z - point.Z));
			return dx * dx + dy * dy + dz * dz < squared;
		}

		private void BuildSpatialIndex()
		{
			m_indexBuilt = true;
			if (!EnsureBounds()) return;
			Vector3D min = m_boundsMin, max = m_boundsMax;
			double x = max.X - min.X, y = max.Y - min.Y, z = max.Z - min.Z;
			m_indexAxis = y > x ? (z > y ? 2 : 1) : (z > x ? 2 : 0);
			for (int i = 0; i < m_snapshotCount; i++)
			{
				m_sortedCoordinates[i] = Coordinate(m_snapshotPositions[i], m_indexAxis);
				m_sortedIndices[i] = i;
			}
			Array.Sort(m_sortedCoordinates, m_sortedIndices, 0, m_snapshotCount);
			m_indexValid = true;
		}

		private int LowerBound(double value)
		{
			int low = 0, high = m_snapshotCount;
			while (low < high)
			{
				int mid = low + (high - low) / 2;
				if (m_sortedCoordinates[mid] < value) low = mid + 1;
				else high = mid;
			}
			return low;
		}

		private int UpperBound(double value)
		{
			int low = 0, high = m_snapshotCount;
			while (low < high)
			{
				int mid = low + (high - low) / 2;
				if (m_sortedCoordinates[mid] <= value) low = mid + 1;
				else high = mid;
			}
			return low;
		}

		internal void ReleaseRecipients(List<ulong> recipients)
		{
			recipients.Clear();
			m_recipientPool.Push(recipients);
		}

		internal void SendPrepared(Command cmd, List<ulong> recipients, bool isReliable)
		{
			if (recipients.Count == 0) return;
			Compress(cmd);
			byte[] packet = Encode(cmd);
			for (int i = 0; i < recipients.Count; i++) Send(packet, recipients[i], isReliable);
			if (LogNetworkTraffic)
				MyLog.Default.Info($"[NetworkAPI] TRANSMITTING Bytes: {packet.Length} To: {recipients.Count} Users");
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
			m_queryCount = 0;
			m_previousMatchCount = 0;
			m_indexBuilt = false;
			m_indexValid = false;
			m_boundsBuilt = false;
			m_boundsValid = false;
			m_snapshotSource.Clear();

			MyAPIGateway.Players.GetPlayers(m_snapshotSource);

			int count = m_snapshotSource.Count;

			if (count > m_snapshotPositions.Length)
			{
				int capacity = Math.Max(count, m_snapshotPositions.Length * 2);
				m_snapshotPositions = new Vector3D[capacity];
				m_snapshotIds = new ulong[capacity];
				m_sortedCoordinates = new double[capacity];
				m_sortedIndices = new int[capacity];
				m_matches = new int[capacity];
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
			ShowLocally(cmd.Message);
			List<ulong> recipients = CollectRecipients(point, radius, steamId, cmd.SteamId);
			try { SendPrepared(cmd, recipients, isReliable); }
			finally { ReleaseRecipients(recipients); }
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
