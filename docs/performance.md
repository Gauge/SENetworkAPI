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
the code as it was prior to the optimisation pass.

| scenario | bytes/op before | after | ns/op before | after |
| --- | ---: | ---: | ---: | ---: |
| chat line that is not ours | 408 | **0** | 125 | **5** |
| chat line that is ours | 440 | **240** | 155 | **68** |
| property fetch | 3720 | **920** | 1792 | **242** |
| receive command packet | 312 | **280** | 777 | **708** |
| server receives + relays a property | 1784 | **1384** | 1541 | **1448** |
| entity property assign (8 recipients) | 1896 | **1768** | 1051 | **1025** |
| entity property assign (3 recipients of 64 players) | 1608 | **1480** | 469 | **448** |
| session property assign | 1336 | **1312** | 795 | **799** |

What changed, and why:

* **Chat.** The old handler lower-cased and split every message before deciding
  it was not addressed to this mod. Since every mod using the API pays that on
  every line anyone types, it is now a prefix comparison: no allocation at all.
* **Fetch.** A fetch used to encode and ship the requester's current value,
  which the receiver discards. It now sends the address only.
* **Relay.** A server re-broadcasting a value it just received reuses the bytes
  it was handed rather than re-encoding the value it decoded from them.
* **Range queries.** The positional send built a closure per call to carry
  point/radius/sender into the filter. The filters are cached delegates now.
* **Receive.** No `Split()` to read the first word of a command, one `DateTime`
  instead of two, one dictionary probe instead of two, and no lower-casing.
* **`lock (_value)`.** Boxed the value on every assignment of a value type. It
  is gone (it also protected nothing — see [known-issues.md](known-issues.md)).

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

Two things were tried and rejected:

* **Reusing a shared player list** across positional sends. It removed 128 bytes
  per send but measured consistently *slower* — a long-lived array takes write
  barriers on every store and is rescanned by every Gen0 collection. The
  short-lived list wins.
* **Skipping sends when the value has not changed.** Tempting, since mods often
  assign every frame, but `NetSync` has always treated every assignment as a
  change, and `EqualityComparer<T>.Default` boxes for structs that do not
  implement `IEquatable<T>` — which would make the common case worse. If your
  mod assigns on a timer, compare before assigning:

  ```csharp
  if (property.Value != computed) property.Value = computed;
  ```

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
