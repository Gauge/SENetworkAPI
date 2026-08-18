# SENetworkAPI

A small, robust wrapper around Space Engineers network transactions, built to
streamline multiplayer mod development.

It gives you two things:

* **`NetSync<T>`** — a variable that keeps itself in step across the network.
* **Commands** — named messages with callbacks, drivable from code or from chat.

Drop the `.cs` files into your mod, pick a communication channel, and go.

📚 **[Full documentation](docs/README.md)** · 🧪 `dotnet test tests/SENetworkAPI.Tests`

> **Two things to know before you build on this.** The steam id you are handed
> as "the sender" is not verified by the game — a modified client can put any id
> there, so never gate permissions on it. And `isReliable: false` silently drops
> anything over 1024 bytes. Details in
> [known-issues.md](docs/known-issues.md).

## Syncing a variable

```csharp
using SENetworkAPI;

[MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), true, "ANewModBlock")]
public class ANewModBlock : MyGameLogicComponent
{
    NetSync<bool> isActive;

    public override void Init(MyObjectBuilder_EntityBase objectBuilder)
    {
        ushort comChannel = 1234;          // the mod communication channel
        string modName = "Hello World";    // shown as the sender of chat messages
        string keyword = "/hello";         // prefix for chat commands

        // Check the initialized state everywhere you use the API.
        if (!NetworkAPI.IsInitialized)
        {
            NetworkAPI.Init(comChannel, modName, keyword);
        }

        isActive = new NetSync<bool>(this, TransferType.Both, false);
    }

    public override void UpdateOnceBeforeFrame()
    {
        isActive.Value = true;   // syncs the new value across the network
    }
}
```

Give reference types a non-null starting value — `new NetSync<string>(this,
TransferType.Both, string.Empty)` — a null default cannot be assigned to or
synced. See [known-issues.md](docs/known-issues.md).

`NetSync` cannot see changes made *inside* a complex object, so push manually:

```csharp
isActive.Value.InnerVariable = "hi";
isActive.Push();
```

Set without syncing, or with a specific sync type:

```csharp
config.SetValue(value);                        // local only
config.SetValue(value, SyncType.Broadcast);    // local + send
```

React to changes:

```csharp
config.ValueChanged          += (oldVal, newVal) => { };          // any change
config.ValueChangedByNetwork += (oldVal, newVal, sender) => { };  // remote changes only

void Fetch();               // ask the server for the current value
void Push();                // send the current value now
void Push(ulong steamId);   // send it to one player
Action<ulong> BeforeFetchRequestResponse;   // last chance to refresh before replying
```

Details: [docs/netsync.md](docs/netsync.md).

## Commands

Two flavours, registered the same way:

```csharp
Network.RegisterNetworkCommand("update", ServerCallback);   // fired by network traffic
Network.RegisterChatCommand("help", Chat_Help);             // fired by "<keyword> help"
```

Chat commands are space-delimited; the callback receives everything after the
command word. Sending a network command triggers the matching callback on the
receiver:

```csharp
Network.SendCommand("update");
Network.SendCommand("update", "text shown in chat");
Network.SendCommand("update", data: MyAPIGateway.Utilities.SerializeToBinary(config));
Network.SendCommand("update", data: bytes, steamId: playerId);   // server only
```

Use lower-case command names — the receive-side lookup is case-sensitive — and
never `null`, which is reserved for chat relays and cannot be registered.

On the server the instance can be cast for the server-only sends:

```csharp
if (NetworkAPI.Instance is Server)
{
    Server s = (Server)NetworkAPI.Instance;

    s.SendCommandTo(new[] { id1, id2 }, "update");
    s.SendCommand("update", location, radius);   // everyone within radius
}
```

Details: [docs/networkapi.md](docs/networkapi.md).

## Example session component

```csharp
using SENetworkAPI;

[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class ANewSessionMod : MySessionComponentBase
{
    private NetworkAPI Network => NetworkAPI.Instance;

    public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
    {
        if (!NetworkAPI.IsInitialized)
        {
            NetworkAPI.Init(ComId, ModName, Keyword);
        }

        // Registered on both clients and servers.
        Network.RegisterChatCommand(string.Empty, Chat_Help);
        Network.RegisterChatCommand("help", Chat_Help);

        if (!MyAPIGateway.Multiplayer.IsServer)
        {
            Network.RegisterNetworkCommand("config", ClientCallback);
            Network.RegisterChatCommand("update", arg => Network.SendCommand("update"));
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

    private void ServerCallback(ulong steamId, string commandString, byte[] data, DateTime sent)
    {
        Network.SendCommand("config", data: MyAPIGateway.Utilities.SerializeToBinary(cfg), steamId: steamId);
    }

    private void ClientCallback(ulong steamId, string commandString, byte[] data, DateTime sent)
    {
        cfg = MyAPIGateway.Utilities.SerializeFromBinary<Config>(data);
    }
}
```

## License

See [license.txt](license.txt).
