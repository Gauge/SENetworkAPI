# Performance tasks

## Constraints

- Optimize for typical servers with 10–40 players, without any player or recipient cap.
- Preserve immediate updates and the existing opt-in coalescing schedule. Do not introduce throttling or delayed update budgets.
- Preserve value precision and use the game's serialization API; it owns its output buffers.
- Keep the default outbound format compatible with older peers. Compact batches remain opt-in and require updated receivers.
- Ship only `SENetworkAPI/`. Tests, stubs, benchmarks, and task notes stay outside that folder.

## Remaining validation

- [ ] Profile in Space Engineers with 10, 20, and 40 players: sparse players, nearby groups, and crowded areas. Record networking CPU time, allocations/GC, bytes sent, and update latency during normal play and grid update bursts.
- [ ] Exercise joins, disconnects, moving players/entities, and changing ranges in live multiplayer. Confirm recipient correctness and that updates remain prompt.
- [ ] Run the in-game scenarios in `TestFiles/` on a listen server and dedicated server with clients. Check both default and compact batch modes with updated peers, and default mode with older peers.
- [ ] Verify the runtime sources pass the game's mod script compiler/whitelist. Building against game assemblies does not validate the runtime whitelist.
- [ ] Measure compact encoding and compression with the game's actual serializer on representative mod values. Compare payload savings against encode/decode CPU time and receiver allocations before changing thresholds or defaults.
- [ ] Use those measurements to decide whether further allocation or payload changes are worthwhile; record evidence in `tests/Benchmarks/README.md` before implementing them.

## Completed implementation

- [x] Group coalesced updates without repeated full-list scans and reuse temporary collections.
- [x] Select recipients before serializing range-limited updates.
- [x] Adapt recipient lookup for common server populations without truncating recipients.
- [x] Split batches by size and compress large values when beneficial.
- [x] Add optional compact batch metadata and whole-batch compression.
- [x] Add regression coverage and reproducible performance/payload benchmarks.
- [x] Separate copy-ready runtime sources from development files.

## Development checks

Run from the repository root:

```sh
dotnet test tests/SENetworkAPI.Tests -c Release
dotnet build tests/Benchmarks -c Release
dotnet build tests/GameContractCheck
```

The game contract check requires installed Space Engineers assemblies; pass
`-p:GameBin=/path/to/SpaceEngineers/Bin64` if needed. Unit tests and benchmarks use
stubs and do not replace the live-game validation above.
