using System;

namespace ModNetworkAPI
{
    public enum MultiplayerTypes { Dedicated, Server, Client }

    public interface ICommunicate
    {
        MultiplayerTypes MultiplayerType { get; }

        event Action<Command> OnCommandRecived;
        event Action<string> OnTerminalInput;

        void SendCommand(string arguments, string message = null, ulong steamId = ulong.MinValue);
        void SendCommand(Command cmd, ulong steamId = ulong.MinValue);
        void Close();
    }
}
