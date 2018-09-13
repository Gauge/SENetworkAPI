using Sandbox.ModAPI;
using System;
using VRage.Utils;

namespace ModNetworkAPI
{
    public class Server : NetworkAPI
    {
        /// <summary>
        /// Handles communication with clients
        /// </summary>
        /// <param name="comId">Identifies the channel to pass information to and from this mod</param>
        /// <param name="keyword">identifies what chat entries should be captured and sent to the server</param>
        public Server(ushort comId, string keyword = null) : base(comId, keyword)
        {
        }

        /// <summary>
        /// A methods for sending client messages
        /// </summary>
        /// <param name="arguments">The argument, or "Command", to be executed by the client</param>
        /// <param name="message">Text that will be displayed to the user</param>
        /// <param name="steamId">The players steam id</param>
        public override void SendCommand(string arguments, string message = null, ulong steamId = ulong.MinValue)
        {
            SendCommand(new Command { Arguments = arguments, Message = message }, steamId);
        }

        /// <summary>
        /// A methods for sending client messages
        /// </summary>
        /// <param name="cmd">The object to be sent to the client</param>
        /// <param name="steamId">The players steam id</param>
        public override void SendCommand(Command cmd, ulong steamId = ulong.MinValue)
        {
            byte[] data = ((object)cmd) as byte[];

            if (steamId == ulong.MinValue)
            {
                MyAPIGateway.Multiplayer.SendMessageToOthers(ComId, data);
            }
            else
            {
                MyAPIGateway.Multiplayer.SendMessageTo(ComId, data, steamId);
            }
        }
    }
}
