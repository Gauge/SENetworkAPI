# Wire protocol

One packet type crosses the wire. It is a protobuf-net contract serialized with
`MyAPIGateway.Utilities.SerializeToBinary`.

## `Command`

`Command.cs`, `internal` — mods never see it directly.

| # | Field | Type | Meaning |
| --- | --- | --- | --- |
| 1 | `SteamId` | `ulong` | Sent by a client: the sender. Sent by a server: the *recipient* (0 = broadcast). See the note below. |
| 2 | `CommandString` | `string` | Command name plus space-delimited arguments. `null` for property packets and pure chat relays. |
| 3 | `Message` | `string` | Text to print in chat on arrival. |
| 4 | `Data` | `byte[]` | Opaque payload. For property packets this is a serialized `SyncData`. |
| 5 | `Timestamp` | `long` | `DateTime.Ticks`, UTC. |
| 6 | `IsProperty` | `bool` | Route to `NetSync.RouteMessage` instead of the command dispatcher. |
| 7 | `IsCompressed` | `bool` | `Data` is GZip/`MyCompression` compressed. |
| 8 | `Property` | `SyncData` | A property update, carried inline. |
| 9 | `Properties` | `List<SyncData>` | Several property updates batched into one packet. |

### The `SteamId` asymmetry

* `Client.SendCommand` always overwrites `SteamId` with the local player's id
  and always sends to the server. The `steamId` argument on the client overload
  is ignored.
* `Server.SendCommand` writes the *destination* id into `SteamId`.
* `NetSync` sets `SteamId` to `MyAPIGateway.Session.LocalHumanPlayer.SteamUserId`
  on both sides, which is **0 on a dedicated server**. Clients therefore see
  `sender == 0` in `ValueChangedByNetwork` for updates originating on a DS.

On the receive side, `SteamId` is always treated as "who sent this" — it is what
gets passed to network command callbacks, `OnCommandRecived`, and
`ValueChangedByNetwork`.

### Property layouts

A property packet sets `IsProperty` and then carries the update in one of three
ways. Receivers understand all three; senders only ever produce the first two.

| Layout | Where the update is | Produced by |
| --- | --- | --- |
| single | `Property` | every property send |
| batched | `Properties` | a coalesced flush with more than one update |
| original | encoded bytes in `Data` | builds predating the inline layout |

The original layout cost an extra encode pass, because `SyncData` was
serialized to bytes and those bytes were then serialized inside `Command`.
Protobuf charges per call, not per payload — a nested message rides along in
the envelope's own pass for free.

## `SyncData`

`NetSync.cs`, `internal`. Carried by `Command.Property` or `Command.Properties`
(and, from older builds, as encoded bytes in `Command.Data`).

| # | Field | Type | Meaning |
| --- | --- | --- | --- |
| 1 | `Id` | `long` | Property address: an index for entity properties, a generated id for session properties. |
| 2 | `EntityId` | `long` | Owning entity, or `0` for a session-scoped property. |
| 3 | `Data` | `byte[]` | The serialized `T`. Ignored for `Fetch`. |
| 4 | `SyncType` | `SyncType` | `Post`, `Fetch`, `Broadcast` or `None`. |

## Compression

`NetworkAPI.CompressionThreshold` is `1024` bytes and is settable at runtime.
Payloads over it are compressed, and the compressed copy is kept only if it
actually came out smaller — random or already-compressed data is sent raw
rather than paying for a decompression at the other end that gains nothing.

The threshold used to be 100000, above both the network MTU and the engine's
unreliable ceiling, so in practice nothing was ever compressed. Because packets
carry an `IsCompressed` flag, the value is self-describing: changing it needs no
agreement between the two ends.
Decompression happens once, in `HandleIncomingPacket`, before any dispatch, so
callbacks always see the original bytes.

## Timestamps

`Command.Timestamp` is `DateTime.UtcNow.Ticks` at send time. Every send path
honours an explicit `sent`; only a command that arrives at the transport without
a timestamp gets stamped, which is how `NetSync` packets (built without one) end
up with the send time.

Callbacks receive it as a `DateTime` (`new DateTime(cmd.Timestamp)`, so `Kind`
is `Unspecified` even though the value is UTC). Helpers:

```csharp
float ms     = NetworkAPI.GetDeltaMilliseconds(timestamp.Ticks);
int   frames = NetworkAPI.GetDeltaFrames(timestamp.Ticks);   // ceil(ms / (1000/60))
```

`GetDeltaMilliseconds` divides ticks as integers, so the result is always a
whole number of milliseconds despite the `float` return type.

## Reliability

`isReliable` (default `true`) is passed straight to the engine, which applies a
hard rule before anything touches the network:

```csharp
if (!reliable && message.Length > 1024) return false;
```

Rather than let that happen, both send paths check the encoded packet and
upgrade an oversized unreliable message to reliable. `isReliable: false` is
therefore a hint, not a way to lose data. Compression runs first, so the limit
applies to the compressed packet.

A packet addressed to the local player is delivered straight back into the local
handlers (`HandleMessageClient`: `if (recipient == Sync.MyId)`), so a listen
server that addresses itself runs its own receive path.

## Trust

None of this is authenticated. SENetworkAPI uses the game's non-secure message
handler, which hands the mod raw bytes and nothing else, so `Command.SteamId` is
whatever the sender chose to write. Never make a permission decision from it —
see [known-issues.md](known-issues.md#sender-identity-is-not-authenticated-engine-verified).

## Channel

All of the above rides on the single `ushort comId` passed to
`NetworkAPI.Init`. Pick an uncommon number; the channel namespace is shared by
every mod in the world, and a collision means another mod's bytes reach your
handler (where they fail to deserialize and are logged as
`Failure in message processing`).
