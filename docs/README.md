# SENetworkAPI documentation

| Document | What is in it |
| --- | --- |
| [architecture.md](architecture.md) | The pieces, the roles, how a packet flows, what state is static |
| [networkapi.md](networkapi.md) | `NetworkAPI`, `Client`, `Server`: init, commands, chat, sending, receiving |
| [netsync.md](netsync.md) | `NetSync<T>`: declaring, addressing, sync/transfer types, events, pitfalls |
| [protocol.md](protocol.md) | The `Command` and `SyncData` wire format, compression, timestamps |
| [known-issues.md](known-issues.md) | Current bugs and sharp edges, each pinned by a test |
| [performance.md](performance.md) | Hot paths, measurements, and the modding whitelist's limits |
| [testing.md](testing.md) | How the stub harness works and how to run or extend the suite |
| [../CHANGELOG.md](../CHANGELOG.md) | What changed in this release, and what to do about it |

New here? Read [architecture.md](architecture.md), then whichever of
[networkapi.md](networkapi.md) (messaging) or [netsync.md](netsync.md)
(variable syncing) matches what you are building — then skim
[known-issues.md](known-issues.md) before you ship.
