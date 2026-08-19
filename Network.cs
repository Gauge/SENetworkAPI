using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage;
using VRage.Utils;
using VRageMath;

namespace SENetworkAPI
{
	/// <summary>Which side of the connection an instance runs on.</summary>
	public enum NetworkTypes { Dedicated, Server, Client }

	/// <summary>
	/// Send and receive layer for one mod on one communication channel.
	/// Call <see cref="Init"/> once, then use <see cref="Instance"/>.
	/// </summary>
	public abstract class NetworkAPI
	{
		/// <summary>
		/// Version of the API these sources came from. Mods embed the source
		/// rather than referencing a binary, so this is the only way to tell
		/// which build a given mod is carrying.
		/// </summary>
		public const string Version = "2.0.0";

		/// <summary>The instance for this mod. Null until <see cref="Init"/> is called.</summary>
		public static NetworkAPI Instance = null;
		/// <summary>True once <see cref="Init"/> has run.</summary>
		public static bool IsInitialized => Instance != null;
		/// <summary>Writes every packet and property update to the game log.</summary>
		public static bool LogNetworkTraffic = false;

		/// <summary>
		/// Payload size in bytes above which a packet is compressed. Compression
		/// is flagged per packet, so this may be changed at runtime and the two
		/// ends need not agree.
		/// </summary>
		public static int CompressionThreshold = 1024;

		/// <summary>
		/// Size in bytes above which the game discards an unreliable message.
		/// Packets over this are sent reliably instead.
		/// </summary>
		public const int UnreliableMessageLimit = 1024;

		internal static bool ResolveReliability(byte[] packet, bool isReliable)
		{
			return isReliable || packet.Length > UnreliableMessageLimit;
		}

		internal static void Compress(Command cmd)
		{
			if (cmd.IsCompressed || cmd.Data == null || cmd.Data.Length <= CompressionThreshold)
			{
				return;
			}

			byte[] compressed = MyCompression.Compress(cmd.Data);

			if (compressed.Length >= cmd.Data.Length)
			{
				return;
			}

			cmd.Data = compressed;
			cmd.IsCompressed = true;
		}

		/// <summary>
		/// Raised for every command packet received, registered or not.
		/// Provides sender, command string, data and send time.
		/// </summary>
		public event Action<ulong, string, byte[], DateTime> OnCommandRecived;

		/// <summary>The communication channel this mod sends and listens on.</summary>
		public readonly ushort ComId;
		/// <summary>Chat command prefix, lowercased. Null when chat commands are off.</summary>
		public readonly string Keyword;
		/// <summary>Sender name used for chat messages the API prints.</summary>
		public readonly string ModName;

		internal bool UsingTextCommands => Keyword != null;

		/// <summary>
		/// Whether this instance is a client, a listen server or a dedicated
		/// server. Derived from the instance type, so
		/// <c>NetworkType != NetworkTypes.Client</c> guarantees a
		/// <see cref="Server"/> cast will succeed.
		/// </summary>
		public NetworkTypes NetworkType
		{
			get
			{
				if (this is Client)
				{
					return NetworkTypes.Client;
				}

				return MyAPIGateway.Utilities.IsDedicated ? NetworkTypes.Dedicated : NetworkTypes.Server;
			}
		}

		internal Dictionary<string, Action<ulong, string, byte[], DateTime>> NetworkCommands = new Dictionary<string, Action<ulong, string, byte[], DateTime>>(StringComparer.OrdinalIgnoreCase);
		internal Dictionary<string, Action<string>> ChatCommands = new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Use <see cref="Init"/> instead of constructing this directly.</summary>
		/// <param name="comId">The communication channel this mod sends and listens on</param>
		/// <param name="modName">Sender name used for chat messages the API prints</param>
		/// <param name="keyword">Chat command prefix, or null to disable chat commands</param>
		public NetworkAPI(ushort comId, string modName, string keyword = null)
		{
			ComId = comId;
			ModName = (modName == null) ? string.Empty : modName;
			Keyword = (keyword != null) ? keyword.ToLowerInvariant() : null;

			if (UsingTextCommands)
			{
				MyAPIGateway.Utilities.MessageEntered -= HandleChatInput;
				MyAPIGateway.Utilities.MessageEntered += HandleChatInput;
			}

			MyAPIGateway.Multiplayer.UnregisterMessageHandler(ComId, HandleIncomingPacket);
			MyAPIGateway.Multiplayer.RegisterMessageHandler(ComId, HandleIncomingPacket);

			MyLog.Default.Info($"[NetworkAPI] Initialized. Version: {Version} Type: {GetType().Name} ComId: {ComId} Name: {ModName} Keyword: {Keyword}");
		}

		private void HandleChatInput(string messageText, ref bool sendToOthers)
		{
			if (!StartsWithKeyword(messageText))
				return;

			sendToOthers = false;

			string command = SecondToken(messageText);

			Action<string> callback;
			if (command == null)
			{
				if (ChatCommands.TryGetValue(string.Empty, out callback))
				{
					Invoke(callback, string.Empty, string.Empty);
					return;
				}
			}
			else if (ChatCommands.TryGetValue(command, out callback))
			{
				Invoke(callback, command, messageText.Substring(Keyword.Length + 1 + command.Length).Trim(' '));
				return;
			}

			if (!MyAPIGateway.Utilities.IsDedicated)
			{
				MyAPIGateway.Utilities.ShowMessage(ModName, "Command not recognized.");
			}
		}

		private bool StartsWithKeyword(string messageText)
		{
			if (messageText == null || Keyword == null)
				return false;

			int length = Keyword.Length;

			if (messageText.Length < length)
				return false;

			if (messageText.Length > length && messageText[length] != ' ')
				return false;

			return string.Compare(messageText, 0, Keyword, 0, length, StringComparison.OrdinalIgnoreCase) == 0;
		}

		private string SecondToken(string messageText)
		{
			int start = Keyword.Length + 1;

			if (start > messageText.Length)
				return null;

			int end = messageText.IndexOf(' ', start);

			return (end < 0) ? messageText.Substring(start) : messageText.Substring(start, end - start);
		}

		private void Invoke(Action<string> callback, string command, string arguments)
		{
			if (callback == null)
			{
				return;
			}

			try
			{
				callback(arguments);
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Chat command '{Keyword} {command}' threw:\n{e}");
			}
		}

		private void HandleIncomingPacket(byte[] msg)
		{
			try
			{
				Command cmd = MyAPIGateway.Utilities.SerializeFromBinary<Command>(msg);

				if (cmd == null)
				{
					if (LogNetworkTraffic)
					{
						MyLog.Default.Info($"[NetworkAPI] Ignored an empty packet on ComId {ComId}");
					}

					return;
				}

				if (LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] ----- TRANSMISSION RECIEVED -----");
					MyLog.Default.Info($"[NetworkAPI] Type: {((cmd.IsProperty) ? "Property" : $"Command ID: {cmd.CommandString}")}, {(cmd.IsCompressed ? "Compressed, " : "")}From: {cmd.SteamId} ");
				}

				if (cmd.IsCompressed)
				{
					cmd.Data = MyCompression.Decompress(cmd.Data);
					cmd.IsCompressed = false;
				}

				if (cmd.IsProperty)
				{
					if (cmd.Property != null)
					{
						NetSync.RouteMessage(cmd.Property, cmd.SteamId, cmd.Timestamp);
					}
					else if (cmd.Properties != null)
					{
						for (int i = 0; i < cmd.Properties.Count; i++)
						{
							NetSync.RouteMessage(cmd.Properties[i], cmd.SteamId, cmd.Timestamp);
						}
					}
					else
					{
						NetSync.RouteMessage(MyAPIGateway.Utilities.SerializeFromBinary<SyncData>(cmd.Data), cmd.SteamId, cmd.Timestamp);
					}
				}
				else
				{
					if (!string.IsNullOrWhiteSpace(cmd.Message))
					{
						if (!MyAPIGateway.Utilities.IsDedicated)
						{
							if (MyAPIGateway.Session != null)
							{
								MyAPIGateway.Utilities.ShowMessage(ModName, cmd.Message);
							}
						}

						if (MyAPIGateway.Multiplayer.IsServer)
						{
							SendCommand(null, cmd.Message);
						}
					}

					if (cmd.CommandString != null)
					{
						DateTime sent = ToDateTime(cmd.Timestamp);

						Invoke(OnCommandRecived, cmd, sent, "OnCommandRecived");

						int space = cmd.CommandString.IndexOf(' ');
						string command = (space < 0) ? cmd.CommandString : cmd.CommandString.Substring(0, space);

						Action<ulong, string, byte[], DateTime> callback;
						if (NetworkCommands.TryGetValue(command, out callback))
						{
							Invoke(callback, cmd, sent, command);
						}
					}
				}

				if (LogNetworkTraffic)
				{
					MyLog.Default.Info($"[NetworkAPI] ----- END -----");
				}

			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Failure in message processing:\n{e.ToString()}");
			}
		}

		private static DateTime ToDateTime(long timestamp)
		{
			if (timestamp < 0)
			{
				return DateTime.MinValue;
			}

			if (timestamp > DateTime.MaxValue.Ticks)
			{
				return DateTime.MaxValue;
			}

			return new DateTime(timestamp);
		}

		private void Invoke(Action<ulong, string, byte[], DateTime> callback, Command cmd, DateTime sent, string label)
		{
			if (callback == null)
			{
				return;
			}

			try
			{
				callback(cmd.SteamId, cmd.CommandString, cmd.Data, sent);
			}
			catch (Exception e)
			{
				MyLog.Default.Error($"[NetworkAPI] Network command '{label}' threw:\n{e}");
			}
		}

		/// <summary>
		/// Registers a callback for a command name. Names are case insensitive
		/// and may only be registered once. Null is not a valid name.
		/// </summary>
		/// <param name="command">The command name to handle</param>
		/// <param name="callback">Receives sender, full command string, data and send time</param>
		/// <exception cref="Exception">The name is null or already registered</exception>
		public void RegisterNetworkCommand(string command, Action<ulong, string, byte[], DateTime> callback)
		{
			if (command == null)
			{
				throw new Exception($"[NetworkAPI] Cannot register a command using null. null is reserved for chat messages.");
			}

			if (NetworkCommands.ContainsKey(command))
			{
				throw new Exception($"[NetworkAPI] Failed to add the network command callback '{command}'. A command with the same name was already added.");
			}

			NetworkCommands.Add(command, callback);
		}

		/// <summary>Removes a command callback. No-op if it was never registered.</summary>
		/// <param name="command">The command name to remove</param>
		public void UnregisterNetworkCommand(string command)
		{
			if (command != null)
			{
				NetworkCommands.Remove(command);
			}
		}

		/// <summary>
		/// Registers a callback for <c>&lt;keyword&gt; &lt;command&gt;</c> typed in chat.
		/// Names are case insensitive and may only be registered once. Null or
		/// empty registers the handler for the bare keyword.
		/// </summary>
		/// <param name="command">The word typed after the keyword</param>
		/// <param name="callback">Receives everything typed after the command word</param>
		/// <exception cref="Exception">The name is already registered</exception>
		public void RegisterChatCommand(string command, Action<string> callback)
		{
			if (command == null)
			{
				command = string.Empty;
			}

			if (ChatCommands.ContainsKey(command))
			{
				throw new Exception($"[NetworkAPI] Failed to add the network command callback '{command}'. A command with the same name was already added.");
			}

			ChatCommands.Add(command, callback);
		}

		/// <summary>Removes a chat command callback. No-op if it was never registered.</summary>
		/// <param name="command">The chat command to remove</param>
		public void UnregisterChatCommand(string command)
		{
			ChatCommands.Remove(command ?? string.Empty);
		}

		/// <summary>
		/// Sends a command. A client always sends to the server; a server sends
		/// to one client, or to all of them when no steam id is given.
		/// </summary>
		/// <param name="commandString">Command name, plus any arguments delimited with spaces</param>
		/// <param name="message">Text to display in chat on arrival</param>
		/// <param name="data">Serialized payload</param>
		/// <param name="sent">Send timestamp. Defaults to now</param>
		/// <param name="steamId">Recipient, or 0 for all. Ignored by clients</param>
		/// <param name="isReliable">False permits the unreliable channel for small packets</param>
		public abstract void SendCommand(string commandString, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true);

		/// <summary>
		/// Sends a command to clients within a radius of a point. Clients ignore
		/// the position and send to the server.
		/// </summary>
		/// <param name="commandString">Command name, plus any arguments delimited with spaces</param>
		/// <param name="point">Center of the send sphere, in world space</param>
		/// <param name="radius">Radius of the send sphere. 0 uses the world's sync distance</param>
		/// <param name="message">Text to display in chat on arrival</param>
		/// <param name="data">Serialized payload</param>
		/// <param name="sent">Send timestamp. Defaults to now</param>
		/// <param name="steamId">Recipient, ignoring the radius, or 0 for everyone in range</param>
		/// <param name="isReliable">False permits the unreliable channel for small packets</param>
		public abstract void SendCommand(string commandString, Vector3D point, double radius = 0, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true);

		internal abstract void SendCommand(Command cmd, ulong steamId = ulong.MinValue, bool isReliable = true);

		internal abstract void SendCommand(Command cmd, Vector3D point, double range = 0, ulong steamId = ulong.MinValue, bool isReliable = true);

		/// <summary>Posts a line of chat under <see cref="ModName"/>.</summary>
		/// <param name="message">The text to post</param>
		public abstract void Say(string message);

		/// <summary>Unregisters the message and chat handlers.</summary>
		[ObsoleteAttribute("This property is obsolete. Close is no longer required", false)]
		public void Close()
		{
			MyLog.Default.Info($"[NetworkAPI] Unregistering communication stream: {ComId}");
			if (UsingTextCommands)
			{
				MyAPIGateway.Utilities.MessageEntered -= HandleChatInput;
			}

			MyAPIGateway.Multiplayer.UnregisterMessageHandler(ComId, HandleIncomingPacket);

		}

		/// <summary>Closes the instance and clears the property registries.</summary>
		[ObsoleteAttribute("This property is obsolete. Dispose is no longer required", false)]
		public static void Dispose()
		{
			if (IsInitialized)
			{
				Instance.Close();
			}

			Instance = null;

			NetSync.ClearRegistries();
		}

		/// <summary>
		/// Creates the instance for this mod, a <see cref="Client"/> or a
		/// <see cref="Server"/> depending on the game state. No-op if already
		/// initialized. Must run on the game update thread.
		/// </summary>
		/// <param name="comId">The communication channel this mod sends and listens on. Shared game wide, so pick an uncommon value</param>
		/// <param name="modName">Sender name used for chat messages the API prints</param>
		/// <param name="keyword">Chat command prefix, or null to disable chat commands</param>
		public static void Init(ushort comId, string modName, string keyword = null)
		{
			if (IsInitialized)
				return;

			if (!MyAPIGateway.Multiplayer.IsServer)
			{
				Instance = new Client(comId, modName, keyword);
			}
			else
			{
				Instance = new Server(comId, modName, keyword);
			}
		}

		/// <summary>Milliseconds between a timestamp and now. Whole milliseconds only.</summary>
		/// <param name="timestamp">A DateTime.Ticks value</param>
		public static float GetDeltaMilliseconds(long timestamp)
		{
			return (DateTime.UtcNow.Ticks - timestamp) / TimeSpan.TicksPerMillisecond;
		}

		/// <summary>Frames at 60fps between a timestamp and now, rounded up.</summary>
		/// <param name="timestamp">A DateTime.Ticks value</param>
		public static int GetDeltaFrames(long timestamp)
		{
			return (int)Math.Ceiling(GetDeltaMilliseconds(timestamp) / MillisecondsPerFrame);
		}

		private const double MillisecondsPerFrame = 1000d / 60d;
	}
}
