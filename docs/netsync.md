# NetSync&lt;T&gt;

`NetSync<T>` is a variable that keeps itself in step across the network. You
declare it once, assign to it, and the new value shows up on the other side.

```csharp
NetSync<bool> isActive;

public override void Init(MyObjectBuilder_EntityBase objectBuilder)
{
    if (!NetworkAPI.IsInitialized) NetworkAPI.Init(ComId, ModName, Keyword);

    isActive = new NetSync<bool>(this, TransferType.Both, false);
}

// later
isActive.Value = true;      // serialized and sent
```

## Declaring one

There are four constructors, differing only in what the property is attached to:

```csharp
new NetSync<T>(IMyEntity entity,            TransferType, T startingValue = default,
               bool syncOnLoad = true, bool limitToSyncDistance = true)
new NetSync<T>(MyEntity entity,             TransferType, ...)
new NetSync<T>(MyGameLogicComponent logic,  TransferType, ...)   // uses logic.Entity
new NetSync<T>(MySessionComponentBase logic, TransferType, ...)  // session-scoped
```

Passing `null` (or a game logic component with no `Entity`) throws.

| Parameter | Default | Effect |
| --- | --- | --- |
| `transferType` | — | Which direction updates are allowed to travel. See below. |
| `startingValue` | `default(T)` | Local initial value. A null reference type is fine to assign to and to fetch, but cannot be transmitted — see [null values](#null-values). |
| `syncOnLoad` | `true` | Ask the server for the current value as soon as this side is ready. |
| `limitToSyncDistance` | `true` | Entity properties only: send updates just to players near the entity. |

`T` must be serializable by `MyAPIGateway.Utilities.SerializeToBinary` — a
primitive, a `string`, or a type carrying protobuf-net contract attributes.

## Addressing

A property is addressed by a pair: `(EntityId, Id)`.

**Entity-scoped** properties get `EntityId` from the entity, and `Id` is
**the index of their declaration order on that entity** — the first `NetSync`
created for an entity is 0, the second is 1, and so on.

**Session-scoped** properties (the `MySessionComponentBase` constructor) use
`EntityId = 0` and take `Id` from a global counter that starts at 1 and
increments per property constructed.

Both schemes mean **client and server must be running the same build of the
mod, and must construct their properties in the same order**. Adding a property
in the middle of a block's `Init` shifts every later index; an update then lands
on the wrong property with no error at all. A packet whose index is past the end
of the list is dropped with a log line (`property index out of range`).

## Reading and writing

```csharp
T   value = property.Value;          // plain read
property.Value = newValue;           // set + Broadcast
property.SetValue(newValue);         // set, do NOT send (SyncType.None)
property.SetValue(newValue, SyncType.Broadcast);
property.Push();                     // send the current value to everyone
property.Push(steamId);              // send the current value to one player
property.Fetch();                    // ask the server for the current value
```

`Value`'s setter is exactly `SetValue(value, SyncType.Broadcast)`. `SetValue`
defaults to *not* syncing, which is the subtle difference between the two.

`NetSync` has no idea when the innards of a complex value change, so mutate and
then push:

```csharp
property.Value.SomeField = "hi";
property.Push();
```

### Tuning a property

Three opt-in switches, chainable so they read well at the declaration site:

```csharp
health   = new NetSync<float>(this, TransferType.ServerToClient, 100f).Coalesce();
aimPoint = new NetSync<Vector3D>(this, TransferType.Both).Lossy();
heartbeat = new NetSync<int>(this, TransferType.Both).AlwaysSend();
```

| | What it does | When to use it |
| --- | --- | --- |
| `Coalesce()` | Batches this property's updates with every other coalesced property that changes in the same frame, into one packet. Costs one frame of latency. | Blocks whose properties change together |
| `Lossy()` | Sends updates on the unreliable channel when they fit; falls back to reliable when they do not. Fetches stay reliable. | Values overwritten constantly, where a dropped update is superseded anyway |
| `AlwaysSend()` | Restores sending on every assignment, even when the value is unchanged | A property used as an event or heartbeat rather than a state |

Coalescing groups by destination — owning entity, distance rule and
reliability — so properties on different entities still get their own packets.
`Push()` ignores the batch and sends immediately.

### Unchanged values are not sent

Assigning a value equal to the one already held does nothing: no packet, and
`ValueChanged` does not fire. This applies to types where comparison is cheap
and means what it looks like — primitives, strings, and structs that compare
without boxing.

**Reference types are always sent.** The same `List<T>` instance can hold
different items from one assignment to the next, so "same reference" must never
be read as "nothing changed".

`AlwaysSend()` restores the original behaviour, and `Push()` has always sent
unconditionally.

### Sync types

| `SyncType` | Meaning |
| --- | --- |
| `Broadcast` | Send to everyone allowed by the transfer type and the distance limit. |
| `Post` | Send to one specific player. Used for fetch replies and `Push(steamId)`. |
| `Fetch` | Ask the other side to send its value back. Payload is ignored on arrival. |
| `None` | Set locally, send nothing. |

On the receive side `Post` and `Broadcast` are identical: both apply the value.

### Transfer types

| `TransferType` | Client may send | Server may send |
| --- | --- | --- |
| `Both` | yes | yes |
| `ServerToClient` | no (except `Fetch`) | yes |
| `ClientToServer` | yes | no |

`Fetch` is exempt from the client-side check so a read-only client can still ask
for the authoritative value.

Note that these are *send-side* checks only. A `ClientToServer` property that
somehow receives an update from the server still applies it, and a server always
re-broadcasts values it receives regardless of transfer type.

## Events

```csharp
property.ValueChanged          += (oldValue, newValue) => { };
property.ValueChangedByNetwork += (oldValue, newValue, senderSteamId) => { };
property.BeforeFetchRequestResponse += senderSteamId => { };
```

* `ValueChanged` fires on every assignment that actually changes the value,
  local or remote. It does *not* fire for `Push()`, which sends without changing
  anything, nor for an assignment that changes nothing (unless you called
  `AlwaysSend()`).
* `ValueChangedByNetwork` fires only for values arriving over the network, after
  `ValueChanged`. `senderSteamId` is 0 for updates originating on a dedicated
  server.
* `BeforeFetchRequestResponse` fires on the machine *answering* a fetch, just
  before the reply is built — use it to refresh the value first. Edits made in
  the handler are visible in the reply.

## Sync on load

With `syncOnLoad: true` (the default) the property asks for the current value as
soon as it can:

* **Session-scoped**: immediately, in the constructor.
* **Entity-scoped**: on the entity's `AddedToScene` event, then it unsubscribes.
  This matters — properties declared in a block's `Init` would otherwise fetch
  before the grid exists on the client.

Servers never fetch (`Fetch()` returns immediately when
`MyAPIGateway.Multiplayer.IsServer`), so this is purely a client-side seeding
mechanism.

## Distance limiting

With `limitToSyncDistance: true` (the default) an **entity** property's updates
go through the positional send: only players within
`MyAPIGateway.Session.SessionSettings.SyncDistance` of the entity receive them.
Session-scoped properties have no position, so they always broadcast regardless
of this flag.

The trade-off: a player who was out of range while a value changed does not get
a late update — they only re-sync if something triggers a fetch or another push.

The player list behind the range test is snapshotted once per game frame, so a
block updating several properties pays for one query rather than one per
property. A player who joins mid-frame receives their first update on the next
frame.

## Things that silently do nothing

`SendValue` returns early, without sending, when:

| Condition | Logged? |
| --- | --- |
| `NetworkAPI` was never initialized | error, always |
| `SyncType.None` | only with `LogNetworkTraffic` |
| The transfer type forbids this direction | only with `LogNetworkTraffic` |
| `MyAPIGateway.Session.OnlineMode == OFFLINE` | only with `LogNetworkTraffic` |
| `Value` is `null` and the sync type carries a value | only with `LogNetworkTraffic` |
| the new value equals the old one (unless `AlwaysSend()`) | no |

In all but the last case the local value is still updated and `ValueChanged`
still fires; only the network send is skipped. An assignment that changes
nothing does neither. Turn on `NetworkAPI.LogNetworkTraffic = true` when a value
is not arriving — the log names the property and the reason.

### Null values

A `null` value is never transmitted — there is nothing to encode. Everything
else works: you can assign to a property that is currently null, and a property
sitting at null can still fetch, because a fetch carries no payload.

So a `NetSync<string>` left at its default will not *push* anything until it
holds a value, but it will happily receive one. If you want it to have something
to send from the start, give it one:

```csharp
new NetSync<string>(this, TransferType.Both, string.Empty);
```

(Assigning to a null-valued property used to throw `ArgumentNullException`.
That is fixed.)

## Lifetime

Closing an entity removes its properties from the registry and unhooks its
events. Unloading the world clears both registries, so nothing carries over into
the next session.

A handler on `ValueChanged` or `ValueChangedByNetwork` that throws is caught and
logged; it will not stop the other handler or the rest of the packet.
