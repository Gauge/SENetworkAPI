# Known issues and sharp edges

Everything below is **current behaviour**, pinned by a test in
`tests/SENetworkAPI.Tests` so it cannot change silently. Nothing here has been
"fixed" — the API is in use by a lot of mods and its behaviour, warts included,
is the contract. Each entry names the test that documents it.

Findings marked **[engine-verified]** were checked against the shipped game
assemblies (`Bin64`), not just against the test harness — decompiled from
`Sandbox.Game.dll` / `VRage.Game.dll`. Where the engine's own code is the reason
for the behaviour, it is quoted.

---

## Security

### Sender identity is not authenticated **[engine-verified]**

`NetworkAPI` registers the game's **non-secure** message handler:

```csharp
MyAPIGateway.Multiplayer.RegisterMessageHandler(ComId, HandleIncomingPacket);
```

In `MyMultiplayerBase` that handler is invoked with nothing but the payload:

```csharp
private static void HandleMessage(ushort id, byte[] message)
{
    foreach (Action<byte[]> item in m_registeredListeners[id]) item(message);   // no sender
    ...
}
```

Every "who sent this" in the API — the `steamId` given to network command
callbacks, to `OnCommandRecived`, and to `ValueChangedByNetwork` — is read out
of `Command.SteamId`, a field the sender wrote. A modified client can put any
steam id there. **Do not gate admin actions or ownership checks on it.**

The same handler carries no "this came from the server" flag, so a client
applies any property packet that reaches it, and the server applies any property
packet a client sends — including for a `ServerToClient` property, because the
transfer type is only checked on the sending machine, never on arrival. The
server then re-broadcasts the value to everyone.

The engine offers exactly what is missing here:

```csharp
RegisterSecureMessageHandler(ushort id, Action<ushort, byte[], ulong, bool> handler);
//                                                          ^ verified sender  ^ from server
```

`HandleMessage` fills those from the transport (`Sync.Clients.TryGetClient(...)`
and `value3 == Sync.ServerId`), so neither can be forged. Migrating would be a
breaking change to the receive path, which is why this is documented rather than
patched.

*Tests: `SenderIdentityTests` (all five).*

### The non-secure handler pair is obsolete **[engine-verified]**

`RegisterMessageHandler` / `UnregisterMessageHandler` are marked
`[Obsolete("Use RegisterSecureMessageHandler && UnregisterSecureMessageHandler
pair instead")]` in the current game. Compiling the sources against the real
assemblies emits CS0618 for both — see `tests/GameContractCheck`. They still
work; they are the reason the finding above exists.

---

## Data loss

### Unreliable messages over 1024 bytes are silently discarded **[engine-verified]**

Every send in `MyMultiplayerBase` starts with:

```csharp
if (!reliable && message.Length > 1024) return false;
```

`SendCommand(..., isReliable: false)` with a larger packet therefore vanishes:
nothing reaches the network, nothing is logged, and the `bool` the engine
returns is discarded by both `Client.SendCommand` and `Server.SendCommand`.

The compression threshold does not line up with this limit. Compression runs
first, so what matters is the *compressed* size: a 100KB block of zeroes squeezes
under 1024 bytes and gets through, while any incompressible payload over ~1KB —
including everything between 1KB and the 100000-byte compression threshold —
is dropped. Use `isReliable: true` (the default) for anything but tiny packets.

*Tests: `ServerSendTests.UnreliableMessagesOverTheEngineLimitAreSilentlyDropped`,
`ServerSendTests.WhetherAnUnreliableSendSurvivesDependsOnHowWellItCompresses`.*

---

## Bugs

### A push aimed at one player becomes a broadcast **[engine-verified]**

The engine delivers a packet addressed to the local player straight back into
the local handlers:

```csharp
private static void HandleMessageClient(ushort id, byte[] message, ulong recipient)
{
    if (recipient == Sync.MyId) HandleMessage(id, message);
}
```

So on a listen server, `property.Push(hostSteamId)` sends the packet, receives
its own copy, and — because `SetNetworkValue` re-broadcasts anything a server
receives — fans the value out to every client. `SendValue` does notice the
self-addressing and logs
`The sender id is the same as the recievers id. data will not be sent.`, but the
message is wrong: it logs and then sends anyway.

*Test: `NetSyncValueTests.SendingToYourself_IsLoggedAsAnErrorButStillTransmitted`.*

### A positional send with a message is amplified **[engine-verified]**

`Server.SendCommand(command, point, radius, message: "...")` on a listen server:

1. echoes the message locally before sending;
2. addresses a packet to every player in range — including the host, because the
   range query only excludes `Command.SteamId`, which is 0 for this overload;
3. the host receives its own packet, shows the message a second time, and being
   a server relays it to everyone, which echoes it a third time.

Net effect: three lines on the host, two copies on every client. `NetSync`'s own
positional sends are safe — they set `Command.SteamId` to the local player, so
the host filters itself out.

*Tests: `ServerSendTests.RadiusSend_WithAMessage_AmplifiesItOnAListenServer`,
`ServerSendTests.RadiusSend_WithoutAMessage_DoesNotAmplify`.*

### Relayed chat is shown twice on a listen server

Independently of the above: the receive path prints an incoming message, then
the server's relay through `Server.SendCommand` prints it again.

*Test: `IncomingPacketTests.MessageOnlyPacket_OnAListenServer_IsShownTwice`.*

---

## Design constraints worth knowing

### NetSync is not thread safe

`NetSync` used to wrap its assignment in `lock (_value)`, which protected
nothing — it boxed a fresh object on every assignment of a value type, and the
getter was never locked — so it is gone. Nothing replaced it: the static
registries are locked, individual values are not.

The engine refuses handler registration off the update thread
(`"Modifying message handlers from another thread is not supported!"`) while
saying nothing about sends. Treat the whole API as main-thread-only.
**[engine-verified]**

### Entity properties are addressed by declaration order

There is no name or hash in the packet, just an index. Client and server must
run the same build and construct properties in the same order, or updates land
on the wrong property with no error.

*Tests: `IntegrationTests.AnEntityPropertyUpdate_LandsOnTheMatchingPropertyOfTheMatchingEntity`,
`IntegrationTests.AMismatchedDeclarationOrder_MisroutesTheUpdate`.*

### `null` command strings are unreachable

`RegisterNetworkCommand(null, cb)` throws, and a packet whose `CommandString` is
`null` never reaches a callback or `OnCommandRecived` — even when it carries
data. Use a real command name for data replies.

*Test: `IncomingPacketTests.APacketWithNoCommandString_DeliversNothingEvenWhenItCarriesData`.*

### `Init` must run on the game update thread **[engine-verified]**

`RegisterMessageHandler` throws
`InvalidOperationException("Modifying message handlers from another thread is not supported!")`
when called from anywhere but `MySandboxGame.Static.UpdateThread`. Since the
`NetworkAPI` constructor registers the handler, `NetworkAPI.Init` inherits that
constraint — do not call it from a background task.

### `GetDeltaMilliseconds` has whole-millisecond resolution

The tick subtraction is integer division, so the `float` result never has a
fractional part.

*Test: `TimingTests.GetDeltaMilliseconds_HasWholeMillisecondResolution`.*

### The `NetworkTypes` enum is dead

`enum NetworkTypes { Dedicated, Server, Client }` is declared and never used;
there is no `NetworkType` property to compare it against (older README examples
show one). Branch on `NetworkAPI.Instance is Server` or
`MyAPIGateway.Multiplayer.IsServer` instead.

---

## Fixed

These were on this list and are not any more. They are recorded because mods
written against the old behaviour may contain workarounds that can now go.

| Was | Now |
| --- | --- |
| `new NetSync<string>(this, TransferType.Both)` threw `ArgumentNullException` on first assignment | Works. A null value still cannot be *transmitted*, but it can be assigned, and a fetch no longer needs one |
| Sends of `"Update"` never reached a handler registered as `"update"` | Command lookup is case insensitive, and locale independent |
| `UnregisterNetworkCommand("Update")` was a silent no-op after `RegisterNetworkCommand("Update")` | Unregister matches Register |
| Closing an entity leaked it and its properties, and could evict a session property with a colliding id | The entity's entry is removed and its events unhooked; session properties are untouched |
| Leaving a world kept every property from it alive for the rest of the process | `Dispose` clears the registries |
| A mod callback that threw abandoned the rest of the packet, and a throwing chat command broke chat for every mod behind it | Callbacks run isolated; the failure is logged and everything else continues |
| A server could not answer a fetch for a `ClientToServer` property | The direction check is bracketed the way it reads |
| `sent` was discarded by the client and by positional server sends | Honoured; only commands without a timestamp get stamped |
| A fetch shipped a copy of the value the receiver discarded | It sends the address only |
| The verbose log labelled old and new values the wrong way round | Fixed |

---

## Checked and cleared

Things that looked suspicious and turned out to be fine, recorded so nobody
re-investigates them:

* **`NetSync<int>`, `NetSync<float>`, `NetSync<string>` really do serialize.**
  `MyAPIGateway.Utilities.SerializeToBinary` is a bare
  `ProtoBuf.Serializer.Serialize(stream, obj)` with no contract wrapper, which
  looks like it should reject bare primitives. The protobuf fork the game ships
  (`ProtoBuf.Net.Core`) handles root-level values through its auxiliary-type
  path: an `int` is 2 bytes, a `float` 5, `"hello"` 7 — identical to the stock
  package the tests use. **[engine-verified]**
* **`ShowMessage` on a dedicated server is harmless.** It resolves to
  `MyHud.Chat.ShowMessage`, which appends to a bounded in-memory queue; `MyHud`
  is registered on dedicated servers too, so there is nothing to null-reference.
  The call is pointless there, not dangerous. **[engine-verified]**
* **Serializing `null` does not throw.** protobuf's `Serialize` is a no-op for a
  null instance, so it yields an empty array. `NetSync` guards against null
  values before it gets that far anyway. **[engine-verified]**
