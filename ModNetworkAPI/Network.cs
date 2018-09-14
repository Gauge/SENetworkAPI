using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Utils;

namespace ModNetworkAPI
{
    public enum NetworkTypes { Dedicated, Server, Client }

    public abstract class NetworkAPI
    {
        public static NetworkAPI Instance = null;
        public static bool IsInitialized = Instance != null;

        /// <summary>
        /// Event triggers apon reciveing data over the network
        /// steamId, command, data
        /// </summary>
        public event Action<ulong, string, object> OnCommandRecived;

        /// <summary>
        /// Event triggers apon client chat input starting with this mods Keyword
        /// </summary>
        [Obsolete("Use NetworkAPI.RegisterCommand to handle input")]
        public event Action<string> OnTerminalInput;

        /// <summary>
        /// returns the type of network user this is: dedicated, server, client
        /// </summary>
        public NetworkTypes NetworkType => GetNetworkType();

        internal ushort ComId;
        internal string Keyword = null;
        internal string ModName = null;

        internal bool UsingTextCommands => Keyword != null;

        internal Dictionary<string, Action<ulong, string, object>> Commands = new Dictionary<string, Action<ulong, string, object>>();

        /// <summary>
        /// Sets up the event listeners and create a 
        /// </summary>
        /// <param name="comId"></param>
        /// <param name="modName">The title of your mod</param>
        /// <param name="keyward"></param>
        public NetworkAPI(ushort comId, string modName, string keyword = null)
        {
            ComId = comId;
            ModName = (modName == null) ? string.Empty : modName;

            if (keyword != null)
            {
                Keyword = keyword.ToLower();
            }

            MyAPIGateway.Utilities.MessageEntered += HandleChatInput;

            if (UsingTextCommands)
            {
                MyAPIGateway.Multiplayer.RegisterMessageHandler(ComId, HandleIncomingPacket);
            }
        }

        /// <summary>
        /// Determins if a message is a command. Triggers the TerminalInput event if it is one.
        /// </summary>
        /// <param name="messageText">Chat message string</param>
        /// <param name="sendToOthers">should be shown normally in global chat</param>
        private void HandleChatInput(string messageText, ref bool sendToOthers)
        {
            string[] args = messageText.Split(' ');
            if (args[0].ToLower() != Keyword) return;
            sendToOthers = false;

            OnTerminalInput.Invoke(messageText.Substring(Keyword.Length).Trim(' '));
        }

        /// <summary>
        /// Unpacks commands and handles arguments
        /// </summary>
        /// <param name="msg">Data chunck recived from the network</param>
        private void HandleIncomingPacket(byte[] msg)
        {
            try
            {
                Command cmd = ((object)msg) as Command;

                if (!string.IsNullOrWhiteSpace(cmd.Message) && NetworkType == NetworkTypes.Client && MyAPIGateway.Session != null)
                {
                    MyAPIGateway.Utilities.ShowMessage(ModName, cmd.Message);
                }

                if (cmd != null)
                {
                    OnCommandRecived.Invoke(cmd.SteamId, cmd.CommandString, cmd.Data);
                }

                if (cmd.CommandString == null)
                {
                    cmd.CommandString = string.Empty;
                }

                string command = cmd.CommandString.Split(' ')[0];

                if (Commands.ContainsKey(command))
                {
                    Action<ulong, string, object> action = Commands[command];

                    if (action != null)
                    {
                        action.Invoke(cmd.SteamId, cmd.CommandString, cmd.Data);
                    }
                }

            }
            catch (Exception e)
            {
                MyLog.Default.Error($"[NetworkAPI] Failed to unpack message:\n{e.ToString()}");
            }
        }

        /// <summary>
        /// Registers a callback that will fire when the command string is sent
        /// </summary>
        /// <param name="command">The command that triggers the callback</param>
        /// <param name="callback">The function that runs when a command is recived</param>
        public void RegisterCommand(string command, Action<ulong, string, object> callback)
        {
            if (command == null)
            {
                command = string.Empty;
            }

            command = command.ToLower();

            if (Commands.ContainsKey(command))
            {
                throw new Exception($"[NetworkAPI] Failed to add the command callback '{command}'. A command with the same name was already added.");
            }

            Commands.Add(command, callback);
        }

        /// <summary>
        /// Unregisters a command
        /// </summary>
        /// <param name="command"></param>
        public void UnregisterCommand(string command)
        {
            if (Commands.ContainsKey(command))
            {
                Commands.Remove(command);
            }
        }

        /// <summary>
        /// Sends a command packet across the network
        /// </summary>
        /// <param name="commandString">The command word and any arguments delimidated with spaces</param>
        /// <param name="message">Text to be writen in chat</param>
        /// <param name="data">An object used to send game information</param>
        /// <param name="steamId">A players steam id</param>
        public abstract void SendCommand(string commandString, string message = null, object data = null, ulong steamId = ulong.MinValue, bool isReliable = true);

        public void Close()
        {
            MyLog.Default.Info($"[NetworkAPI] Unregistering communication stream: {ComId}");
            MyAPIGateway.Utilities.MessageEntered -= HandleChatInput;
            if (UsingTextCommands)
            {
                MyAPIGateway.Multiplayer.UnregisterMessageHandler(ComId, HandleIncomingPacket);
            }
        }

        /// <summary>
        /// Initialize
        /// </summary>
        /// <param name="comId"></param>
        /// <param name="keyword"></param>
        public static void Init(ushort comId, string modName, string keyword = null)
        {
            if (IsInitialized) return;

            if (GetNetworkType() == NetworkTypes.Client)
            {
                Instance = new Client(comId, modName, keyword);
            }
            else
            {
                Instance = new Server(comId, modName, keyword);
            }
        }

        /// <summary>
        /// Finds the type of network system the current instance is running on
        /// </summary>
        /// <returns>MultiplayerTypes Enum</returns>
        public static NetworkTypes GetNetworkType()
        {
            if (!MyAPIGateway.Multiplayer.IsServer)
            {
                return NetworkTypes.Client;
            }
            else if (MyAPIGateway.Utilities.IsDedicated)
            {
                return NetworkTypes.Dedicated;
            }

            return NetworkTypes.Server;
        }
    }
}
