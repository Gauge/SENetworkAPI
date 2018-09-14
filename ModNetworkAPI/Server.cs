using Sandbox.Engine.Multiplayer;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

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

        public override void SendCommand(string commandString, string message = null, object data = null, ulong steamId = ulong.MinValue, bool isReliable = true)
        {
            SendCommand(new Command() { SteamId = steamId, CommandString = commandString, Message = message, Data = data }, steamId, isReliable);
        }

        public void SendCommandTo(ulong[] steamIds, string commandString, string message = null, object data = null, bool isReliable = true)
        {
            foreach (ulong id in steamIds)
            {
                SendCommand(new Command() { SteamId = id, CommandString = commandString, Message = message, Data = data }, id, isReliable);
            }
        }

        public void SendCommandToPlayersInRange(Vector3D point, string commandString, string message = null, object data = null, bool isReliable = true)
        {
            //(MyMultiplayerBase)MyAPIGateway.Multiplayer
        }

        public void SendCommandToPlayersInRange(Vector3D point, long radius, string commandString, string message = null, object data = null, bool isReliable = true)
        {
            SendCommandToPlayersInRange(new BoundingSphereD(point, radius), commandString, message, data, isReliable);
        }

        public void SendCommandToPlayersInRange(BoundingSphereD sphere, string commandString, string message = null, object data = null, bool isReliable = true)
        {
            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players, (p) => p.Controller?.ControlledEntity?.Entity != null && p.Controller.ControlledEntity.Entity.GetIntersectionWithSphere(ref sphere));

            foreach (IMyPlayer player in players)
            {
                SendCommand(new Command() { SteamId = player.SteamUserId, CommandString = commandString, Message = message, Data = data }, player.SteamUserId, isReliable);
            }
        }

        /// <summary>
        /// A methods for sending client messages
        /// </summary>
        /// <param name="cmd">The object to be sent to the client</param>
        /// <param name="steamId">The players steam id</param>
        private void SendCommand(Command cmd, ulong steamId = ulong.MinValue, bool isReliable = true)
        {
            byte[] data = ((object)cmd) as byte[];

            if (steamId == ulong.MinValue)
            {
                MyAPIGateway.Multiplayer.SendMessageToOthers(ComId, data, isReliable);
            }
            else
            {
                MyAPIGateway.Multiplayer.SendMessageTo(ComId, data, steamId, isReliable);
            }
        }
    }
}
