# In game test mod

Everything else in this repository is verified against a stub of the ModAPI.
This is the only part that runs inside Space Engineers, so it is what proves the
engine behaves the way the unit tests assume.

## Running it

1. Copy these files, plus the API sources from the repository root, into a mod
   folder under `%AppData%/SpaceEngineers/Mods/`.
2. Enable the mod in a world, search "Test Block" in the G menu and place one.
3. Drive it from chat with `test <command>`.

| Command | What it does |
| --- | --- |
| `test help` | lists the commands |
| `test local` | checks that need only this machine; prints PASS/FAIL |
| `test reset` | zeroes the counters here and on the other machine |
| `test report` | what this machine has received since the last reset |
| `test burst` | runs one pass of every network scenario |

The block also has a **Run Network Test** button in its terminal, which does the
same as `test burst`.

## What to check, and where

`test local` is self-scoring. The network scenarios are not: run `test burst` on
one machine and `test report` on the other.

| Configuration | Why it matters |
| --- | --- |
| Single player | Nothing should be sent at all; the API skips the network when the world is offline |
| Listen server + client | The normal case, and the only one where the host's own packets loop back |
| Dedicated server + client | No local player on the server: `Command.SteamId` is 0 there, and the frame counter and update thread differ |

## Reading a report

After `test reset` on both machines and one `test burst`, the other machine
should report:

```
packets received: 3
property updates received: 24
batch 12, coalesced 4, lossy 1, deduped 1 (expect 1), always 3 (expect 3), request 0
```

The counts that matter:

* **packets received: 3** — twelve plain properties in one packet, four
  coalesced ones in another, and the session value. If this is closer to twenty
  then batching is not working, which most likely means `InvokeOnGameThread`
  never fired.
* **deduped 1** from three assignments — change detection is working.
* **always 3** from three assignments — `AlwaysSend()` is working.
* **batch 12** — every property arrived, so declaration order lines up.

## Also worth watching

* Join a world that already has a Test Block placed and watch the log for the
  sync-on-load fetch. It should be one packet, not one per property.
* Set `NetworkAPI.LogNetworkTraffic = true` in `SessionTest.cs` to see every
  packet in the log, with the property it belongs to.
* The startup line names the version: `[NetworkAPITest] ready. API version 2.0.0`.
