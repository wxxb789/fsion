# Goal 0003 Evidence — Named Stateful Sessions, Cooperative Timeout & Drain-and-Respawn

Contract: [../goals/archive/0003-stateful-sessions-and-runaway-recovery.md](../goals/archive/0003-stateful-sessions-and-runaway-recovery.md)
Implementation choices: [../plans/2026-11-23-goal-0003-implementation-choices.md](../plans/2026-11-23-goal-0003-implementation-choices.md)

## Environment

| Item | Value |
|---|---|
| Host | `CPC-lhan-XF3D0M` (Microsoft Windows 11 Enterprise 10.0.26300.0) |
| Base commit | `8b939fb791a12fe09087cbc6ebbadaf426bb32d1` (review-fix tree uncommitted on top) |
| .NET SDK | 11.0.100 |
| `FSharp.Compiler.Service` (fsiond) | 43.13.101 |
| FSharp.Core (fsiond / scripts) | 11.0.101 |
| PowerShell | 7.6.10 |

## Gate

Single runnable gate: `cd Q:/repos/fsion && pwsh -NoProfile -File eng/gate.ps1`.

Recorded run on this tree: **exit code 0**.

| Step | Result |
|---|---|
| `dotnet build fsion.slnx -c Release` | Build succeeded, 0 warnings, 0 errors |
| `dotnet publish src/fsion -c Release -f net10.0` | exit 0, `artifacts/publish/fsion/net10.0/fsion.exe` (4,118,016 bytes) |
| `dotnet publish src/fsion -c Release -f net11.0` | exit 0, `artifacts/publish/fsion/net11.0/fsion.exe` (4,143,104 bytes) |
| `dotnet test tests/Fsion.Tests -c Release -f net10.0` | Failed 0, Passed 60, Skipped 0, Total 60, 9 m 46 s |
| `dotnet test tests/Fsion.Tests -c Release -f net11.0` | Failed 0, Passed 60, Skipped 0, Total 60, 10 m 57 s |
| `Assert-LineEndings` | `line endings: 44 tracked and 44 non-ignored files clean` |

The only `warning` line anywhere in the gate log is the summary literal `0 Warning(s)`;
the publish steps printed no IL/trim warning lines.

Focused detailed runs, used for the per-clause transcripts below:

| Command (add `tests/Fsion.Tests/Fsion.Tests.fsproj -c Release -f <tfm>`) | Result |
|---|---|
| `--filter "FullyQualifiedName~SessionTests\|FullyQualifiedName~RecoveryTests" --logger "console;verbosity=detailed"` (net11.0) | exit 0, 6/6, 49 s |
| `--filter "FullyQualifiedName~FaultTests\|FullyQualifiedName~ParityTests"` (net11.0) | exit 0, 20/20, 3 m 1 s |
| `--filter "FullyQualifiedName~RecoveryTests" --logger "console;verbosity=normal"` (net11.0) | pre-fix exit 1, 3/5 (the two new tests failing); post-fix exit 0, 5/5, 54 s |

Baseline observed before the implementation: every new option failed argument parsing, so no
session or timeout behavior existed at `f8854ef`.

| Command (net11.0 published client) | Observed | Exit |
|---|---|---|
| `fsion --session alpha -e 1` | `fsion: unknown option: --session` + usage | 1 |
| `fsion --timeout 300 -e 1` | `fsion: unknown option: --timeout` + usage | 1 |
| `fsion --session alpha --reset -e 1` | `fsion: unknown option: --session` + usage | 1 |
| `fsion --session-ttl 0.05 warmup` | `fsion: unknown option: --session-ttl` + usage | 1 |

## Clause 1 — multi-turn stateful continuity

`SessionTests` `named sessions preserve bindings across turns and stay isolated from stateless and
other ids` (Passed [6 s net11.0 in the detailed run]; both TFMs in the gate) runs
`let data = [1 .. 1000]` under `--session alpha`, sums `data` under the same id, then proves a default
run and `--session beta` both fail with `FS0039` while the daemon pid is unchanged.

Live transcript (net11.0, endpoint `fsion-evidence-0003-1795414567`, daemon log enabled):

```
$ fsion --session alpha -e 'let data = [1 .. 1000]'                 # exit 0
$ time fsion --session alpha -e 'printfn "%d" (List.sum data)'
500500                                                              # exit 0, 0.155 s wall
$ fsion -e 'printfn "%d" (List.sum data)'                            # exit 1, FS0039 'data'
$ fsion --session beta -e 'printfn "%d" (List.sum data)'             # exit 1, FS0039 'data'
```

Daemon-side `elapsedMs` for the same sequence (from `Server.handleConnection`):

| Run | Daemon-side elapsedMs |
|---|---|
| `--session alpha` turn 1 (cold daemon + cold session claim) | 4,898 |
| `--session alpha` turn 2 (`List.sum data`) | 90 |
| default run after that (FS0039) | 142 |
| `--session beta` (FS0039) | 11 |

Turn 2 never re-created a session: `releaseSession` is skipped on a normal named return, and only the
default/beta runs claimed pooled sessions. `GetMetrics` `NamedSessions` observed `1` while the named
session lived (see clause 2).

## Clause 2 — session reset and TTL cleanup

`SessionTests` `session reset discards old bindings and keeps the recreated session usable` (Passed
[6 s]) and `session idle ttl evicts and unloads the abandoned named session` (Passed [10 s]; both TFMs in
the gate).

Live transcript (reset):

```
$ fsion --session alpha -e 'let data = 41'                    # exit 0
$ fsion --session alpha --reset -e 'let fresh = 7
printfn "%d" fresh'                                            # prints 7, exit 0
$ fsion --session alpha -e 'printfn "%d" data'                 # exit 1, FS0039 'data'
$ fsion --session alpha -e 'printfn "%d" fresh'                # prints 7, exit 0
```

TTL: the daemon is auto-started with `--session-ttl 0.05` (3 s); after defining a binding, the test
polls `GetMetrics` until the named entry is evicted, then checks that the binding is gone and the
collectible session actually unloaded:

```
named sessions after ttl: 0, scriptAssemblies=3, poolTarget=3, aliveDisposedSessions=0
$ fsion --session alpha -e 'printfn "%d" (List.sum data)'       # exit 1, FS0039 'data'
```

The daemon pid is unchanged across the TTL expiry (the daemon retires only on its own idle TTL, not
on a named-session TTL). `NamedSessions` returned to 0 because the sweeper removed and released the
entry; `ScriptAssemblies = PoolTarget = 3` and `AliveDisposedSessions = 0` prove the unload ran
through the existing `CleanupWork` drain.

## Clause 3 — cooperative timeout in place

`RecoveryTests` `cooperative async timeout cancels in place with exit 124 and keeps the daemon alive`
(Passed [7 s]; both TFMs in the gate). Script:
`Async.RunSynchronously(Async.Sleep 10000, cancellationToken = fsionFsiState.CancellationToken)`.
The deadline is armed only after the session is acquired (and immediately before `evaluate`), so a
cold daemon/session boot is never charged against `--timeout`; the review fix round below covers the
cold-daemon and busy-gate paths.

Live transcript (net11.0; daemon pid 59504 before and after):

```
$ time fsion --json --timeout 300 -e 'Async.RunSynchronously(Async.Sleep 10000, cancellationToken = fsionFsiState.CancellationToken)'
{"exitCode":124,"stdout":"","stderr":"System.OperationCanceledException: The operation was canceled.
 ...
fsion: script timed out after 300 ms
","timedOut":true,"elapsedMs":318.5698,"diagnostics":[]}
exit 124, 0.379 s wall
$ fsion daemon status
fsiond running (pid=59504, ... executed=7, warm=False)
```

Daemon log line for the cancellation: `executed exit=124 timedOut=True wedged=False elapsedMs=315`.
`fsion daemon status` before and after reports the same pid, and the immediately following
`fsion -e 'printfn "alive"'` succeeds, so the cooperative path never retires the daemon.

`fsionFsiState.CancellationToken` is set for the run on the prelude state and the same
`CancellationToken` is passed to `EvalInteraction(..., cancellationToken = ...)`. FSharp.Core 11's
`Async.DefaultCancellationToken` is a single process-global `CancellationTokenSource`
(`AsyncPrimitives.defaultCancellationTokenSource` returns the static `$Async` field; the
`RunSynchronously` IL reads it when no explicit token can be cancelled), so the daemon deliberately
does not swap or cancel it per run; the documented cooperative pattern passes the exposed token
explicitly.

## Clause 4 — uncooperative infinite-loop drain-and-respawn

`RecoveryTests` `uncooperative infinite loop drains the daemon and the next call starts a fresh
generation` (Passed [12 s]; both TFMs in the gate; test-recorded wall time 737 ms).

Live transcript (net11.0, same endpoint daemon pid 59504):

```
$ fsion --timeout 400 -e 'while true do ()'
fsion: script timed out after 400 ms
exit 124, 694 ms wall
$ fsion -e 'printfn "%d" (1 + 1)'
2                                                               # exit 0
$ fsion daemon status
fsiond running (pid=53488, ... executed=1, warm=True)
$ (after old pid check) old daemon terminated
```

Daemon log for the draining generation:

```
executed exit=124 timedOut=True wedged=True elapsedMs=588
spawned a replacement fsiond generation
listening on fsion-evidence-0003-1795414567        # the replacement generation
```

The piped timeout response is the one produced by the wedged worker's caller before the worker
returned; the old generation stopped its listener, released endpoint ownership, sent the response,
spawned the replacement, and the scheduled self-kill bounded its lifetime. The old pid was gone within
2 s and the replacement pid served the next call. `--no-daemon --timeout 300 -e 'while true do ()'`
also returns exit 124 (648 ms wall) and leaves its unique one-shot endpoint unreachable without
respawning a daemon.

## Clause 5 — client disconnect cancels a cooperative wait in place

`RecoveryTests` `client disconnect cancels a cooperative wait without retiring the daemon` (Passed
[8 s]; both TFMs in the gate). The test starts a `--session alpha` client whose script writes a marker
file and then waits on `fsionFsiState.CancellationToken`, kills the client once the marker exists,
and checks that the daemon pid is unchanged and the next turn on that session succeeds:

```
pid1=50072
marker seen: yes
client killed
pid2=50072                      # same daemon: the run was cancelled in place
fsion --session alpha -e 'printfn "still here"'   -> exit 0
pid3=50072
```

The connection handler holds a pending 1-byte `ReadAsync` on the connection; EOF cancels the run's
`CancellationTokenSource`, and an uncooperative disconnect takes the same wedged path as a timeout.

## Review fix round (R-01–R-03)

The review of `8b939fb` found that `--timeout` armed before session acquisition (falsely charging cold
session boot and draining healthy daemons), that the client deadline (`timeoutMs + 2000`) cut cold
starts short, and that the TTL test's assembly assertion allowed one leaked `FSI-ASSEMBLY`. All three
were fixed in this round; the gate above ran after the fix.

### R-01 — the script deadline starts after session acquisition

`SessionHost.Execute` now claims the session first (`claimSession disconnectToken`; `entry.Session <-
Some(claimSession disconnectToken)` on a named session's first turn), arms the per-evaluation `Timer`
immediately before `evaluate`, and tracks `evaluationStarted` (set before `evaluate`, reset in its
`finally`). A busy named session's `entry.Gate.Wait(remaining, disconnectToken)` honors the deadline;
an expired wait returns exit 124 + `timedOut` in place with `Wedged = false`. Only a run that entered
evaluation and did not return within the 150 ms grace window is wedged, so acquisition can never
drain a healthy daemon. The client's `receive.Wait` safety net is now `timeoutMs + 30000` ms because
`fsiond` itself enforces the script deadline after acquisition.

Observed before/after on a brand-new auto-started daemon with `--timeout 500`:

| Command | Before (pre-fix) | After (fixed) |
|---|---|---|
| `fsion --timeout 500 -e 'printfn "%d" (1 + 1)'` (cold daemon) | exit 124; daemon drained and respawned | exit 0, prints `2`; 6,830 ms wall; daemon pid unchanged |
| second warm call on the same daemon | — | exit 0; 579 ms wall; same pid |

Daemon log for the queued-gate path (`--session alpha` busy with a cooperative 3 s sleep, concurrent
`--session alpha --timeout 250`):

```
executed exit=124 timedOut=True wedged=False elapsedMs=262     # queued turn timed out on the gate
executed exit=0 timedOut=False wedged=False elapsedMs=3084     # busy turn completed normally
```

Same daemon pid before/after (42164) and zero `replacement` log lines: the queued timeout neither
drained the daemon nor disturbed the busy turn.

### R-02 — new black-box coverage

Two tests were added to `tests/Fsion.Tests/RecoveryTests.fs` and observed failing before the R-01 fix:

- `a short --timeout on a freshly auto-started cold daemon runs a fast script without draining the
  daemon`: pre-fix first call exit 124 (expected 0); post-fix exit 0, same daemon pid.
- `a --timeout waiting behind a busy named session cancels in place without draining the daemon`:
  pre-fix the queued call returned 124 but the busy turn got exit 1 and the daemon respawned (queued
  wall 498 ms); post-fix queued exit 124 in 449 ms, busy turn exit 0, same daemon pid.

### R-03 — tightened TTL unload assertion

`SessionTests` TTL now polls for depletion and asserts
`Assert.Equal(settled.PoolTarget, settled.ScriptAssemblies)` (the previous `<= PoolTarget + 1`
allowed one leaked `FSI-ASSEMBLY`). The
tightened assertion already passed against the pre-fix tree (observed 3 = 3) and after the fix, so the
unload was exact; the test now fails directly on any leak.

## Known limits

- FCS 43.13.101 `EvalScript` has no `CancellationToken` overload, so file-mode scripts rely on the
  prelude token being observed at runtime; `-e`/stdin interactions also pass the token into the
  compiler, which can abort a long typecheck cooperatively.
- FSharp.Core 11's process-global `Async.DefaultCancellationToken` is never mutated; a script that
  calls `Async.RunSynchronously` without passing `fsionFsiState.CancellationToken` cannot be
  cancelled cooperatively and will hit the drain-and-respawn path instead.
- The drain-and-respawn path inherits the draining daemon's *current* process environment (which may
  be the wedged run's environment snapshot) in the replacement generation; every explicit daemon
  option (`--endpoint`, `--idle-ttl`, `--session-ttl`, `--pool-size`, `--log`) is re-passed on the
  replacement command line.
- The wedge test asserts the timeout outcome within 4 s of wall time; measured 694–737 ms on this
  host. The drained daemon self-kill backstop is 1 s after detach, with process exit normally
  earlier via the accept-loop drain.
- `LifecycleTests` `daemon log path containing spaces...` reads the log immediately after the client
  returns; the daemon's append can overlap that read on Windows (share-mode violation). The test now
  retries the read for up to 10 s, which keeps the same content assertion. This flake existed before
  0003 and was observed once in the first net10.0 gate run of this goal.
- Non-Windows code paths (Unix domain socket, drain/ownership release, replacement spawn) are
  code-reviewed but not exercised; this host is Windows.
