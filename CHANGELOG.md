# Changelog

## 2.0.0

Mods embed these sources rather than referencing a binary, so updating means
copying the files into your mod again. The wire format is backward compatible:
a build of this version understands packets from the previous one.

### Breaking behaviour

Three changes an existing mod can notice. Everything else is additive.

**Assigning a value that has not changed sends nothing, and does not raise
`ValueChanged`.** This only applies where comparison is cheap and means what it
looks like: numbers, strings, enums, and structs that compare by value. Classes
are always sent, because the same instance can hold different contents than it
did a moment ago.

If your mod uses a property as a signal rather than as state — assigning the
same value again to trigger something — call `AlwaysSend()` on it, or use
`Push()` at the point you want the send:

```csharp
trigger = new NetSync<bool>(this, TransferType.Both, false).AlwaysSend();
```

Patterns that already alternate are unaffected: a flag cleared locally with
`SetValue(false, SyncType.None)` after acting, or one toggled with
`Value = !Value`, both keep working untouched.

**Command names now match case insensitively.** A `SendCommand("Update")` that
previously reached nobody now reaches the handler registered as `"update"`.
Nothing that worked can break, but a callback that never used to fire may start
firing. `UnregisterNetworkCommand` and `UnregisterChatCommand` match the same
way, so unregistering by the spelling you registered with now works.

**`Fetch()` is asynchronous by a frame.** Requests raised in the same frame
travel in one packet. A fetch was always answered over the network, so nothing
could observe it completing synchronously, but code that calls `Fetch()` and
immediately inspects the wire will see the packet a frame later.

### New

* `NetSync<T>.Coalesce()` — batches this property's updates with every other
  coalesced property that changes in the same frame, into one packet per
  destination. Costs a frame of latency.
* `NetSync<T>.Lossy()` — permits the unreliable channel for updates that fit
  within `NetworkAPI.UnreliableMessageLimit`. Fetches stay reliable.
* `NetSync<T>.AlwaysSend()` — restores sending on every assignment.
* `NetworkAPI.NetworkType` — `Client`, `Server` or `Dedicated`. Derived from the
  instance, so `NetworkType != NetworkTypes.Client` guarantees the `Server` cast
  succeeds.
* `NetworkAPI.Version` — stamped into the startup log line.
* `NetworkAPI.UnreliableMessageLimit` — the size above which the game discards an
  unreliable message.
* `NetworkAPI.CompressionThreshold` is now settable at runtime, and defaults to
  1024 rather than 100000. It was above both the network MTU and the unreliable
  ceiling, so in practice nothing was ever compressed. A compressed copy is kept
  only when it actually came out smaller.

### Fixed

* `new NetSync<string>(this, TransferType.Both)` threw `ArgumentNullException`
  on first assignment. The lock it came from protected nothing and is gone.
* Closing an entity leaked it and every property on it for the rest of the
  session, and could evict an unrelated session property with a colliding id.
* Leaving a world kept every property from it alive for the rest of the process.
* A mod callback that threw abandoned the rest of the packet, and a chat command
  that threw stopped every mod behind this one from seeing the message.
* A server could not answer a fetch for a `ClientToServer` property.
* `SendCommandTo` printed its message on the host once per recipient.
* An explicit `sent` timestamp was discarded by clients and by positional sends.
* Unreliable messages over 1024 bytes were discarded by the game with no trace;
  they are sent reliably instead.
* A corrupt `Timestamp` threw out of `DateTime` and cost the whole packet.
* A positional send with `radius: 0` and no session threw.
* `GetEntityById` returning something that is not a `MyEntity` threw.
* The verbose log labelled old and new values the wrong way round.

### Performance

Measured against the previous version, each scenario in its own process:

| | before | after |
| --- | --- | --- |
| client streams in 200 blocks x 4 properties | 1515 KB / 883 us / 800 packets | 247 KB / 211 us / 2 packets |
| server answers those fetches | 1495 KB / 1504 us / 800 packets | 575 KB / 824 us / 2 packets |
| property fetch | 3720 B / 2958 ns | 592 B / 608 ns |
| entity property assign, nobody in range | 1456 B / 963 ns | 504 B / 327 ns |
| session property assign | 1336 B / 826 ns | 944 B / 658 ns |
| chat line that is not ours | 408 B / 118 ns | 0 B / 12 ns |

Joining a world was the worst of it: every property on every block that streamed
in sent its own fetch, and the server answered each separately.

A plain command broadcast costs 16 bytes and about 30ns more than before, since
the packet gained two fields for carrying property updates inline. Property
traffic more than repays it; a mod that only uses commands pays a little.

### Wire format

`Command` fields 1 to 7 are unchanged, as are every enum value. Fields 8
(`Property`) and 9 (`Properties`) were added to carry property updates on the
packet itself rather than as separately encoded bytes. The original layout is
still decoded on receive.
