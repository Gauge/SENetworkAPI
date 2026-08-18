# Known issues and sharp edges

Everything below is **current behaviour**, pinned by a test in
`tests/SENetworkAPI.Tests` so that it cannot change silently. Nothing here has
been "fixed" — the API is in use by a lot of mods and its behaviour, warts
included, is the contract. Each entry names the test that documents it.

## Bugs

### Assigning to a null-valued property throws

`SetValue` does `lock (_value)` on the value it is about to replace. If that
value is `null`, `Monitor.Enter` throws `ArgumentNullException`. This makes the
most natural declaration of a string property blow up on first use:

```csharp
NetSync<string> name = new NetSync<string>(this, TransferType.Both);  // starts null
name.Value = "hello";                                                 // throws
```

Workaround: always pass a non-null starting value.
Related: a `null` value is never transmitted, so such a property cannot send its
sync-on-load fetch either.

*Tests: `NetSyncValueTests.SetValue_OnANullValuedProperty_Throws`,
`NetSyncValueTests.ANullValuedProperty_CannotEvenFetch`.*

### `lock (_value)` does not synchronise anything

Even with a non-null value the lock is ineffective: for a value type `T` the
expression boxes into a **new** object on every call, so two threads never share
a monitor. `oldval` is also read outside the lock. Treat `NetSync` as
main-thread-only.

### Closing an entity leaks, and can evict the wrong property

`Entity_OnClose` runs `PropertyById.Remove(Id)` — but entity-scoped properties
live in `PropertiesByEntity`, never in `PropertyById`. Two consequences:

1. The entity and its property list stay in `PropertiesByEntity` for the rest of
   the session: a leak that grows with grid churn.
2. `Id` for an entity property is a small per-entity index (0, 1, 2 …), while
   session property ids come from a counter that also starts at 1. Closing an
   entity that owns two properties therefore removes whatever session property
   happens to hold id 1 — after which that property stops receiving updates.

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

### A listen server host sees relayed chat twice

The receive path prints the message, then the server relay through
`Server.SendCommand` prints it again because the message is echoed locally
before sending.

*Test: `IncomingPacketTests.MessageOnlyPacket_OnAListenServer_IsShownTwice`.*

### Clients discard the `sent` timestamp

`Client.SendCommand(commandString, ..., sent: someTime)` builds the command with
that timestamp and then the internal sender overwrites it with
`DateTime.UtcNow.Ticks`. The server's positional overload does the same. The
non-positional server overload and `SendCommandTo` honour it.

*Test: `ClientSendTests.SendCommand_IgnoresTheSuppliedTimestamp`.*

### The self-send check logs but does not stop

`SendValue` notices when the sender id equals the recipient id, logs
`The sender id is the same as the recievers id. data will not be sent.` — and
then sends anyway. The message is wrong; the packet goes out.

*Test: `NetSyncValueTests.SendingToYourself_IsLoggedAsAnErrorButStillTransmitted`.*

### Operator precedence in the transfer direction guard

```csharp
if (syncType != SyncType.Fetch &&
    (TransferType == TransferType.ServerToClient && !IsServer) ||
    (TransferType == TransferType.ClientToServer && IsServer))
```

`&&` binds tighter than `||`, so the `syncType != Fetch` exemption applies only
to the `ServerToClient` branch. A server cannot answer a fetch for a
`ClientToServer` property. Harmless today — servers never originate a fetch, and
that transfer direction is client-authoritative anyway — but it is not what the
formatting suggests.

*Test: `NetSyncValueTests.AServerCannotAnswerAFetchForAClientToServerProperty`.*

### Swapped labels in the verbose log

`SetNetworkValue` logs `New value: {oldval} --- Old value: {_value}`. The values
are the right way round, the labels are not. Only visible with
`LogNetworkTraffic` on.

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
