## Overview

The ModNetworkAPI is a neat, robust rapper for Space Engineers mods. Designed to be plug and play, the ModNetworkAPI has very little setup overhead.

To this in action check out: https://github.com/Gauge/RelativeTopSpeed

## Example

```cs
using ModNetworkAPI;

private NetworkAPI Network => NetworkAPI.Instance; // readability

public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
{
    // Its recommended to check the initialized state in all classes using NetworkAPI
    if (!NetworkAPI.IsInitialized) 
    {
        NetworkAPI.Init(ComId, ModName, Keyword);
    }
    
    // these commands will be executed on both clients and server instances
    Network.RegisterChatCommand(string.Empty, Chat_Help);
    Network.RegisterChatCommand("help", Chat_Help);
    
    if (Network.NetworkType == NetworkTypes.Client)
    {
        Network.RegisterNetworkCommand(null, ClientCallback);
        Network.RegisterChatCommand("update", (arg) => { Network.SendCommand("update"); });
    }
    else
    {
        Network.RegisterNetworkCommand("update", ServerCallback);
    }
}

private void Chat_Help(string arguments)
{
    MyAPIGateway.Utilities.ShowMessage(Network.ModName, "This is a useful help message");
}

private void ServerCallback(ulong steamId, string commandString, byte[] data)
{
    Network.SendCommand(null, data: MyAPIGateway.Utilities.SerializeToBinary(cfg), steamId: steamId);
}

private void ClientCallback(ulong steamId, string commandString, byte[] data)
{
  cfg = MyAPIGateway.Utilities.SerializeFromBinary<Config>(data);
}

```

## Nit and Grit

There are two different types of commands `Network` and `Chat`.
```cs
Network.RegisterNetworkCommand();
Network.RegisterChatCommand();
```

As seen above. The process of sending a command requires a `CommandString` and a `Callback` method
```cs
Network.RegisterNetworkCommand("update", ServerCallback);
```

### Chat Commands

If a `help` chat command is registered
```cs
Network.RegisterChatCommand("help", Chat_Help);
```
Then the event will fire when a client enters the following into chat.
```
<keyword> help
```
All chat commands are delimited with spaces.

### Network Commands

Network commands are much like chat commands. They differ only in that they trigger from network traffic instead of client chat.
```cs
Network.SendCommand("update"); // this will trigger the callback method of the reciever.
```

The `Network.SendCommand()` funciton has a few options
```cs
Network.SendCommand(string command); // CommandString
Network.SendCommand(string command, string message); // A message that will be in clients chat
Network.SendCommand(string command, string message, byte[] data); // serialized object data
Network.SendCommand(string command, string message, byte[] data, ulong steamId); // The receiver, Server only
```
The `Network`when runing on the server instance can be cast for more specialized functions
```
if (Network.NetworkType != NetworkTypes.Client)
{
    Server s = Network as Server;
    
    s.SendCommandTo(ulong[] clients, string command, ...)
    s.SendCommandToPlayersInRange(Vector3D location, float radius, string command, ...)
}
```

