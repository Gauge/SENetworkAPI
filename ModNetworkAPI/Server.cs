using Sandbox.ModAPI;
using System;
using VRage.Utils;

namespace ModNetworkAPI
{
    internal class Server : ICommunicate
    {
        /// <summary>
        /// Event triggers apon reciveing data from the client
        /// </summary>
        public event Action<Command> OnCommandRecived = delegate { };
        
        /// <summary>
        /// Event triggers apon client chat input starting with this mods Keyword
        /// </summary>
        public event Action<string> OnTerminalInput = delegate { };

        private ushort ModId;
        private string Keyword;

        public MultiplayerTypes MultiplayerType => GetMultiplayerType();   

        public Server(ushort modId, string keyword)
        {
            ModId = modId;
            Keyword = keyword;
            MyAPIGateway.Utilities.MessageEntered += HandleChatInput;
            MyAPIGateway.Multiplayer.RegisterMessageHandler(modId, HandleMessage);
        }

        public void Close()
        {
            MyAPIGateway.Utilities.MessageEntered -= HandleChatInput;
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(ModId, HandleMessage);
        }

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
                MyLog.Default.Error($"Did not recieve a command packet. Mod Id may be compromise. Please send a list of all mods used with this on to me (the mod owner)\n{e.ToString()}");
            }
        }

        private void HandleChatInput(string messageText, ref bool sendToOthers)
        {
            string[] args = messageText.Split(' ');
            if (args[0].ToLower() != Keyword) return;
            sendToOthers = false;

            OnTerminalInput.Invoke(messageText.Substring(Keyword.Length).Trim(' '));
        }

        /// <summary>
        /// One of two methods for sending server messages
        /// </summary>
        /// <param name="arguments">Command argument string</param>
        /// <param name="message">Text for display purposes</param>
        /// <param name="steamId">Player Identifier</param>
        public void SendCommand(string arguments, string message = null, ulong steamId = ulong.MinValue)
        {
            SendCommand(new Command { Arguments = arguments, Message = message }, steamId);
        }

        public void SendCommand(Command cmd, ulong steamId = ulong.MinValue)
        {
            byte[] data = ((object)cmd) as byte[];

            if (steamId == ulong.MinValue)
            {
                MyAPIGateway.Multiplayer.SendMessageToOthers(ModId, data);
            }
            else
            {
                MyAPIGateway.Multiplayer.SendMessageTo(ModId, data, steamId);
            }
        }

        public static MultiplayerTypes GetMultiplayerType()
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
