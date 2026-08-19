# Testing

```bash
dotnet test tests/SENetworkAPI.Tests
```

301 tests, no game install required, about a second to run.

If you have the game installed, there is a second check that needs no test
runner — it compiles the shipped sources against the real assemblies:

```bash
dotnet build tests/GameContractCheck
dotnet build tests/GameContractCheck -p:GameBin=/path/to/SpaceEngineers/Bin64
```

Nothing runs; the compiler is the test. It catches API drift and surfaces
obsolete-API warnings, which is the one thing the stub harness cannot see.

## Why there is a stub assembly

The mod sources reference `Sandbox.ModAPI`, `VRage.*` and `Sandbox.Game.*`.
Those assemblies ship with the game, are Windows-only, and cannot be loaded by a
test runner. They are also unavailable to anyone who does not own the game.

`tests/SEStubs` therefore re-declares **only the surface these five files
actually touch**, under the same namespaces and with the same signatures, so the
production sources compile against it unmodified:

| Stub | Stands in for |
| --- | --- |
| `MyAPIGateway.Utilities` | chat input/output, protobuf serialization |
| `MyAPIGateway.Multiplayer` | `IsServer`, message handlers, the three send calls |
| `MyAPIGateway.Session` | local player, sync distance, online mode |
| `MyAPIGateway.Players` | the range query behind positional sends |
| `MyAPIGateway.Entities` | `GetEntityById` |
| `MyEntity`, `MyCubeBlock`, `MyGameLogicComponent`, `MySessionComponentBase` | the objects a `NetSync` can attach to |
| `MyCompression` | payload compression (GZip here) |
| `MyLog.Default` | the game log, retained in memory so tests can assert on it |

There is also an allocation and throughput harness:

```bash
dotnet run -c Release --project tests/Benchmarks
```

See [performance.md](performance.md) for what it measures and the current
numbers.

## How faithful is it?

The stub surface was checked against the shipped assemblies by metadata
inspection and decompilation, and matches them on the things that matter:

* **Namespaces and signatures.** The ModAPI interfaces are declared where the
  game declares them (`VRage.Game.ModAPI`, `VRage.ModAPI`); only `MyAPIGateway`
  lives in `Sandbox.ModAPI`. `SessionSettings` is a
  `MyObjectBuilder_SessionSettings`, `MyEntity.OnClose`/`AddedToScene` are
  `Action<MyEntity>`, `SyncDistance` is an `int` field, and the send calls
  return `bool`.
* **Serialization.** `StubSerializer` is the same two lines as
  `MyAPIUtilities.SerializeToBinary`: a bare `Serializer.Serialize` with no
  wrapper. The fork the game ships and the stock protobuf-net package produce
  identical bytes for root-level values (int 2, float 5, `"hello"` 7).
* **Transport rules.** `FakeMultiplayer` reproduces the two behaviours of
  `MyMultiplayerBase` a mod can actually observe: unreliable messages over 1024
  bytes are refused, and a message addressed to the local player is delivered
  back into the local handlers.

Known, deliberate divergences: compression is GZip rather than Keen's block
compressor (same contract — bytes in, smaller bytes out, round-trips); `MyLog`
retains lines in memory so tests can assert on them; and a few members have
setters the real types do not (`MyGameLogicComponent.Entity`,
`MyCubeBlock.CubeGrid`) so tests can construct scenarios.

If you extend the API and touch a new piece of the ModAPI, add it to the stubs —
and check the real signature first, e.g. with `ilspycmd` against `Bin64`. The
stub is deliberately dumb: public settable state, no behaviour beyond what the
real call does.

## How the tests are wired

`tests/SENetworkAPI.Tests` compiles the production `.cs` files **into the test
assembly** (`<Compile Include="../../*.cs" />`) rather than referencing a built
library. That gives the tests access to `internal` types like `Command`,
`SyncData` and the `NetSync` registries without adding `InternalsVisibleTo` to
shipped code — and it works with a repo that has no project file, because Space
Engineers compiles mods itself.

`FakeGame` wires a whole fake session into `MyAPIGateway` and records what the
mod does:

```csharp
Game.Sent            // every packet handed to the multiplayer layer
Game.ShownMessages   // every ShowMessage call
Game.Log             // every log line, by severity
```

`NetworkTestBase` resets all of SENetworkAPI's statics before and after each
test and offers the setup helpers (`GivenServer`, `GivenClient`,
`GivenDedicatedServer`) plus packet encode/decode helpers. Test parallelism is
disabled because that static state is process-wide.

## Writing a test

```csharp
public class MyTests : NetworkTestBase
{
    [Fact]
    public void TheServerBroadcastsAnUpdate()
    {
        Server server = GivenServer();
        Game.ClearTraffic();

        server.SendCommand("update", data: new byte[] { 1 });

        SentPacket packet = Assert.Single(Game.Sent);
        Assert.Equal(PacketTarget.Others, packet.Target);
        Assert.Equal("update", DecodeCommand(packet).CommandString);
    }
}
```

To drive the receive path, build wire bytes with `EncodeCommandPacket` /
`EncodePropertyPacket` and hand them to `Receive`.

## Playing both sides

The registries are static, so one process cannot be a server and a client at the
same time. The end-to-end tests instead capture the bytes one role emits, call
`Restart()` to tear the instance down, start a fresh instance in the other role,
and deliver those bytes — see `IntegrationTests`.

## Coverage map

| Test class | Covers |
| --- | --- |
| `CommandTests` | `Command`/`SyncData` protobuf round trips |
| `LifecycleTests` | `Init`, role selection, handler registration, `Close`/`Dispose`, `SessionTools` |
| `ChatCommandTests` | keyword matching, argument parsing, unknown commands |
| `CommandRegistrationTests` | register/unregister rules for both command kinds |
| `IncomingPacketTests` | the receive path: dispatch, chat relay, decompression, failure containment |
| `ClientSendTests` | client send path, compression, missing session |
| `ServerSendTests` | broadcast, targeted, multi-target and positional sends |
| `NetSyncConstructionTests` | id assignment, registries, sync-on-load, `Descriptor()` |
| `NetSyncValueTests` | value/events, sync types, transfer gating, packet shape, send guards |
| `NetSyncNetworkTests` | receive routing, re-broadcast, routing failures, fetch replies, packet layouts |
| `CoalescingTests` | opt-in batching: grouping, flush timing, interaction with Push and Fetch |
| `TimingTests` | `GetDeltaMilliseconds` / `GetDeltaFrames` |
| `SenderIdentityTests` | the trust model: unverified sender ids, arrival-side checks |
| `IntegrationTests` | full client↔server exchanges |

## Are the tests any good?

Passing tests only prove the code runs. To check they actually constrain it, the
suite is mutation tested: 49 deliberate breakages, introduced one at a time
across every path — deduplication, batch grouping, the reliability upgrade, the
compression rules, the player snapshot, entity cleanup, chat parsing, the relay,
timestamps, sync distance, the exception guards, and the lazy encode — with the
suite run against each.

All 49 are caught. Four of them were not, first time round, and each pointed at
a real gap: a test that asserted "no exception" without checking the branch it
meant to cover, and three performance behaviours that produce identical output
either way. The fake counts serialization calls (`Game.Utilities.SerializeCallCount`)
so tests can assert that work was *skipped*, not merely that the result matched.

If you add behaviour here, do the same to whatever you write to cover it: break
the code on purpose and make sure the test notices.

## Not covered

* Keen's block compressor (GZip stands in for it).
* Real network transport — ordering, loss, latency. Only the two engine rules
  above are modelled; delivery is otherwise instant and perfect.
* Anything requiring a running session: whether a component is created on a
  dedicated server, HUD rendering, actual Steam identities.
* Thread safety. The suite is single-threaded; see
  [known-issues.md](known-issues.md#lock-_value-does-not-synchronise-anything).
