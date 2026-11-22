# Goal 0001 Evidence — Native Thin Client & Warm Reactor Vertical Slice

Contract: [../goals/archive/0001-native-cli-and-warm-reactor-slice.md](../goals/archive/0001-native-cli-and-warm-reactor-slice.md)
Implementation choices: [../plans/2026-11-22-goal-0001-implementation-choices.md](../plans/2026-11-22-goal-0001-implementation-choices.md)

## Environment

| Item | Value |
|---|---|
| Host | `CPC-lhan-XF3D0M` (Microsoft Windows NT 10.0.26300.0) |
| Base commit | `af0ad553ba517c99d7e75117a8655f56e710cb24` (goal 0001 tree uncommitted on top) |
| .NET SDK | 11.0.100 |
| Shared runtimes | `Microsoft.NETCore.App` 10.0.15 and 11.0.0 |
| `dotnet fsi` | F# Interactive 15.2.101.0 (F# 11.0) |
| `FSharp.Compiler.Service` (fsiond) | 43.13.101 |
| PowerShell | 7.6.10 |
| Python (pipe-repro harness) | 3.15.2 |

## Gate

Single runnable gate: `cd Q:/repos/fsion && pwsh -NoProfile -File eng/gate.ps1`.

Recorded run on this tree after the round-2 review fixes: **exit code 0**.

| Step | Result |
|---|---|
| `dotnet build fsion.slnx -c Release` | Build succeeded, 0 warnings, 0 errors |
| `dotnet publish src/fsion -c Release -f net10.0` | exit 0, `Generating native code`, 0 warning lines, `artifacts/publish/fsion/net10.0/fsion.exe` (3,946,496 bytes) |
| `dotnet publish src/fsion -c Release -f net11.0` | exit 0, `Generating native code`, 0 warning lines, `artifacts/publish/fsion/net11.0/fsion.exe` (3,976,192 bytes) |
| `dotnet test tests/Fsion.Tests -c Release -f net10.0` | Failed 0, Passed 36, Skipped 0, Total 36, 4 m 27 s |
| `dotnet test tests/Fsion.Tests -c Release -f net11.0` | Failed 0, Passed 36, Skipped 0, Total 36, 4 m 7 s |
| `git ls-files --eol` + untracked scan | 35 tracked and 35 non-ignored files clean, 0 CR bytes |

The forced full NativeAOT analysis for both TFMs was also captured separately
(`obj/.../win-x64` and publish outputs deleted first): 0 `warning` lines in
both publish logs.

## Clause 1 — lifecycle: auto-start, warmup, status/restart/stop, `daemon start`, `--no-daemon`

Transcript endpoint `fsion-evidence-b59a5a8d`, published `fsion` net11.0,
`FSIOND_PATH=src/fsiond/bin/Release/net11.0/fsiond.exe`.

| Command | Observed output | Exit |
|---|---|---|
| `fsion daemon status` (before any daemon) | `fsiond not running (endpoint fsion-evidence-b59a5a8d)` | 0 |
| `fsion -e 'printfn "auto-start"'` (on-demand spawn) | `auto-start` | 0 |
| `fsion daemon status` | `fsiond running (pid=44112, ..., uptimeMs=3631, idleMs=34, executed=1, warm=True)` | 0 |
| `fsion warmup` | `fsiond ready (pid=44112, endpoint=..., warm=True)` | 0 |
| `fsion daemon restart` | `fsiond started (pid=61404, ...)` | 0 |
| `fsion daemon status` (after restart) | `fsiond running (pid=61404, ..., warm=False)` | 0 |
| `fsion daemon stop` | `fsiond stopped (endpoint fsion-evidence-b59a5a8d)` | 0 |
| `fsion daemon start` | `fsiond started (pid=62232, ...)` | 0 |
| `fsion daemon stop` | `fsiond stopped (endpoint fsion-evidence-b59a5a8d)` | 0 |
| `fsion --no-daemon -e 'printfn "oneshot"'` | `oneshot` | 0 |
| `fsion daemon status` (after `--no-daemon`) | `fsiond not running (endpoint fsion-evidence-b59a5a8d)` | 0 |
| `fsion --pipe "" daemon status` | `fsion: --pipe requires a non-empty endpoint name` + usage | 1 |
| `fsion --pipe 'bad\name' daemon status` | `fsion: pipe name must not contain path separators` | 1 |
| `FSION_ONESHOT_PIPE=<name> fsion --no-daemon -e 'printfn "oneshot"'` | `oneshot` | 0 |
| `fsion --pipe <name> daemon status` (right after) | `fsiond not running (endpoint <name>)` | 0 |

Pinned by the 13 `Fsion.Tests.LifecycleTests`: auto-start with PID stability,
warmup, stop/auto-restart, restart/PID change, `--no-daemon` (also polling the
`FSION_ONESHOT_PIPE` endpoint to prove the single-shot `fsiond` is gone), idle-TTL
retirement (`--idle-ttl 0.05` retires within a 30 s deadline), a `FSIOND_LOG` path
containing spaces (exercising `quoteWindowsArgument` on the Windows spawn), `--pipe`
overriding `FSION_PIPE` with the override daemon stopped in cleanup, `--pipe` without
a value or with an empty value exiting non-zero before any daemon contact, `--pipe`
with `\` or `/` rejected before any connect, colocated binary discovery without
`FSIOND_PATH`, and `Ipc.connect` raising `TimeoutException` at its deadline for an
endpoint that never appears.

## Clause 2 — `dotnet fsi` parity

Byte comparison on `probe.fsx` (stdout + stderr + `fsi.CommandLineArgs` +
`__SOURCE_DIRECTORY__` + cwd), run as `probe.fsx one two`:

| Stream | fsion net11.0 SHA-256 | `dotnet fsi` SHA-256 | Identical |
|---|---|---|---|
| stdout | `AE800EAD241C097BD1062824670F1328541983357D5AEC70FF16E65D4FB9F07E` | same | yes |
| stderr | `294BCC0226D84AAE75F08FB099B5F94CCE1342346313399A913A57A372B4EC55` | same | yes |

Both exits were 0; fsion printed `args=[|"probe.fsx"; "one"; "two"|]`,
`src=Q:\tmp\fsion-evidence-b59a5a8d`, `cwd=Q:\tmp\fsion-evidence-b59a5a8d`.

Pinned by the 17 `Fsion.Tests.ParityTests`: file mode, file-mode argument
quoting (`a"b`, `c\d\`, `x\"y`, `tail\`, `""`), file mode in a subdirectory with
`#load`, `-e` mode against an `eval.fsx` oracle, stdin mode against a `stdin.fsx`
oracle, stdin prefixed with a UTF-8 BOM against the same file, syntax errors,
type errors (`-e` and file), missing script files, `exit` mid-file, `exit` at end
of file, `exit` in `-e` mode, negative `exit (-1)` (also asserting no
`@@FSION-EXIT` marker leaks to stderr), uncaught runtime exceptions (stack frames
normalized only for `--multiemit-` generated names), 4000-line output, and unicode
output.

Missing script files (`missing.fsx` and `missing_dir/missing.fsx`, the latter with a
nonexistent parent directory) now match `dotnet fsi --nologo` byte for byte on stdout,
stderr, and exit code: the daemon evaluates `#load @"<source as typed>"` from source
name `stdin` and rewrites the FCS location `stdin(1,1)` to `dotnet fsi`'s `stdin(0,1)`;
both print `\r\n\nstdin(0,1): error FS0078: Unable to find the file '<source>' in any of\n <cwd>\r\n\r\n`
and exit 1, and a run after the failure still succeeds on the same warm daemon.

## Clause 3 — `exit 42` keeps `fsiond` alive

`exit42.fsx` = `printfn "before"` / `exit 42` / `printfn "after"`:

- `fsion exit42.fsx` → exit code **42**, stdout `before`.
- `fsion daemon status` immediately after → `fsiond running (pid=19744, ..., executed=2, warm=True)`; the next invocation reuses the same PID.

Pinned by `Fsion.Tests.FaultTests.exit 42 returns the code while fsiond keeps serving the next invocation`.

## Clause 4 — compiler errors: stderr diagnostics, non-zero exit, daemon survives

`typeerr.fsx` = `let x : int = "nope"`:

- `fsion typeerr.fsx` → exit code **1**, stderr contains
  `typeerr.fsx(1,15): error FS0001: This expression was expected to have type 'int' but here has type 'string'`.
- `fsion daemon status` → same `pid=19744` still running (`executed=3`).

Pinned by `FaultTests.a compile error does not crash fsiond and the next invocation succeeds`
(also asserts the next invocation succeeds) and `a runtime exception does not crash fsiond...`.

## Clause 5 — no temporary `.dll` writes + warm latency

Zero-`.dll` run with `TMP`/`TEMP` pointed at a fresh watch directory on a fresh
endpoint (`fsion-evidence-hyg-*`): `warmup` + 3 fresh scripts →
`dll files under TMP watch dir: 0` and `0` in the script/working directory. Pinned
by the two `Fsion.Tests.HygieneTests` (the zero-`.dll` test checks both directories).

Warm latency on freshly generated unseen scripts (`LatencyTests`, 8 fsion rounds
vs 3 `dotnet fsi` rounds on the same host, same script text, medians, re-measured
per TFM after the round-2 review fixes):

| Runtime | fsion median | `dotnet fsi` median | Speedup | Required floor |
|---|---|---|---|---|
| net11.0 | 125.9617 ms | 2965.0916 ms | 23.54x | 5x (asserted); 20x is Goal 0005 |
| net10.0 | 121.7797 ms | 2977.4926 ms | 24.45x | 5x (asserted); 20x is Goal 0005 |

## Windows handle-inheritance hang (observed failure → fix → regression check)

- **Failure observed first (pre-fix binary):** a Python caller capturing
  `fsion`'s pipes (`subprocess.run(..., capture_output=True, timeout=25)`)
  hung; the caller's 25 s timeout could not even finish its capture cleanup.
  Process listing during the hang showed exactly one holder of the pipe write
  ends: `fsiond.exe` (PID 10620, endpoint `fsion-hangrepro-5a2a65cc08be`).
  Root cause: .NET `Process.Start` with `UseShellExecute=false` sets
  `bInheritHandles=TRUE`, so the long-lived `fsiond` inherited the caller's
  stdout/stderr pipe write handles; `ReadToEnd` stayed blocked after `fsion`
  exited.
- **Fix:** `spawnDaemon` on Windows calls Win32 `CreateProcessW` with
  `bInheritHandles=false` and `CREATE_NO_WINDOW | DETACHED_PROCESS`.
- **Regression check after the fix (both TFMs):** the identical
  redirected-pipe repro completes: net11.0 rc=0 in 4.20 s, net10.0 rc=0 in
  4.77 s, stdout exact. The xunit suite itself is the durable regression
  harness: every `TestInfra.runProcess` invocation captures redirected pipes,
  so `LifecycleTests`/`ParityTests` would time out again if inheritance
  regressed.

## Round-2 review fixes (negative exit sentinel, connect stack growth)

- **Negative `exit <n>` (before):** `fsion -e 'exit (-1)'` exited **1** and leaked
  `@@FSION-EXIT-...@@FSI_0001+FsionExit: FsionExit -1` plus the FSI stack trace to
  stderr, because `FsionFsiState.ExitCode` used `-1` as its "not exited" sentinel.
- **Negative `exit <n>` (after):** the prelude sets a separate `Exited` flag before
  writing `ExitCode`, and the daemon truncates the marker whenever `Exited` is true,
  returning **all** integer codes verbatim. `fsion -e 'exit (-1)'` now exits **-1**
  (MSYS bash reports 127) with empty stderr; `fsion exitneg.fsx` prints `pre` and also
  exits **-1**; `exit 0`, `exit 42`, and compile-error exit **1** are unchanged. Pinned
  by the parity test `negative exit returns its code without leaking the internal
  marker`, which compares against `dotnet fsi`'s signed `-1` exit code.
- **`Ipc.connect` retry loop (before):** a `dotnet fsi` harness loading `Ipc.fs` and
  calling `connect` on a nonexistent endpoint with a 60 s timeout died after 32 s with
  `Stack overflow. Repeated 93 times: ... FSI_0001.Fsion.Ipc.attempt@45 ...` and signed
  exit `-1073741571` (`STATUS_STACK_OVERFLOW`); the recursive `attempt ()` was not
  tail-optimized because it sat inside the `with` handler.
- **`Ipc.connect` retry loop (after):** rewritten as an O(1)-stack `while` loop. The
  same 60 s repro now finishes cleanly: `timeout after 60243.72 ms: fsiond endpoint
  'fsion-nonexistent-...' is not reachable`, rc 0, empty stderr. Pinned by
  `Fsion.Tests.LifecycleTests.connect raises TimeoutException when an endpoint never
  comes up` (120 ms timeout; the test project now references `Fsion.Protocol` to call
  `Ipc.connect` directly).

## Known limits

- Warm medians are ~122–126 ms, above the ~30–70 ms aspiration; the formal
  benchmark gate (>= 20x, `bun`-class latency) is Goal 0005. Current measured
  speedups are 24.45x (net10.0) and 23.54x (net11.0).
- `--multiemit-` (kept for the measured compile saving) means uncaught
  runtime-exception stack frames lack `in <file>:line N`; parity tests
  normalize only those generated frame names.
- The non-Windows spawn path (managed `Process.Start` with redirected stdio)
  is code-reviewed but not exercised; this host is Windows.
- Deferred by contract to Goals 0002–0005: collectible session pool,
  `AsyncLocal` stream isolation, `--json`, `--session`, `--timeout`,
  NuGet resolution caching, packaging/CI.
