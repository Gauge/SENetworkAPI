# Performance

The API sits on two paths that run constantly in a populated world:

* **every property assignment** — encode the value, wrap it, encode the wrapper,
  wrap that, encode the packet, pick recipients, send;
* **every chat line anyone types** — the game hands it to every mod's handler.

`tests/Benchmarks` measures both, plus the receive path:

```bash
dotnet run -c Release --project tests/Benchmarks
```

It runs against the stub harness, so treat the absolute numbers as indicative
and the deltas as real: the serializer is the same protobuf-net the game uses,
and the allocation counts come from the same code the game would run.

## Where it stands

Measured on .NET 9, workstation GC, 200k iterations per scenario. "before" is
the code as it was prior to the optimisation work.

| scenario | bytes/op before | after | ns/op before | after |
| --- | ---: | ---: | ---: | ---: |
| chat line that is not ours | 408 | **0** | 125 | **5** |
| chat line that is ours | 440 | **240** | 155 | **67** |
| property fetch | 3720 | **560** | 1792 | **185** |
| session property assign | 1336 | **944** | 795 | **734** |
| entity property assign (8 recipients) | 1896 | **1224** | 1051 | **~500** |
| server receives + relays a property | 1784 | **1040** | 1541 | **1415** |
| receive command packet | 312 | **296** | 777 | **715** |
| 8 properties on a block, one frame | 8192 | **4359** | 3462 | **1954** |
| entity property assign, nobody in range | 904 | **504** | 376 | **218** |

The last row needs `Coalesce()`; everything else is automatic.

What changed, and why:

* **Chat.** The old handler lower-cased and split every message before deciding
  it was not addressed to this mod. Since every mod using the API pays that on
  every line anyone types, it is now a prefix comparison: no allocation at all.
* **One less encode pass.** Property updates used to be encoded to bytes and
  then encoded again inside the envelope. Protobuf charges per call — a
  `MemoryStream` and a `ToArray` every time, about 376 bytes whatever is in it —
  so nesting the message instead of its bytes removes a third of the cost.
* **Fetch.** A fetch used to encode and ship the requester's current value,
  which the receiver discards. It sends the address only.
* **Unchanged values.** An assignment that changes nothing no longer sends.
* **Coalescing.** Opt-in batching turns a block's simultaneous property changes
  into one packet.
* **Player snapshot.** The range query used to walk the engine's player list and
  call `GetPosition()` per player, per property, per frame. It is snapshotted
  once per frame into parallel arrays, after which the range test is arithmetic.
* **Nothing is encoded for an empty recipient list.** A distance-limited
  property used to serialize its packet and then discover nobody was near
  enough to receive it. On a large world that is most blocks most of the time.
* **Relay.** A server re-broadcasting a value reuses the bytes it was handed.
* **Range filters.** Cached delegates instead of a closure allocated per send.
* **Receive.** No `Split()` to read the first word of a command, one `DateTime`
  instead of two, one dictionary probe instead of two, no lower-casing.
* **`lock (_value)`.** Boxed the value on every assignment of a value type. Gone
  (it also protected nothing — see [known-issues.md](known-issues.md)).
* **Compression.** The threshold was 100000 bytes, above both the MTU and the
  engine's unreliable ceiling, so nothing was ever compressed in practice. It is
  1024 now, settable, and the compressed copy is kept only when it is smaller.

## What is left, and why

A property update still costs ~1.3KB. Almost all of it is three nested
protobuf passes that the wire format requires:

```
Command { Data = encode( SyncData { Data = encode(T) } ) }
```

Each `MyAPIGateway.Utilities.SerializeToBinary` call creates a `MemoryStream`,
grows a buffer, and copies it out with `ToArray`. Removing a pass would mean
changing the packet layout, which every mod already on the network would have
to change with it. Not worth it.

One encode pass per distinct value is therefore the floor, and coalescing is
what gets the *number* of values down.

Two things were tried and rejected:

* **Reusing a shared player list** across positional sends. It removed 128 bytes
  per send but measured consistently *slower* — a long-lived array takes write
  barriers on every store and is rescanned by every Gen0 collection. The
  short-lived list wins.
* **Blanket change detection.** Now the default, but only for types where
  comparison is cheap and meaningful. `EqualityComparer<T>.Default` boxes for
  structs that do not implement `IEquatable<T>`, and reference equality would be
  actively wrong for a mutable `List<T>` — the same instance with different
  contents would look unchanged. Both cases fall back to always sending.

## Rules this code has to play by

Space Engineers compiles mod sources itself and runs a whitelist analyzer over
them (`MyScriptWhitelist`, registered in `SpaceEngineers.Game`). Only
whitelisted types and members compile. That rules out most of the usual
allocation-reduction toolkit:

| Available | Not available |
| --- | --- |
| all of `string` (`IndexOf`, `Substring`, `StartsWith`, `Compare`) | `Span<T>`, `Memory<T>` |
| `StringComparison`, `StringComparer` | `System.Buffers`, `ArrayPool<T>` |
| `System.Collections.Generic`, `System.Linq` | `[MethodImpl(AggressiveInlining)]` |
| `Action<...>`, `Func<...>`, `Math`, `Array`, `Buffer`, `BitConverter` | most of `System.Runtime.CompilerServices` |
| `Monitor`, `Interlocked`, `ConcurrentDictionary` | `System.Reflection` beyond a few members |

Before reaching for something new, check it against the registration calls in
`SpaceEngineers.Game.dll` (`AllowTypes` / `AllowNamespaceOfTypes` /
`AllowMembers`). Whitelisting a *type* allows all of its members; whitelisting
members allows only those.
