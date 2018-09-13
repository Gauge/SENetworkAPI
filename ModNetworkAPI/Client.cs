using Sandbox.ModAPI;
using System;
using VRage.Utils;

namespace ModNetworkAPI
{
    public class Client : ICommunicate
    {
        /// <summary>
        /// Event triggers apon reciveing data from the server
        /// </summary>
        public event Action<Command> OnCommandRecived = delegate { };
        
        /// <summary>
        /// Event triggers apon client chat input starting with this mods Keyword
        /// </summary>
        public event Action<string> OnTerminalInput = delegate { };

        private ushort ComId;
        private string Keyword;

        public MultiplayerTypes MultiplayerType => Server.GetMultiplayerType();

        /// <summary>
        /// Handles communication with the server
        /// </summary>
        /// <param name="comId">Identifies the channel to pass information to and from this mod</param>
        /// <param name="keyword">identifies what chat entries should be captured and sent to the server</param>
        public Client(ushort comId, string keyword)
        {
            ComId = comId;
            Keyword = keyword.ToLower();

            MyAPIGateway.Utilities.MessageEntered += HandleChatInput;
            MyAPIGateway.Multiplayer.RegisterMessageHandler(this.ComId, HandleMessage);
        }

        /// <summary>
        /// Closes any long term processes that are no longer needed
        /// </summary>
        public void Close()
        {
            MyLog.Default.Info($"Unregistering communication stream: {ComId}");
            MyAPIGateway.Utilities.MessageEntered -= HandleChatInput;
            MyAPIGateway.Multiplayer.UnregisterMessageHandler(this.ComId, HandleMessage);
        }

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
        /// <param name="msg">Data chunck recived from server</param>
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
                MyLog.Default.Error($"Failed to unpack message:\n{e.ToString()}");
            }
        }

        /// <summary>
        /// A methods for sending server messages
        /// </summary>
        /// <param name="arguments">The argument, or "Command", to be executed by the server</param>
        /// <param name="message">Text that will be displayed to the user</param>
        /// <param name="steamId">The players steam id</param>
        public void SendCommand(string arguments, string message = null, ulong steamId = ulong.MinValue)
        {
            SendCommand(new Command {Arguments = arguments, Message = message }, steamId);
        }

        /// <summary>
        /// A methods for sending server messages
        /// </summary>
        /// <param name="cmd">The object to be sent to the server</param>
        /// <param name="steamId">The players steam id</param>
        public void SendCommand(Command cmd, ulong steamId = ulong.MinValue)
        {
            cmd.SteamId = MyAPIGateway.Session.Player.SteamUserId;
            byte[] data = ((object)cmd) as byte[];

            MyAPIGateway.Multiplayer.SendMessageToServer(ComId, data, true);
        }
    }
}
