using Sandbox.ModAPI;
using System;
using VRage.Utils;

namespace ModNetworkAPI
{
    public enum MultiplayerTypes { Dedicated, Server, Client, Private }

    public abstract class NetworkAPI
    {
        MultiplayerTypes MultiplayerType { get; }

        /// <summary>
        /// Event triggers apon reciveing data over the network
        /// </summary>
        public event Action<Command> OnCommandRecived;

        /// <summary>
        /// Event triggers apon client chat input starting with this mods Keyword
        /// </summary>
        public event Action<string> OnTerminalInput;

        /// <summary>
        /// returns the type of network user this is: dedicated, server, client
        /// </summary>
        public MultiplayerTypes NetworkType => GetNetworkType();

        internal ushort ComId;
        internal string Keyword;

        internal bool UsingTextCommands => Keyword != null;

        /// <summary>
        /// Sets up the event listeners and create a 
        /// </summary>
        /// <param name="comId"></param>
        /// <param name="keyward"></param>
        public NetworkAPI(ushort comId, string keyword = null)
        {
            ComId = comId;

            if (keyword != null)
            {
                Keyword = keyword.ToLower();
            }

            MyAPIGateway.Utilities.MessageEntered += HandleChatInput;

            if (UsingTextCommands)
            {
                MyAPIGateway.Multiplayer.RegisterMessageHandler(this.ComId, HandleMessage);
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
        private void HandleMessage(byte[] msg)
        {
            try
            {
                Command cmd = ((object)msg) as Command;

                if (cmd != null)
                {
                    OnCommandRecived.Invoke(cmd);
                }
            }
            catch (Exception e)
            {
                MyLog.Default.Error($"[NetworkAPI] Failed to unpack message:\n{e.ToString()}");
            }
        }

        public abstract void SendCommand(string arguments, string message = null, ulong steamId = ulong.MinValue);
        public abstract void SendCommand(Command cmd, ulong steamId = ulong.MinValue);

        public void Close()
        {
            MyLog.Default.Info($"[NetworkAPI] Unregistering communication stream: {ComId}");
            MyAPIGateway.Utilities.MessageEntered -= HandleChatInput;
            if (UsingTextCommands)
            {
                MyAPIGateway.Multiplayer.UnregisterMessageHandler(this.ComId, HandleMessage);
            }
        }

        public static MultiplayerTypes GetNetworkType()
        {
            if (!MyAPIGateway.Multiplayer.IsServer)
            {
                return MultiplayerTypes.Client;
            }
            else if (MyAPIGateway.Utilities.IsDedicated)
            {
                return MultiplayerTypes.Dedicated;
            }

            return MultiplayerTypes.Server;
        }
    }
}
