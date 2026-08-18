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

## `SyncData`

`NetSync.cs`, `internal`. Carried inside `Command.Data` when `IsProperty` is set.

| # | Field | Type | Meaning |
| --- | --- | --- | --- |
| 1 | `Id` | `long` | Property address: an index for entity properties, a generated id for session properties. |
| 2 | `EntityId` | `long` | Owning entity, or `0` for a session-scoped property. |
| 3 | `Data` | `byte[]` | The serialized `T`. Ignored for `Fetch`. |
| 4 | `SyncType` | `SyncType` | `Post`, `Fetch`, `Broadcast` or `None`. |

## Compression

`NetworkAPI.CompressionThreshold` is `100000` bytes. Both send paths do:

```csharp
if (cmd.Data != null && cmd.Data.Length > CompressionThreshold)
{
    cmd.Data = MyCompression.Compress(cmd.Data);
    cmd.IsCompressed = true;
}
```

The threshold is exclusive: a payload of exactly 100000 bytes is sent raw.
Decompression happens once, in `HandleIncomingPacket`, before any dispatch, so
callbacks always see the original bytes.

## Timestamps

`Command.Timestamp` is `DateTime.UtcNow.Ticks` at send time. Two of the four
send paths overwrite whatever the caller passed as `sent`:

| Path | Honours `sent`? |
| --- | --- |
| `Client.SendCommand(...)` | No — overwritten with now |
| `Server.SendCommand(commandString, ...)` | Yes |
| `Server.SendCommand(commandString, point, radius, ...)` | No — overwritten with now |
| `Server.SendCommandTo(ids, ...)` | Yes |

Callbacks receive it as a `DateTime` (`new DateTime(cmd.Timestamp)`, so `Kind`
is `Unspecified` even though the value is UTC). Helpers:

```csharp
float ms     = NetworkAPI.GetDeltaMilliseconds(timestamp.Ticks);
int   frames = NetworkAPI.GetDeltaFrames(timestamp.Ticks);   // ceil(ms / (1000/60))
```

`GetDeltaMilliseconds` divides ticks as integers, so the result is always a
whole number of milliseconds despite the `float` return type.

## Channel

All of the above rides on the single `ushort comId` passed to
`NetworkAPI.Init`. Pick an uncommon number; the channel namespace is shared by
every mod in the world, and a collision means another mod's bytes reach your
handler (where they fail to deserialize and are logged as
`Failure in message processing`).
