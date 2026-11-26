# Goal 0007 Evidence — Protocol Coexistence, Endpoint Hardening & Fault Isolation

## Summary

Goal 0007 implemented TFM/build-scoped default endpoints, version-tolerant `GetStatus`/`Shutdown`/`Failure` control frames, `PipeOptions.CurrentUserOnly` (Windows) and `0700`/`0600` + `flock` + `setsid` (POSIX), fair FIFO cancellable `ProcessStateGate` with nested-call deadlock detection, non-collateral wedged draining, named-session generation-loss tombstones, atomic `Server.fs` counters, `System.GC.ConserveMemory = 9`, idle LOH compaction, and Windows `EmptyWorkingSet` idle trimming.

## Verification

- `HardeningTests` (5/5 passed on `net10.0`, Release):
  1. `default endpoint incorporates TFM and build fingerprint and control commands tolerate protocol version skew`: verified `defaultEndpointName ()` embeds TFM + fingerprint, and simulated protocol-999 peer answers `fsion daemon status` and `fsion daemon stop` cleanly.
  2. `IPC permissions enforce owner-only access and detached POSIX daemon survives caller process group`: verified Windows `CurrentUserOnly` pipe handshake, `daemon status` TFM/protocol fields, and POSIX `0700` dir / `0600` socket assertions (executed on Linux/macOS CI).
  3. `nested same-daemon invocation with conflicting env does not deadlock and FIFO gate prevents starvation`: verified nested child with `--timeout 500` exits `124` without wedging parent daemon, nested child without `--timeout` fails fast with `deadlock` diagnostic, and staggered `A1`/`B1`/`A2` requests execute strictly in `A1|B1|A2` FIFO order.
  4. `wedged timeout does not kill healthy concurrent sibling and reports named session generation loss`: verified 1800 ms cooperative sibling A completes with exit `0` + `SIBLING_A_OK` while script B wedges at 300 ms, script C is served immediately by the replacement PID, retired PID exits cleanly after A finishes, `--session alpha` reports explicit generation loss advising `--reset`, and `--session alpha --reset` recovers cleanly.
  5. `runtimeconfig enables ConserveMemory and idle working set trims on Windows`: verified `fsiond.runtimeconfig.json` sets `System.GC.ConserveMemory = 9` and `FSION_TRIM_IDLE_MINUTES=0.02` measurably shrinks `WorkingSet64` during idle while keeping post-trim execution healthy.
- Full 3-OS CI (`windows-latest`, `macos-latest`, `ubuntu-latest`) runs `eng/gate.ps1` (both `net10.0` and `net11.0`) + `eng/package.ps1 -Framework net10.0` on PR #7.
