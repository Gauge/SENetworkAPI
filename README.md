## Overview

The ModNetworkAPI is a neat, robust rapper that sets up and handles data trafficing between the client and server instances of your mod. The module is designed to be entirely plug and play with vary little inital setup cost.

## Example

```cs
private NetworkAPI Network => NetworkAPI.Instance; // readability

public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
{
    // Its recommended to check the initialized state in all classes using NetworkAPI
    if (!NetworkAPI.IsInitialized) 
    {
        NetworkAPI.Init(ComId, ModName, Keyword);
    }
    
    // these commands will be executed on both clients and host client
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
