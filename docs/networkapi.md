# NetworkAPI, Client and Server

## Initialization

```csharp
if (!NetworkAPI.IsInitialized)
{
    NetworkAPI.Init(comId, modName, keyword);
}
```

| Argument | Type | Meaning |
| --- | --- | --- |
| `comId` | `ushort` | The communication channel. Shared game-wide — pick an uncommon value. |
| `modName` | `string` | Shown as the sender of any chat message the API prints. `null` becomes `""`. |
| `keyword` | `string` | Optional chat command prefix, e.g. `"/mymod"`. Lower-cased on the way in. `null` disables chat handling entirely. |

`Init` is a no-op once `Instance` is set, so every class in your mod can call it
defensively. Because `Init` reads `MyAPIGateway.Multiplayer.IsServer`, call it
no earlier than your session component's `Init` / your block's `Init` — and call
it **on the game update thread**: it registers a message handler, and the engine
throws `InvalidOperationException` if that happens from another thread.

Useful members:

```csharp
static NetworkAPI Instance;                // the single instance for this mod
static bool       IsInitialized;           // Instance != null
static bool       LogNetworkTraffic;       // verbose logging into the SE log
static int        CompressionThreshold;    // payloads over this are compressed (1024)
const  int        UnreliableMessageLimit;  // the engine's unreliable ceiling (1024)
NetworkTypes      NetworkType;             // Client, Server or Dedicated (see below)
readonly ushort   ComId;
readonly string   ModName;
readonly string   Keyword;                 // null when chat commands are off
```

`NetworkType` reports which side this instance is, and is the safe way to reach
the server-only sends:

```csharp
if (Network.NetworkType != NetworkTypes.Client)
{
    Server s = (Server)Network;
}
```

It is derived from the instance, not from a live "am I the server" check, so it
can never disagree with what the object actually is — a cast guarded this way
cannot throw. Only the `Dedicated`/`Server` split asks the game, since both are
`Server` instances. If you want the live game state instead, read
`MyAPIGateway.Multiplayer.IsServer` directly.

`Close()` and `Dispose()` are obsolete. `SessionTools`, a session component
inside the API, calls `Dispose()` on world unload for you.

## Network commands

A network command is a name plus a callback. Names keep the spelling you
register them with, match case-insensitively, and must be unique — registering
the same name twice, in any casing, throws.

```csharp
Network.RegisterNetworkCommand("update", OnUpdate);
Network.UnregisterNetworkCommand("update");

private void OnUpdate(ulong steamId, string commandString, byte[] data, DateTime sent)
{
    Config cfg = MyAPIGateway.Utilities.SerializeFromBinary<Config>(data);
}
```

* `steamId` — who sent it (see the [`SteamId` asymmetry](protocol.md#the-steamid-asymmetry)).
  It is **not verified by the engine** — the sender wrote it. Do not use it for
  permission checks; see [known-issues.md](known-issues.md#sender-identity-is-not-authenticated-engine-verified).
* `commandString` — the **whole** string as sent, arguments included. Dispatch
  matches only the first space-delimited word.
* `data` — the payload, already decompressed.
* `sent` — the send timestamp.

Two things to know:

* **`RegisterNetworkCommand(null, cb)` throws.** `null` command strings are
  reserved for pure chat messages, and there is no way to register a handler for
  them. A packet whose `CommandString` is `null` never reaches a callback, even
  if it carries data.
* **Names are case insensitive**, on registration, dispatch and unregistration
  alike. Registering `"Update"` twice in different casing throws; sending
  `"UPDATE"` reaches the handler registered as `"update"`.

A callback that throws is caught and logged, and the rest of the packet is
processed normally.

For traffic-level visibility there is also an event that fires for every
non-property packet carrying a command string, registered or not:

```csharp
Network.OnCommandRecived += (steamId, command, data, sent) => { ... };
```

## Chat commands

Only available when a `keyword` was supplied.

```csharp
Network.RegisterChatCommand("help", Chat_Help);   // "<keyword> help ..."
Network.RegisterChatCommand("", Chat_Help);       // bare "<keyword>"
Network.UnregisterChatCommand("help");

private void Chat_Help(string arguments) { ... }
```

Parsing of `"<keyword> <command> <arguments...>"`:

* Matching is case-insensitive and ordinal (so it does not depend on the
  player's locale), on whole space-delimited words. `/mymod` does not trigger on
  `/mymodding`. A keyword containing a space can never match.
* Any message starting with the keyword is swallowed from global chat
  (`sendToOthers = false`), including unrecognised ones.
* `arguments` keeps its original casing and inner spacing; only the outer
  padding is trimmed.
* A bare keyword (with or without trailing spaces) dispatches to the `""`
  command.
* An unrecognised command prints `"Command not recognized."` — unless the
  instance is a dedicated server, which stays silent.
* `null` passed as the command name registers the `""` command.

A chat callback that throws is caught and logged rather than escaping into the
game's `MessageEntered` event, which every other mod is also subscribed to.

Chat commands run on the machine that typed them. To reach the server, send a
network command from the chat callback:

```csharp
Network.RegisterChatCommand("update", args => Network.SendCommand("update", args));
```

## Sending

`NetworkAPI` declares two public send overloads; `Client` and `Server` implement
them differently.

```csharp
void SendCommand(string commandString, string message = null, byte[] data = null,
                 DateTime? sent = null, ulong steamId = 0, bool isReliable = true);

void SendCommand(string commandString, Vector3D point, double radius = 0,
                 string message = null, byte[] data = null,
                 DateTime? sent = null, ulong steamId = 0, bool isReliable = true);

void Say(string message);   // SendCommand(null, message)
```

Note the parameter order: `data` comes before `sent` and `steamId`, so pass them
by name.

`isReliable: false` is a hint, not a risk: the engine refuses unreliable
messages over `UnreliableMessageLimit` bytes, so both send paths check the
encoded packet and upgrade an oversized one to reliable rather than letting it
disappear. Compression runs first, so the limit applies to the compressed size.

### As a client

Everything goes to the server; there is no client-to-client path.

* `steamId` is ignored, and `Command.SteamId` is set to the local player.
* `point` / `radius` are ignored — the positional overload just forwards.
* With no `MyAPIGateway.Session.Player` (i.e. no session) nothing is sent and a
  warning is logged.
* `Say` does **not** echo locally; the text appears once the server relays it
  back.

### As a server

```csharp
Server s = NetworkAPI.Instance as Server;

s.SendCommand("update");                            // everyone
s.SendCommand("update", steamId: playerId);         // one player
s.SendCommandTo(new[] { id1, id2 }, "update");      // several players
s.SendCommand("boom", position, 500);               // everyone within 500m
s.SendCommand("boom", position);                    // everyone within sync distance
```

* `steamId == 0` broadcasts (`SendMessageToOthers`), anything else is a directed
  send (`SendMessageTo`).
* A non-empty `message` is also printed locally on the server before sending.
* For the positional overload: `radius == 0` falls back to
  `MyAPIGateway.Session.SessionSettings.SyncDistance`; the test is
  `distance² < radius²`, so the boundary is exclusive; the player identified by
  `Command.SteamId` is excluded (that is how an update avoids echoing to its
  originator); and passing an explicit `steamId` ignores distance entirely.
* The player list behind the range test is snapshotted once per game frame, so
  several sends in the same frame share one query. A player who joins mid-frame
  is picked up on the next one; a send addressed at a specific `steamId` skips
  the snapshot and queries the engine directly, so it never misses them.
* On a listen server the host's own player is in `MyAPIGateway.Players`, so a
  positional send addresses a packet to the host as well — and the engine
  delivers it, so the host runs its own receive path. With a `message` attached
  that amplifies into three chat lines and an extra broadcast; see
  [known-issues.md](known-issues.md#a-positional-send-with-a-message-is-amplified-engine-verified).

## Receiving

`HandleIncomingPacket` is the single receive path and is wrapped in a
`try/catch`, so a malformed packet or another mod's traffic on the same channel
is swallowed and written to the log as `Failure in message processing`. Your
callbacks are additionally isolated from each other: one that throws is logged
and the rest of the packet is processed anyway.

For a non-property packet it, in order:

1. prints `Message` in chat (skipped on a dedicated server);
2. if this is a server, relays `Message` on to every client — which also prints
   it locally again, so a listen server host sees the line twice;
3. raises `OnCommandRecived` when `CommandString != null`;
4. invokes the callback registered for the first word of `CommandString`.
