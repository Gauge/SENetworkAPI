using Sandbox.ModAPI;
using VRage.Utils;

namespace ModNetworkAPI
{
    public class Client : NetworkAPI
    {

        /// <summary>
        /// Handles communication with the server
        /// </summary>
        /// <param name="comId">Identifies the channel to pass information to and from this mod</param>
        /// <param name="keyword">identifies what chat entries should be captured and sent to the server</param>
        public Client(ushort comId, string modName, string keyword = null) : base(comId, modName, keyword)
        {
        }

        /// <summary>
        /// Sends a command packet to the server
        /// </summary>
        /// <param name="commandString">The command to be executed</param>
        /// <param name="message">Text that will be displayed in client chat</param>
        /// <param name="data">A serialized object to be sent across the network</param>
        /// <param name="steamId">The client reciving this packet (if 0 it sends to all clients)</param>
        /// <param name="isReliable">Enture delivery of the packet</param>
        public override void SendCommand(string commandString, string message = null, byte[] data = null, ulong steamId = ulong.MinValue, bool isReliable = true)
        {
            if (MyAPIGateway.Session?.Player != null)
            {
                byte[] packet = MyAPIGateway.Utilities.SerializeToBinary(new Command() { CommandString = commandString, Message = message, Data = data, SteamId = MyAPIGateway.Session.Player.SteamUserId });
                MyAPIGateway.Multiplayer.SendMessageToServer(ComId, packet, isReliable);
            }
            else
            {
                MyLog.Default.Warning($"[NetworkAPI] ComID:{ComId} | Failed to send command. Session does not exist.");
            }
        }
    }
}
