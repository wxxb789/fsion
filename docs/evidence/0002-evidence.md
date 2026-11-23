# Goal 0002 Evidence — Collectible Fission Pool, Multi-Threaded Isolation & Structured Diagnostics

Contract: [../goals/archive/0002-collectible-fission-pool-and-isolation.md](../goals/archive/0002-collectible-fission-pool-and-isolation.md)
Implementation choices: [../plans/2026-11-22-goal-0002-implementation-choices.md](../plans/2026-11-22-goal-0002-implementation-choices.md)

## Environment

| Item | Value |
|---|---|
| Host | `CPC-lhan-XF3D0M` (Microsoft Windows 11 Enterprise 10.0.26300.0) |
| Base commit | `de3468eaa950e261c3649758e22f992be8bcfec8` (goal 0002 tree uncommitted on top) |
| .NET SDK | 11.0.100 |
| `dotnet fsi` | F# Interactive 15.2.101.0 (F# 11.0) |
| `FSharp.Compiler.Service` (fsiond) | 43.13.101 |
| PowerShell | 7.6.10 |

## Gate

Single runnable gate: `cd Q:/repos/fsion && pwsh -NoProfile -File eng/gate.ps1`.

Recorded run on this tree: **exit code 0**.

| Step | Result |
|---|---|
| `dotnet build fsion.slnx -c Release` | Build succeeded, 0 warnings, 0 errors |
| `dotnet publish src/fsion -c Release -f net10.0` | exit 0, `artifacts/publish/fsion/net10.0/fsion.exe` (4,102,144 bytes) |
| `dotnet publish src/fsion -c Release -f net11.0` | exit 0, `artifacts/publish/fsion/net11.0/fsion.exe` (4,127,744 bytes) |
| `dotnet test tests/Fsion.Tests -c Release -f net10.0` | Failed 0, Passed 51, Skipped 0, Total 51, 8 m 17 s |
| `dotnet test tests/Fsion.Tests -c Release -f net11.0` | Failed 0, Passed 51, Skipped 0, Total 51, 8 m 7 s |
| `Assert-LineEndings` | `line endings: 40 tracked and 40 non-ignored files clean` |

The only `warning` line anywhere in the gate log is the summary literal `0 Warning(s)`;
the publish steps printed no IL/trim warning lines.

After the evidence refresh (only `.md` content changed after the gate), the same
`Assert-LineEndings` logic was re-run on the settled tree: `line endings: 40 tracked and 40
non-ignored files clean`, 0 CR bytes.

Focused detailed runs, used for the per-clause transcripts below:

| Command (add `tests/Fsion.Tests/Fsion.Tests.fsproj -c Release -f <tfm>`) | Result |
|---|---|
| `--filter "FullyQualifiedName~IsolationTests\|FullyQualifiedName~JsonTests\|FullyQualifiedName~LatencyTests" --logger "console;verbosity=detailed"` (net10.0) | exit 0, 16/16, 3 m 24 s |
| same (net11.0) | exit 0, 16/16, 2 m 57 s |

## Clause 1 — stateless fission isolation

Recorded baseline (measured while the design was settled, see the implementation-choices
note): goal 0001 reuses one session, so after a run defining `let secret = 123`, the run
`printfn "%d" secret` printed `123` and exited 0.

| Command | Observed | Exit |
|---|---|---|
| `IsolationTests` `stateless runs do not leak let bindings across invocations` (both TFMs, detailed) | Passed [8 s net10.0 / 7 s net11.0]; run 1 `-e 'let secret = 123'` exit 0, run 2 `-e 'printfn "%d" secret'` exit **1**, stderr contains `FS0039`, daemon pid still reported | 0 |
| `JsonTests` `unbound values emit FS0039 without leaking earlier bindings` (both TFMs, detailed) | Passed [9 s net10.0 / 7 s net11.0]; `--json` run 2 has `exitCode: 1` and one diagnostic `code: "FS0039"`, `number: 39` | 0 |

Live `--json` transcript of the second run (net11.0, endpoint `fsion-evidence-0002-1795399844`):

```json
{"exitCode":1,"stdout":"","stderr":"\r\n\neval.fsx(1,14): error FS0039: The value or constructor 'secret' is not defined. ...","elapsedMs":392.3134,"diagnostics":[{"code":"FS0039","number":39,"severity":"error","message":"The value or constructor 'secret' is not defined. ...","file":"eval.fsx","startLine":1,"startColumn":13,"endLine":1,"endColumn":19}]}
```

Every default `Execute` claims a pooled session and releases it in `SessionHost.releaseSession`
before the response is sent — disposing the `FSI-ASSEMBLY` static `IDisposable` bindings and
running a Gen-0 finalizer sweep — while the collectible unload (checker caches, session dispose,
full sweep) drains on the replenisher thread; nothing is carried across runs.

## Clause 2 — concurrent stream isolation

| Command | Observed | Exit |
|---|---|---|
| `IsolationTests` `four concurrent invocations keep their own stdout and stderr lines` (both TFMs, detailed) | Passed [10 s net10.0 / 9 s net11.0]; 4 processes, 10 interleaved stdout + 10 stderr lines each, byte-exact per tag | 0 |
| `IsolationTests` `concurrent invocations from different directories see their own working directory` (both TFMs, detailed) | Passed [6 s both TFMs]; two concurrent processes print their own normalized cwd | 0 |

Live transcript (`artifacts/publish/fsion/net11.0/fsion.exe`, per-run `FSION_PIPE`,
`FSIOND_PATH=src/fsiond/bin/Release/net11.0/fsiond.exe`): four background `fsion -e` runs
interleaving `printfn "out-<tag>-%d"` / `eprintfn "err-<tag>-%d"` 10 times each:

```
tag=a out_lines=10 err_lines=10 foreign_out=0 foreign_err=0
tag=b out_lines=10 err_lines=10 foreign_out=0 foreign_err=0
tag=c out_lines=10 err_lines=10 foreign_out=0 foreign_err=0
tag=d out_lines=10 err_lines=10 foreign_out=0 foreign_err=0
```

`foreign_*` counts lines that are not exactly `out-<tag>-N` / `err-<tag>-N`. The four runs
share one process-state epoch (`ProcessStateGate`), so they execute concurrently; each
connection's `Ambient` `AsyncLocal` streams make cross-talk impossible by construction.

## Clause 3 — Windows file-lock release

Recorded baseline: after 0001 returned from a script that opened a `StreamReader`/`FileStream`
without `use`/`.Dispose()`, the caller's `os.remove` failed with `WinError 32` (recorded in the
implementation-choices note).

| Command | Observed | Exit |
|---|---|---|
| `IsolationTests` `undisposed file handles from a throwaway script are released before fsion returns` (both TFMs, detailed) | Passed [9 s net10.0 / 6 s net11.0]; `File.Delete` on both the read and the write target succeeds immediately after `fsion` exits; daemon pid survives | 0 |

Live transcript (net11.0, `leaked.fsx` opening an undisposed `StreamReader` and `FileStream`):

```
"$FSION" leaked.fsx          -> exit 0
mv lock-read.txt renamed-ok.txt -> OK
rm renamed-ok.txt lock-write.txt -> OK
```

`SessionHost.releaseSession` runs on the request thread before the response: it disposes every
`IDisposable` `FSI-ASSEMBLY` static binding (the test's top-level
`let leakedReader = new StreamReader(...)` and `let leakedWriter = new FileStream(...)`), clears
the diagnostics `AsyncLocal`s, and runs `GC.Collect(0, Forced, true, false)` +
`WaitForPendingFinalizers` for throwaway function-local streams. The remaining unload —
`tcConfigB` callback severing, checker-cache clearing, collectible session dispose, and removal
of the process-wide handlers — is queued as `CleanupWork` and drained by the background
replenisher (which also runs with `--pool-size 0`); `GetMetrics` drains it synchronously before
sampling. The Windows locks are already gone when `fsion` returns because the static bindings
are disposed synchronously, and the daemon pid survives.

## Clause 4 — collectible memory stability

`IsolationTests` `fifty unique scripts keep the daemon post-GC heap bounded and unload script
assemblies` (Passed [49 s net10.0 / 44 s net11.0]) runs 10 unique scripts, samples `GetMetrics`,
then 40 more unique scripts and samples again. Measured values:

| TFM | post-GC heap before → after | Δ over 40 runs | loaded `FSI-ASSEMBLY` | `AliveDisposedSessions` | `ColdClaims` | `PoolWarm` |
|---|---|---|---|---|---|---|
| net10.0 | 114,466,536 → 126,026,800 B | +11,559,464 B | 3 → 3 | 0 | 12 | 3 |
| net11.0 | 115,598,960 → 114,669,256 B | −929,704 B | 3 → 3 | 0 | 11 | 3 |

Assertions in the test: daemon pid unchanged, `Executed` advances by exactly 40, heap growth
< 64 MiB, post-GC heap < 400 MiB, loaded script assemblies ≤ `PoolTarget + 1`, and
`AliveDisposedSessions = 0` (a `WeakReference` to every disposed session is checked after the
final forced collections).

Measured hazard the cleanup counters (recorded in the implementation-choices note, FCS
43.13.101): a disposed collectible session stayed rooted by `AppDomain.ProcessExit`,
`AssemblyLoadContext.ResolvingUnmanagedDll`/`AssemblyResolve`, the diagnostics `AsyncLocal`s,
and the `TcConfigBuilder` callback closure — 30 create/dispose rounds grew the managed heap
~27.3 MB/round (41 → 831 MB) without the cleanup and stayed 41.0 → 44.6 MB with it.

## Clause 5 — structured `--json` contract

`JsonTests` (7/7 passed in both detailed runs, 6–9 s each; both TFMs in the gate): success
envelope with `stdout`/`stderr`/`elapsedMs`/empty diagnostics; unicode and newline
round-trip; `FS0001` with exact span; `FS0039`; file-mode error with the script name; runtime
failure still one valid document; compile warning before a runtime failure executes the script
once.

Live transcripts (net11.0, endpoint `fsion-evidence-0002-1795399844`):

| Invocation | JSON document (abridged) | Exit |
|---|---|---|
| `fsion --json -e 'printfn "hello"; eprintfn "oops"'` | `{"exitCode":0,"stdout":"hello\r\n","stderr":"oops\r\n","elapsedMs":5658.0017,"diagnostics":[]}` | 0 |
| `fsion --json -e 'let x : int = "nope"'` | `{"exitCode":1,...,"diagnostics":[{"code":"FS0001","number":1,"severity":"error",...,"file":"eval.fsx","startLine":1,"startColumn":14,"endLine":1,"endColumn":20}]}` | 1 |
| `fsion --json -e 'printfn "%d" secret'` | `{"exitCode":1,...,"diagnostics":[{"code":"FS0039","number":39,"severity":"error",...,"file":"eval.fsx","startLine":1,"startColumn":13,"endLine":1,"endColumn":19}]}` | 1 |
| `fsion --json bad.fsx` | `{"exitCode":1,"stdout":"","diagnostics":[{"code":"FS0001",...,"file":"Q:\\repos\\fsion\\artifacts\\evidence-0002\\bad.fsx","startLine":2,"startColumn":14,"endLine":2,"endColumn":20}]}` | 1 |
| `fsion --json -e` (appends `"once"` to a marker file, emits a warning, then `failwith "boom"`) | `{"exitCode":1,...,"diagnostics":[]}`; marker file still exactly `once\n` (the script ran once, not twice) | 1 |

Notes: `elapsedMs` is client-observed wall time (5658 ms for the auto-started cold daemon
run and 2224 ms when the run paid a cold session claim in this capture; current warm `-e`
medians are in clause 6). `startColumn` in JSON
is FCS's 0-based column (`14`) while the rendered text says `eval.fsx(1,15)`; the tests pin
both. `Utf8JsonWriter` escapes `'` as `\u0027`, which `JsonDocument.Parse` accepts. The
document is written with no trailing newline, so `--json` stdout is exactly one JSON object.
For `FS0001`/`FS0039`, the throwing `Eval*` call raises `FsiCompilationException` with
`ErrorInfos = None` in FCS 43.13.101, and no structured diagnostics come back, so for `--json`
clients (`ExecuteRequest.IncludeDiagnostics`) whose text contains a `: error FS\d+:` line the
daemon re-runs the source once through the non-throwing API with console output redirected to a
discard writer; a compile error never executed user code, and a warning before a runtime failure
never triggers the re-run.

## Clause 6 — warm pool latency

`IsolationTests` `spaced fresh scripts claim warm sessions without cold claims` (Passed
[19 s net10.0 / 13 s net11.0]) waits until `Metrics.PoolWarm >= 1` before each of 5 freshly
generated, unseen `-e` scripts:

| TFM | run wall times (ms) | median (ms) | `PoolTarget` | `ColdClaims` | `Executed` |
|---|---|---|---|---|---|
| net10.0 | 266.9141, 118.9179, 105.9747, 124.2625, 276.3215 | 124.2625 | 3 | 0 | 5 |
| net11.0 | 172.9895, 82.1001, 90.5482, 98.0793, 92.5377 | 92.5377 | 3 | 0 | 5 |

`ColdClaims = 0` proves those runs took pre-warmed sessions from the pool and paid no
session-creation cost on the caller's critical path. Whole-invocation latency against
`dotnet fsi` on fresh unseen scripts (`LatencyTests`, 8 fsion rounds vs 3 fsi rounds):

| TFM | fsion median (ms) | `dotnet fsi` median (ms) | speedup | floor asserted by the test |
|---|---|---|---|---|
| net10.0 | 416.1275 | 3628.799 | 8.72x | 5x |
| net11.0 | 538.0204 | 4047.2505 | 7.52x | 5x |

Pool watermark configuration is live-verified (`fsiond` + `fsion daemon status`):

| Setup | Observed | Exit |
|---|---|---|
| `fsiond --pool-size 1` + `fsion warmup` | `fsiond ready (..., warm=True)`; status `warm=True` | 0 |
| `fsiond --pool-size 2` + `fsion warmup` | Win32 command line `fsiond.exe --pool-size 2`; status `warm=True` | 0 |
| `fsiond --pool-size 0` / `FSION_POOL_SIZE=0` + `fsion warmup` | `fsiond ready (..., warm=False)`; status stays `warm=False` (pool disabled) | 0 |

## Known limits

- The hot-path split keeps only the `IDisposable` static-binding disposal, diagnostics
  `AsyncLocal` clearing, and a Gen-0 finalizer sweep on the caller's critical path; the
  checker-cache clearing, session dispose, handler removal, and full `GC.Collect(2, ...)`
  sweep now run on the background replenisher (`GetMetrics` drains them synchronously), so the
  clause-6 spaced `-e` medians are 92.5–124.3 ms here versus ~350–515 ms before the split.
  Back-to-back `LatencyTests` rounds outpace the single replenisher (each round queues one
  cleanup batch, and a cold claim can wait for the drain plus a new session), so those medians
  stay 416–538 ms. The formal `>= 20x` benchmark gate remains Goal 0005; this goal's asserted
  floor is 5x, measured 7.52–8.72x.
- Rapid back-to-back runs can still claim cold sessions while the single replenisher refills
  (`ColdClaims = 11–12` across the 50-run loop); spaced runs claim warm (clause 6). A run never
  stalls behind replenishment by design.
- Eval-mode top-level syntax errors (`FS0010`/`FS0058`) print the compiler diagnostic to
  stderr but return exit 0 with `--json` `diagnostics: []`: FCS `EvalInteraction` returns
  without throwing for this input, and the `EvalInteraction(source + "\n()")` /
  `FsiCompilationException` catch structure is unchanged from goal 0001, so this is
  pre-existing rather than a 0002 regression. File-mode syntax errors still match
  `dotnet fsi` byte for byte (`ParityTests`) and `FS0001`/`FS0039` exit 1 in both modes.
  No test pins the eval-mode syntax-error exit code; a follow-up goal should.
- Non-Windows code paths (Unix domain socket, macOS/Linux) are code-reviewed but not
  exercised; this host is Windows.
