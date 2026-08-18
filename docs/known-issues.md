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

### Assigning to a null-valued property throws

`SetValue` does `lock (_value)` on the value it is about to replace. If that
value is `null`, `Monitor.Enter` throws `ArgumentNullException`. This makes the
most natural declaration of a string property blow up on first use:

```csharp
NetSync<string> name = new NetSync<string>(this, TransferType.Both);  // starts null
name.Value = "hello";                                                 // throws
```

Workaround: always pass a non-null starting value. Related: a `null` value is
never transmitted, so such a property cannot send its sync-on-load fetch either.

*Tests: `NetSyncValueTests.SetValue_OnANullValuedProperty_Throws`,
`NetSyncValueTests.ANullValuedProperty_CannotEvenFetch`.*

### `lock (_value)` does not synchronise anything

Even with a non-null value the lock is ineffective: for a value type `T` the
expression boxes into a **new** object every call, so two threads never share a
monitor. `oldval` is also read outside the lock.

This matters more than it looks, because the engine refuses handler
registration off the update thread —
`"Modifying message handlers from another thread is not supported!"` — while
saying nothing about sends. Treat `NetSync` as main-thread-only. **[engine-verified]**

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

### Closing an entity leaks, and can evict the wrong property

`Entity_OnClose` runs `PropertyById.Remove(Id)` — but entity-scoped properties
live in `PropertiesByEntity`, never in `PropertyById`. Two consequences:

1. The entity and its property list stay in `PropertiesByEntity` for the rest of
   the session: a leak that grows with grid churn.
2. `Id` for an entity property is a small per-entity index (0, 1, 2 …), while
   session property ids come from a counter that also starts at 1. Closing an
   entity that owns two properties therefore removes whatever session property
   holds id 1 — after which that property stops receiving updates.

The `AddedToScene` subscription is likewise left behind if the entity is closed
before it ever enters the scene.

*Tests: `NetSyncConstructionTests.ClosingAnEntity_UnregistersTheMatchingPropertyId`,
`NetSyncConstructionTests.ClosingAnEntity_CanEvictAnUnrelatedSessionProperty`.*

### A callback that throws stops the rest of the packet

`HandleIncomingPacket` wraps everything in one `try/catch`. If a mod callback
throws, the exception is logged as `Failure in message processing` and the
remaining work for that packet is abandoned. Keep callbacks defensive.

*Test: `IncomingPacketTests.ThrowingCallback_IsContainedByTheReceiveHandler`.*

### Command dispatch is case-sensitive, registration is not

`RegisterNetworkCommand` lower-cases the key. The lookup in
`HandleIncomingPacket` does not lower-case the incoming string. Sending
`"Update"` silently reaches nobody.

`UnregisterNetworkCommand` and `UnregisterChatCommand` also skip the
lower-casing, so unregistering by the same mixed-case string you registered with
does nothing.

*Tests: `IncomingPacketTests.CommandPacket_LookupIsCaseSensitive_SoMixedCaseSendsNeverMatch`,
`CommandRegistrationTests.UnregisterNetworkCommand_DoesNotLowercase_SoMixedCaseUnregisterIsANoOp`.*

### Clients discard the `sent` timestamp

`Client.SendCommand(commandString, ..., sent: someTime)` builds the command with
that timestamp and then the internal sender overwrites it with
`DateTime.UtcNow.Ticks`. The server's positional overload does the same. The
non-positional server overload and `SendCommandTo` honour it.

*Test: `ClientSendTests.SendCommand_IgnoresTheSuppliedTimestamp`.*

### Operator precedence in the transfer direction guard

```csharp
if (syncType != SyncType.Fetch &&
    (TransferType == TransferType.ServerToClient && !IsServer) ||
    (TransferType == TransferType.ClientToServer && IsServer))
```

`&&` binds tighter than `||`, so the `syncType != Fetch` exemption applies only
to the `ServerToClient` branch. A server cannot answer a fetch for a
`ClientToServer` property. Harmless today — servers never originate a fetch, and
that direction is client-authoritative anyway — but it is not what the
formatting suggests.

*Test: `NetSyncValueTests.AServerCannotAnswerAFetchForAClientToServerProperty`.*

### Swapped labels in the verbose log

`SetNetworkValue` logs `New value: {oldval} --- Old value: {_value}`. The values
are the right way round, the labels are not. Only visible with
`LogNetworkTraffic` on.

---

## Design constraints worth knowing

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

### A fetch request carries a redundant payload

`SendValue(SyncType.Fetch)` serializes and ships the requester's current value,
which the receiver ignores. For a large `T` this doubles the cost of the
handshake.

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
