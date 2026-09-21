# Performance and payload measurements

Run from the repository root, one scenario per process:

```sh
dotnet run -c Release --project tests/Benchmarks -- "nobody in range"
dotnet run -c Release --project tests/Benchmarks -- "the same, coalesced"
dotnet run -c Release --project tests/Benchmarks -- "2000 blocks x 4 coalesced"
dotnet run -c Release --project tests/Benchmarks -- payload
dotnet run -c Release --project tests/Benchmarks -- population 20 spread 2000
dotnet run -c Release --project tests/Benchmarks -- population 40 dense 2000
dotnet run -c Release --project tests/Benchmarks -- batch-payload
dotnet run -c Release --project tests/Benchmarks -- "batch encode compact"
```

The timing scenarios report allocated bytes and elapsed nanoseconds per operation.
The `payload` scenario reports packet count and total serialized bytes, excluding
transport headers. Use the same machine/runtime and separate processes when
comparing changes. Results are illustrative single runs, not statistical bounds.

## Measured changes

Comparison against production sources from commit `3a83159`, using the same
updated benchmark program and fake game on .NET 9.0.19 / Linux:

| Scenario | Before | After |
| --- | ---: | ---: |
| Distance-limited assignment, nobody nearby: allocation after warmup | 504 B | 0 B |
| Same assignment: elapsed time | 374 ns | 220 ns |
| 2,000 blocks × 4 dirty coalesced properties, 64 players: time per flush | 10.16 ms | 2.27 ms |
| Same large workload: allocated bytes per operation | 4,966,936 B | 4,966,936 B |
| 100 client entities, one coalesced integer each: packet count | 100 | 1 |
| Same 100 updates: total serialized bytes | 2,500 B | 1,015 B |
| Single property containing 10,000 repeated characters: serialized bytes | 10,017 B | 68 B |

The compression example is intentionally repetitive and the initial measurement
used the old stub's GZip framing. The stub now matches the installed game's
four-byte length prefix and default GZip compression mode; that single-property
example now measures 72 bytes. The harness still uses stock protobuf-net and a
different runtime from the game. Compression ratios and timings must be verified
in-game for representative data.
For the eight-property single-block scenario, initial timings were approximately
2.77 µs before and 2.93 µs after: dictionary grouping is intended to improve
scaling across many destinations, not every small operation. Allocation was
unchanged in that scenario.

## Implemented behavior

* Group coalesced updates with a dictionary instead of scanning the remaining
  queue for each entity. Clients share a destination across entities; unrestricted
  server properties do too. Distance-limited server groups stay separate.
* Resolve distance-limited recipients before serializing property values. Reuse
  the selected recipients and pool their lists across sends. An explicit recipient
  lookup stays fresh even when the frame's position snapshot predates a join.
* Keep server relays coalesced when the property opts in. Callbacks remain
  immediate, and the current value is serialized at the next flush.
* Bound batches by estimated protobuf size and a 500-value ceiling. The reliable
  byte budget is 16 KiB; the unreliable budget is 1,024 bytes. These are batching
  targets, not a new transport maximum. A single oversized value remains intact
  and uses reliable delivery when necessary.
* Compress large individual property packets through the existing legacy
  `Command.Data` layout. Keep the inline layout if compression does not save
  bytes. This individual-property path preserves the existing wire format.

## Spatial recipient queries

The index sorts the player snapshot along its widest coordinate axis and uses
binary searches to narrow sphere queries. Exact squared-distance tests still
select recipients, and recipient order is preserved. Snapshot freshness is
unchanged, and directed sends retain their fresh player lookup.

The current selection is tuned for typical 10–40-player servers. After 16 queries
in a frame with at least 16 players, selective searches may build an index. A
previous result containing at least a quarter of the population favors a scan.
When nearly everyone was nearby, a bounding-box test can prove that all players
are inside the new query and avoid individual distance calculations. Bounds and
index arrays are reused; current query position, radius, and exclusion always apply.

These numbers are algorithm crossover heuristics, not player limits. No path
truncates the recipient list. Regression coverage includes 5,000 eligible players.
Short bursts and populations below the crossover use direct scans.

### Tuning for 10–40 players

Medians of three runs, alternating baseline and candidate, with 2,000 queries per
frame including snapshot refresh. Baseline is the preceding 64-player-cutoff
implementation. Measurements use .NET 9 with `DOTNET_TieredCompilation=0` for
consistent compilation across separate processes; do not compare their absolute
times against earlier tables using default tiered compilation.

| Players | Distribution | Before | Tuned |
| --- | --- | ---: | ---: |
| 10 | Spread out | 42.81 µs | 41.79 µs |
| 10 | Groups of five | 50.64 µs | 50.25 µs |
| 10 | All nearby | 47.55 µs | 49.61 µs |
| 20 | Spread out | 64.56 µs | 43.50 µs |
| 20 | Groups of five | 71.77 µs | 69.69 µs |
| 20 | All nearby | 79.35 µs | 57.11 µs |
| 40 | Spread out | 125.54 µs | 49.68 µs |
| 40 | Groups of five | 122.51 µs | 79.54 µs |
| 40 | All nearby | 134.34 µs | 90.27 µs |

The population benchmark also supports 1, 8, or 128 queries per frame. The tuning
was checked at 8, 128, and 2,000 with spread, grouped, and dense populations.
Spread players are 20 km apart; grouped players are clustered in fives 20 km
apart; dense players are all within 400 m. Queries have a 10 km radius and cycle
through player locations, rather than mostly querying empty space. Ten-player
performance remains approximately unchanged, including a small dense-case cost.
No deliberate delay or update-rate reduction is introduced.

### Earlier large-population measurements

Before/after the initial spatial change (before the above tuning), 2,000 queries per frame (including snapshot and
index rebuilding), 300 measured operations after warmup:

| Players / distribution | Linear | Indexed / adaptive |
| --- | ---: | ---: |
| 8, sparse | 196 µs | 167 µs |
| 64, sparse | 582 µs | 201 µs |
| 256, sparse | 1,060 µs | 277 µs |
| 64, dense | 597 µs | 607 µs |

Both paths allocate approximately zero per operation after warmup (the benchmark
reports 0.1 B/op from measurement overhead). These are single-run timings; the
small-population difference should not be attributed to indexing, which is off.

## Compact batch format

Set `NetworkAPI.UseCompactBatches = true` on senders after all recipients have the
updated decoder. It defaults to false; there is no capability negotiation.
Updated decoders continue accepting old formats, even with compact sending off.

Format 1 uses Command field 10 as its version marker and Command.Data as the
payload. A shared entity ID and sync type replace repeated copies when possible;
packed ID/length arrays describe concatenated value bytes. Heterogeneous batches
carry per-value entity IDs/types. Null and empty values remain distinct. The
original game-serialized value bytes are copied intact. An entire compact payload
is compressed above CompressionThreshold only when the result saves bytes.

The final compact encoding is kept only if smaller than the legacy layout.
CompactBatchThreshold (default 256 bytes) avoids allocating compact envelopes for
small batches. There is no extra batching window, rate limit, or frame budget.
The existing byte budgets, reliable fallback, and opt-in coalescing schedule remain.

Payload measurements with the corrected GZip stub, excluding transport headers:

| Batch | Legacy | Compact |
| --- | ---: | ---: |
| 64 integers, shared entity | 1,162 B | 290 B |
| 64 integers, mixed entities | 1,162 B | 733 B |
| 64 random 128-byte values, shared entity | 9,354 B | 8,358 B |
| 64 random 128-byte values, mixed entities | 9,354 B | 8,508 B |
| 256 integers, shared entity, whole-batch compression | 4,874 B | 800 B |

The 64-integer compact payloads remain uncompressed below the default compression
threshold; their gains come from metadata packing. Random byte arrays illustrate
that metadata savings remain possible without compressible application data.

CPU/allocation tradeoff for encoding/decoding one batch of 256 already serialized
integer values, measured separately (20,000 operations):

| Operation | Legacy | Compact |
| --- | ---: | ---: |
| Encode | 18.7 µs / 20,272 B allocated | 22.7 µs / 11,400 B allocated |
| Decode | 21.4 µs / 22,784 B allocated | 17.0 µs / 29,272 B allocated |

Compression adds sender CPU work; splitting packed values adds receiver
allocations. Enable it for bandwidth savings rather than assuming every CPU or
allocation metric improves. Recheck these tradeoffs in-game with real mod values.

The installed game's SerializeToBinary implementation owns its MemoryStream and
returned byte array; its public call has no reusable-buffer argument. These
changes keep that API and focus on routing and packet structure.

## Validation

```sh
dotnet test tests/SENetworkAPI.Tests -c Release
dotnet build tests/GameContractCheck
```

373 tests passed after these changes. The game-contract build also passed against
the installed game assemblies, with the existing `SessionTools` warning about
calling obsolete `NetworkAPI.Dispose()`. Compilation is not an in-game runtime
or mod-whitelist check.
