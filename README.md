# SENetworkAPI

A small, robust wrapper around Space Engineers network transactions, built to
streamline multiplayer mod development.

It gives you two things:

* **`NetSync<T>`** — a variable that keeps itself in step across the network.
* **Commands** — named messages with callbacks, drivable from code or from chat.

## Installing in a mod

Copy the entire [`SENetworkAPI/`](SENetworkAPI/) folder into
`<YourMod>/Data/Scripts/<YourModName>/`. It contains only the seven runtime `.cs`
files and their license. Include all seven files, including `CompactBatch.cs`
even when compact batches are disabled, and retain `license.txt`.

When upgrading, replace your previous API source files to avoid duplicate class
definitions. Pick a unique communication channel for your mod.

The `tests/` and `TestFiles/` folders are development tools and test scenarios;
they are not part of the API installation. Copy only `SENetworkAPI/`, rather than
the repository, into your mod's scripts directory.

Remaining performance work and validation tasks are tracked in
[`TASKS.md`](TASKS.md). Benchmark instructions and results are in
[`tests/Benchmarks/README.md`](tests/Benchmarks/README.md).

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

A null value cannot be transmitted, so give reference types a starting value if
they should sync before anything sets them — `new NetSync<string>(this,
TransferType.Both, string.Empty)`.

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

Three opt-in switches, chainable at the declaration:

```csharp
// batch this frame's changes into one packet with the block's other properties
health = new NetSync<float>(this, TransferType.ServerToClient, 100f).Coalesce();

// send on the unreliable channel when it fits - for values overwritten constantly
aim = new NetSync<Vector3D>(this, TransferType.Both).Lossy();

// send on every assignment, even when the value did not change
beat = new NetSync<int>(this, TransferType.Both).AlwaysSend();
```

By default an assignment that does not change the value sends nothing and does
not raise `ValueChanged`; reference types are always sent, since their contents
can change behind the reference. `AlwaysSend()` restores the old behaviour.

Coalescing applies to server relays as well as local assignments. Callbacks run
immediately; the latest value is sent on the next flush. Client properties can
share a packet across entities. Distance-limited server updates remain grouped
by entity so each update reaches the appropriate players.

Batches are limited to 500 values and a byte budget: 16 KiB for reliable traffic
and 1,024 bytes for lossy traffic. A single value larger than its budget is sent
intact, with reliable delivery when needed. Large individual property values
are compressed when this saves space, using the existing legacy property layout.
Multi-property packets use their existing layout by default. `CompressionThreshold`
controls compression attempts; small values stay inline.

Distance-limited sends select recipients before serializing values. If nobody
is in range, the value remains local and no serialization is performed.
Range queries adapt to player count, query volume, and how many players are
nearby. Selective searches can use a spatial index; crowded areas can use a
bounding-box check, with direct scans for short bursts and small populations.
The tuning targets typical 10–40-player servers and imposes no player or recipient
cap. All paths use the existing per-frame player snapshot and evaluate each
query's current position, radius, and sender exclusion.

To enable smaller batches when every receiver has the updated library (including
`CompactBatch.cs`):

```csharp
NetworkAPI.UseCompactBatches = true;
```

Compact batches share repeated entity IDs, pack per-value metadata, and compress
the entire batch when that saves bytes. The original value serialization stays
unchanged. Small batches below `CompactBatchThreshold` (256 bytes by default)
keep the existing layout; the compact layout is retained only when it is smaller.
`CompressionThreshold` still controls the compression step. Immediate sends retain
their timing, and `.Coalesce()` retains its existing flush schedule.

`UseCompactBatches` defaults to `false` for older-peer compatibility. Updated
receivers always accept both layouts, regardless of their own sending setting.
There is no automatic capability negotiation; enable compact sending only once
all intended receivers support it.

Benchmark commands, results, and further payload options are in
[tests/Benchmarks/README.md](tests/Benchmarks/README.md).

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

Command names are case insensitive. Never register `null`, which is reserved
for chat relays.

On the server the instance can be cast for the server-only sends:

```csharp
if (Network.NetworkType != NetworkTypes.Client)
{
    Server s = (Server)Network;

    s.SendCommandTo(new[] { id1, id2 }, "update");
    s.SendCommand("update", location, radius);   // everyone within radius
}
```

The server uses the sender ID supplied by the secure transport, ignoring the ID
claimed in a client packet. Clients accept packets only when the transport marks
them as coming from the server; their callbacks retain the server-provided
envelope ID. Property transfer directions are enforced on receive as well as
send. Only the server answers fetch requests. Mods still decide which commands
and `Both` properties each player is allowed to use.

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
