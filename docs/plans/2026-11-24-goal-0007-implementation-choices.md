# Goal 0007 Implementation Choices

- **Build/TFM-scoped default endpoints & version-tolerant control (`Protocol.fs`, `Ipc.fs`, `fsion/Program.fs`)**:
  - `ProtocolVersion` bumped to `5`. `defaultEndpointName ()` now encodes `fsion-<user>-<buildFingerprint>` (where `buildFingerprint` is `p5-net10.0-<major.minor>` / `p5-net11.0-<major.minor>`), isolating `net10.0` vs `net11.0` and upgraded binaries without collisions.
  - `parseRequest` and `parseResponse` decode control frames (`GetStatus` = `2uy`, `Shutdown` = `3uy`, `Status` = `12uy`, `ShuttingDown` = `13uy`, `Failure` = `15uy`) regardless of wire version; `StatusInfo` carries optional trailing fields (`ProtocolVersion`, `TargetFramework`, `BuildFingerprint`, `PoolSize`, `IdleTtlMinutes`, `SessionTtlMinutes`, `GenerationId`) with backwards-compatible stream-length gating so `daemon status` and `daemon stop`/`restart` always succeed across version skew.
- **OS-level IPC permissions & detached Unix session (`Ipc.fs`, `fsiond/Program.fs`)**:
  - Windows `NamedPipeClientStream` and `NamedPipeServerStream` enforce `PipeOptions.CurrentUserOnly`.
  - POSIX sockets live under `<XDG_RUNTIME_DIR|$TMPDIR>/fsion-<uid>` with `chmod 0700` directory and `chmod 0600` socket permissions, guarded by an atomic non-blocking `flock(LOCK_EX | LOCK_NB)` lockfile (`<sock>.lock`) eliminating the bind/stale-unlink race.
  - `fsiond` calls `setsid()` on POSIX startup (`detachPosixSession ()`) so it leaves the caller's process group.
- **Fair FIFO cancellable `ProcessStateGate` & nested deadlock prevention (`SessionHost.fs`)**:
  - Replaced uninterruptible `Monitor.Wait` with an explicit FIFO `LinkedList<GateWaiter>` queue, honoring `disconnectToken` and the remaining `--timeout` budget (`remainingGateMs ()`, returning clean exit `124`/`TimedOut = true`/`Wedged = false`).
  - Active runs stamp `FSION_ACTIVE_ENDPOINT` and `FSION_ACTIVE_RUN_ID` in process env; a nested child `fsion` targeting the same endpoint with a conflicting cwd/env without `--timeout` fails fast with an actionable diagnostic instead of deadlocking forever.
- **Non-collateral wedged drain & named-session generation tombstones (`Server.fs`, `SessionHost.fs`)**:
  - `Server.fs` replaces non-atomic refs with `Interlocked`/`Volatile` counters (`activeRequests`, `wedgedCount`, `stopped`, `lastActivityTicks`).
  - `scheduleSelfTermination` stops accepting and releases endpoint ownership immediately so a fresh `fsiond` starts at once, while waiting for `activeRequests <= wedgedCount` (up to a 60 s ceiling) so healthy concurrent siblings finish cleanly.
  - Named sessions write a lightweight generation tombstone under `<temp>/fsion-sessions-<endpoint>/<sha256>.gen`; after a daemon restart/respawn, calling `--session <id>` without `--reset` reports an explicit session-lost error advising `--reset`.
- **Idle memory compaction & Windows working-set trim (`fsiond.fsproj`, `SessionHost.fs`)**:
  - `fsiond.fsproj` sets `System.GC.ConserveMemory = 9` and `GarbageCollectionAdaptationMode = 1`.
  - Quiet `phase2` unload sets `GCLargeObjectHeapCompactionMode.CompactOnce` before the idle Gen-2 collection.
  - On Windows, `tryTrimIdleWorkingSet` compacts and calls `psapi!EmptyWorkingSet(-1)` once per idle epoch after `FSION_TRIM_IDLE_MINUTES` (default `5.0`, `0` disables).
