# Testing

```bash
dotnet test tests/SENetworkAPI.Tests
```

192 tests, no game install required, about a second to run.

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

Serialization uses real protobuf-net — the same library the game uses — with a
one-field envelope so that bare primitives round-trip.

If you extend the API and touch a new piece of the ModAPI, add it to the stubs.
The stub is deliberately dumb: public settable state, no behaviour beyond what
the real call does.

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
| `NetSyncNetworkTests` | receive routing, re-broadcast, routing failures, fetch replies |
| `TimingTests` | `GetDeltaMilliseconds` / `GetDeltaFrames` |
| `IntegrationTests` | full client↔server exchanges |

## Not covered

* Real Space Engineers serialization and compression (protobuf-net and GZip
  stand in for them).
* Real network transport — ordering, loss, MTU and the game's own reliability
  layer.
* Thread safety. The suite is single-threaded; see
  [known-issues.md](known-issues.md#lock-_value-does-not-synchronise-anything).
