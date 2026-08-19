using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage;
using VRage.Utils;
using VRageMath;

namespace SENetworkAPI
{
	public enum NetworkTypes { Dedicated, Server, Client }

	public abstract class NetworkAPI
	{
		public static NetworkAPI Instance = null;
		public static bool IsInitialized => Instance != null;
		public static bool LogNetworkTraffic = false;

		/// <summary>
		/// Payloads larger than this are compressed before sending. The old
		/// value of 100000 was far above both the network MTU and the engine's
		/// 1024 byte ceiling for unreliable messages, so nothing under 100KB was
		/// ever compressed. Compression is self describing - packets carry a
		/// flag - so changing this is safe at any time and needs no agreement
		/// between the two ends.
		/// </summary>
		public static int CompressionThreshold = 1024;

		/// <summary>
		/// The engine drops any unreliable message longer than this, silently.
		/// </summary>
		public const int UnreliableMessageLimit = 1024;

		/// <summary>
		/// The engine refuses an unreliable message over its size limit and
		/// reports it through a return value nothing reads, so an oversized one
		/// is sent reliably instead of disappearing.
		/// </summary>
		internal static bool ResolveReliability(byte[] packet, bool isReliable)
		{
			return isReliable || packet.Length > UnreliableMessageLimit;
		}

		/// <summary>
		/// Compresses the payload when it is over the threshold, keeping the
		/// result only if it actually came out smaller. Safe to call more than
		/// once on the same command; only the first call can do anything.
		/// </summary>
		internal static void Compress(Command cmd)
		{
			if (cmd.IsCompressed || cmd.Data == null || cmd.Data.Length <= CompressionThreshold)
			{
				return;
			}

			byte[] compressed = MyCompression.Compress(cmd.Data);

			// Small or already-compressed payloads can come out bigger.
			if (compressed.Length >= cmd.Data.Length)
			{
				return;
			}

			cmd.Data = compressed;
			cmd.IsCompressed = true;
		}

		/// <summary>
		/// Event triggers apon reciveing data over the network
		/// steamId, command, data
		/// </summary>
		public event Action<ulong, string, byte[], DateTime> OnCommandRecived;

		public readonly ushort ComId;
		public readonly string Keyword;
		public readonly string ModName;

		internal bool UsingTextCommands => Keyword != null;

		/// <summary>
		/// Which side of the connection this instance is. A listen server and a
		/// dedicated server are both <see cref="Server"/> instances and differ
		/// only here.
		/// </summary>
		public NetworkTypes NetworkType
		{
			get
			{
				// Deliberately the instance type, not a live IsServer check.
				// Init picked Client or Server once, from the game state at the
				// time, and every send path is that choice. Asking the session
				// again could answer Server for an instance that is actually a
				// Client, and the documented way to use this is:
				//
				//     if (Network.NetworkType != NetworkTypes.Client)
				//         Server s = (Server)Network;
				//
				// which would then throw. Only the Dedicated/Server split needs
				// the game, because both are Server instances.
				if (!(this is Server))
				{
					return NetworkTypes.Client;
				}

				return MyAPIGateway.Utilities.IsDedicated ? NetworkTypes.Dedicated : NetworkTypes.Server;
			}
		}

		// Ordinal, case insensitive: lookups no longer have to lower-case the
		// incoming string (an allocation on every packet), registration keeps the
		// caller's spelling, and "Update" now finds the handler registered as
		// "update" instead of silently missing it.
		internal Dictionary<string, Action<ulong, string, byte[], DateTime>> NetworkCommands = new Dictionary<string, Action<ulong, string, byte[], DateTime>>(StringComparer.OrdinalIgnoreCase);
		internal Dictionary<string, Action<string>> ChatCommands = new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Event driven client, server syncing API. 
		/// </summary>
		/// <param name="comId">The communication channel this mod will listen on</param>
		/// <param name="modName">The title use for displaying chat messages</param>
		/// <param name="keyward">The string identifying a chat command</param>
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

			MyLog.Default.Info($"[NetworkAPI] Initialized. Type: {GetType().Name} ComId: {ComId} Name: {ModName} Keyword: {Keyword}");
		}

		/// <summary>
		/// Invokes chat command events
		/// </summary>
		/// <param name="messageText">Chat message string</param>
		/// <param name="sendToOthers">should be shown normally in global chat</param>
		private void HandleChatInput(string messageText, ref bool sendToOthers)
		{
			// Every line typed by the player reaches every mod's handler, so the
			// common case - a line that is not ours - must not allocate at all.
			if (!StartsWithKeyword(messageText))
				return;

			sendToOthers = false;

			string command = SecondToken(messageText);

			Action<string> callback;
			if (command == null)
			{
				// The keyword on its own maps to the empty command.
				if (ChatCommands.TryGetValue(string.Empty, out callback))
				{
					Invoke(callback, string.Empty, string.Empty);
					return;
				}
			}
			else if (ChatCommands.TryGetValue(command, out callback))
			{
				// Everything after the command word, cut straight out of the
				// message: building the whole tail first and trimming the
				// command off it costs a second string for nothing.
				Invoke(callback, command, messageText.Substring(Keyword.Length + 1 + command.Length).Trim(' '));
				return;
			}

			if (!MyAPIGateway.Utilities.IsDedicated)
			{
				MyAPIGateway.Utilities.ShowMessage(ModName, "Command not recognized.");
			}
		}

		/// <summary>
		/// True when the message's first whitespace delimited word is the keyword.
		/// Allocation free: no lower-casing, no splitting.
		/// </summary>
		private bool StartsWithKeyword(string messageText)
		{
			if (messageText == null || Keyword == null)
				return false;

			int length = Keyword.Length;

			if (messageText.Length < length)
				return false;

			// "/modding" must not trigger the "/mod" keyword.
			if (messageText.Length > length && messageText[length] != ' ')
				return false;

			return string.Compare(messageText, 0, Keyword, 0, length, StringComparison.OrdinalIgnoreCase) == 0;
		}

		/// <summary>
		/// The word following the keyword, or null when the message is just the
		/// keyword. Callers have already checked <see cref="StartsWithKeyword"/>.
		/// </summary>
		private string SecondToken(string messageText)
		{
			int start = Keyword.Length + 1;

			if (start > messageText.Length)
				return null;

			int end = messageText.IndexOf(' ', start);

			return (end < 0) ? messageText.Substring(start) : messageText.Substring(start, end - start);
		}

		/// <summary>
		/// Runs a chat callback. This is called straight from the game's
		/// MessageEntered event, which is a multicast delegate shared with every
		/// other mod: letting an exception escape would stop the mods behind us
		/// in the invocation list from ever seeing the message.
		/// </summary>
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

		/// <summary>
		/// Unpacks commands and handles arguments
		/// </summary>
		/// <param name="msg">Data chunck recived from the network</param>
		private void HandleIncomingPacket(byte[] msg)
		{
			try
			{
				Command cmd = MyAPIGateway.Utilities.SerializeFromBinary<Command>(msg);

				if (cmd == null)
				{
					// An empty packet, or another mod's traffic on our channel.
					// Not worth an exception and a stack trace every time.
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
						// A packet from a build using the original layout, which
						// shipped the update as pre-encoded bytes in Data.
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

						// The command is the first word. Split() would allocate an
						// array plus a string per argument just to read it; a
						// command with no arguments now costs nothing at all.
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

		/// <summary>
		/// Turns a wire timestamp into a DateTime. The value is whatever the
		/// sender wrote, and DateTime's constructor throws outside its range, so
		/// a corrupt or hostile packet would otherwise cost us the whole message.
		/// </summary>
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

		/// <summary>
		/// Runs a network callback in isolation, so one mod handler that throws
		/// does not abandon the rest of the packet.
		/// </summary>
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
		/// Registers a callback that will fire when the command string is sent
		/// </summary>
		/// <param name="command">The command that triggers the callback</param>
		/// <param name="callback">The function that runs when a command is recived</param>
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

		/// <summary>
		/// Unregisters a command
		/// </summary>
		/// <param name="command"></param>
		public void UnregisterNetworkCommand(string command)
		{
			if (command != null)
			{
				NetworkCommands.Remove(command);
			}
		}

		/// <summary>
		/// will trigger when you type <keyword> <command>
		/// </summary>
		/// <param name="command">this is the text command that will be typed into chat</param>
		/// <param name="callback">this is the function that will be called when the keyword is typed</param>
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

		/// <summary>
		/// Unregisters a chat command
		/// </summary>
		/// <param name="command">the chat command to unregister</param>
		public void UnregisterChatCommand(string command)
		{
			ChatCommands.Remove(command ?? string.Empty);
		}

		/// <summary>
		/// Sends a command packet across the network
		/// </summary>
		/// <param name="commandString">The command word and any arguments delimidated with spaces</param>
		/// <param name="message">Text to be writen in chat</param>
		/// <param name="data">A serialized object used to send game information</param>
		/// <param name="sent">The date timestamp this command was sent</param>
		/// <param name="steamId">A players steam id</param>
		/// <param name="isReliable">Makes sure the data gets to the target</param>
		public abstract void SendCommand(string commandString, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true);

		/// <summary>
		/// Sends a command packet across the network
		/// </summary>
		/// <param name="commandString">The command word and any arguments delimidated with spaces</param>
		/// <param name="point"></param>
		/// <param name="radius"></param>
		/// <param name="message">Text to be writen in chat</param>
		/// <param name="data">A serialized object used to send game information</param>
		/// <param name="sent">The date timestamp this command was sent</param>
		/// <param name="steamId">A players steam id</param>
		/// <param name="isReliable">Makes sure the data gets to the target</param>
		public abstract void SendCommand(string commandString, Vector3D point, double radius = 0, string message = null, byte[] data = null, DateTime? sent = null, ulong steamId = ulong.MinValue, bool isReliable = true);

		/// <summary>
		/// Sends a command packet to the server / client
		/// </summary>
		/// <param name="cmd">The object to be sent across the network</param>
		/// <param name="steamId">the id of the user this is being sent to. 0 sends it to all users in range</param>
		/// <param name="isReliable">make sure the packet reaches its destination</param>
		internal abstract void SendCommand(Command cmd, ulong steamId = ulong.MinValue, bool isReliable = true);


		/// <summary>
		/// Sends a command packet to the server / client if in range
		/// </summary>
		/// <param name="cmd">The object to be sent across the network</param>
		/// <param name="point">the center of the sending sphere</param>
		/// <param name="range">the radius of the sending sphere</param>
		/// <param name="steamId">the id of the user this is being sent to. 0 sends it to all users in range</param>
		/// <param name="isReliable">make sure the packet reaches its destination</param>
		internal abstract void SendCommand(Command cmd, Vector3D point, double range = 0, ulong steamId = ulong.MinValue, bool isReliable = true);

		/// <summary>
		/// Posts text into the ingame chat.
		/// </summary>
		/// <param name="message"></param>
		public abstract void Say(string message);

		/// <summary>
		/// Unregisters listeners
		/// </summary>
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

		/// <summary>
		/// Calls Instance.Close()
		/// </summary>
		[ObsoleteAttribute("This property is obsolete. Dispose is no longer required", false)]
		public static void Dispose()
		{
			if (IsInitialized)
			{
				Instance.Close();
			}

			Instance = null;

			// The property registries are static and outlive the session. Left
			// alone they keep every property, and every entity that owned one,
			// alive across a world reload.
			NetSync.ClearRegistries();
		}

		/// <summary>
		/// Initializes the default instance of the NetworkAPI
		/// </summary>
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

		/// <summary>
		/// Gets the diffrence between now and a given timestamp in milliseconds
		/// </summary>
		/// <returns></returns>
		public static float GetDeltaMilliseconds(long timestamp)
		{
			return (DateTime.UtcNow.Ticks - timestamp) / TimeSpan.TicksPerMillisecond;
		}

		/// <summary>
		/// Gets the diffrence between now and a given timestamp in frames (60 fps)
		/// </summary>
		/// <param name="timestamp">A DateTime.Ticks value</param>
		/// <returns></returns>
		public static int GetDeltaFrames(long timestamp)
		{
			return (int)Math.Ceiling(GetDeltaMilliseconds(timestamp) / MillisecondsPerFrame);
		}

		private const double MillisecondsPerFrame = 1000d / 60d;
	}
}
