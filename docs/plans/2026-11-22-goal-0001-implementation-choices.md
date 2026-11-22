# Goal 0001 — Implementation Choices

The accepted plan is [docs/goals/0001-native-cli-and-warm-reactor-slice.md](../goals/0001-native-cli-and-warm-reactor-slice.md),
constrained by [docs/core-design.md](../core-design.md) and
[docs/specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md).
This note only records choices those documents leave open, plus the evidence plan.

## Repo layout

| Path | Kind | Purpose |
|---|---|---|
| `src/Fsion.Protocol` | library, `net10.0;net11.0` | Wire framing/DTOs + cross-platform local IPC (named pipe on Windows, Unix domain socket elsewhere). Referenced by both executables; must stay AOT-clean. |
| `src/fsion` | NativeAOT exe | Thin CLI: parsing, daemon discovery/auto-start, IPC client, stdout/stderr/exit passthrough. |
| `src/fsiond` | CoreCLR exe | Reactor: endpoint listener, single warm `FsiEvaluationSession`, per-run console/cwd routing, exit shadow. |
| `tests/Fsion.Tests` | xunit, `net10.0;net11.0` | Black-box tests that spawn the published `fsion` and compare against `dotnet fsi`. |
| `eng/gate.ps1` | script | Runnable form of the repository gate. |

## CLI grammar (choices the goal leaves open)

```
fsion [options] <script.fsx> [args...]
fsion [options] -e <code> [args...]
fsion [options] - [args...]            # also implicit when no input and stdin is redirected
fsion [options] warmup
fsion [options] daemon <start|stop|restart|status>
```

Options: `--no-daemon`, `--pipe <name>` (env `FSION_PIPE`), `--idle-ttl <minutes>`
(env `FSION_IDLE_TTL_MINUTES`, default 30), `-h|--help`, `--version`. Options are parsed before
the input; everything after the script path, `-e <code>`, or `-` is a script argument, matching
`dotnet fsi <script.fsx> [args...]`.

## `-e` / stdin semantics

`dotnet fsi` has no `-e`; the only coherent parity oracle is "the same text as the body of a
`.fsx` file run by `dotnet fsi`". Both modes therefore evaluate like script files:

- `-e` uses source name `eval.fsx`, stdin uses `stdin.fsx`; `fsi.CommandLineArgs.[0]` is that
  name and the rest are trailing CLI args. `__SOURCE_DIRECTORY__` is the client working directory.
- Evaluation is in memory via `EvalInteraction(code + "\n()", sourceName)`. The trailing `()`
  keeps a trailing generic expression (for example a final `exit 42`) from tripping FS0030 value
  restriction, matching how script files are generalized. Documented deviation: a trailing
  `failwith` becomes a runtime error instead of FS0030 in `-e`/stdin mode.

## Daemon session (Goal 0001 scope)

One long-lived non-collectible-usage session per daemon, created lazily on first execute or
`warmup`; the collectible pool is Goal 0002. Session argv:
`--nologo --quiet --noninteractive --optimize- --multiemit- --targetprofile:netcore`.
`--multiemit-` is kept for the measured ~20 ms/run compile saving; consequence: uncaught runtime
exception stack frames are named `$FSI_000n_<file>$fsx` without `in <file>:line N` (stack-frame
text is the only non-byte-identical output; tests normalize it).

## Compatibility mechanics

- `FsiEvaluationSessionHostConfig` is built with `FsiEvaluationSession.GetDefaultConfiguration(settings, false)`
  and a private settings object exposing the reflection-read properties the default configuration
  expects. `useFsiAuxLib = false`; the prelude defines the `fsi` value:
  `FsionFsiState` with settable `CommandLineArgs`/`ExitCode`, read and written per run through
  reflection on the session-bound value (`TryFindBoundValue`).
- `exit` is shadowed in the prelude. It records the code in `FsionFsiState.ExitCode`, writes a
  random per-daemon marker to `Console.Error`, then raises. The throwing `EvalScript`/`EvalInteraction`
  APIs are used so FCS prints compiler diagnostics and exception traces byte-for-byte as
  `dotnet fsi` does; on `FsiCompilationException` the daemon reads `ExitCode` and truncates the
  error text at the marker, returning the script's code with no leaked trace.
- Per run: `Directory.SetCurrentDirectory(client cwd)`, `Console.SetOut/SetError` to per-run
  `StringWriter`s, session writers are forwarding writers so FCS output also lands in the run
  capture, restore afterwards. Runs are serialized by a semaphore.
- Missing script files never reach `EvalScript` (which would report `missing.fsx(1,1)` with the
  basename). When `File.Exists` is false, the daemon `#cd`s to the client working directory and
  evaluates `#load @"<source as typed>"` with source name `stdin`, then rewrites the FCS location
  `stdin(1,1): error FS0078` to `stdin(0,1): error FS0078` so the diagnostic text, search path,
  and exit code match `dotnet fsi --nologo missing.fsx` byte for byte; a missing parent directory
  no longer emits a spurious `FS2302` from `#cd`.

## Daemon lifecycle

- Endpoint: `fsion-<user>` sanitized; `FSION_PIPE`/`--pipe` override. Windows named pipe,
  Unix domain socket under `$XDG_RUNTIME_DIR` or the temp directory. Windows pipe names with `\`
  or `/` are rejected before any connect/spawn (`validateEndpointName`), and empty or
  whitespace-only `--pipe` values fail argument parsing.
- Auto-start: connect (~250 ms) then spawn `fsiond --endpoint <name> --idle-ttl <min>` and retry
  for up to 30 s. `FSIOND_PATH` overrides daemon discovery; otherwise `fsiond` must sit next to
  `fsion`. On Windows the client spawns the apphost via `CreateProcessW` with
  `bInheritHandles = false` (`CREATE_NO_WINDOW | DETACHED_PROCESS`); elsewhere it uses the
  redirected-stdio path, so a long-lived daemon can never hold the caller's stream handles.
- Windows singleton: named mutex. Unix singleton: live-connect probe then stale socket deletion.
- Idle TTL defaults to 30 minutes and only executions/warmups count as activity, so `status`
  polling does not keep a daemon alive. `--no-daemon` spawns a `--oneshot` daemon that exits after
  one execute; its endpoint is `--pipe <name>` when given, else `FSION_ONESHOT_PIPE`, else a unique
  `fsion-oneshot-<pid>-<hex>` name.
- `fsion daemon status` prints `pid=...`, uptime, idle, executed count and warm flag;
  `restart` = stop + start.

## Evidence plan

- Integration tests (black-box, xunit) drive the published NativeAOT `fsion` with per-test
  `FSION_PIPE`/`FSIOND_PATH` isolation and always stop their own daemon by PID.
- Parity tests compare stdout/stderr/exit byte-for-byte against `dotnet fsi` on the same host,
  using `eval.fsx`/`stdin.fsx` oracle files so names line up; runtime-exception frames compare
  after normalization.
- Hygiene test runs warm scripts with `TMP`/`TEMP` pointed at a fresh directory and asserts zero
  `*.dll` files appear; lifecycle tests assert daemon PID stability across failures and exits.
- Latency test generates a fresh unseen typical script per round in the daemon's warm state,
  compares medians against `dotnet fsi`, and asserts a >= 5x floor (Goal 0005 owns the formal
  20x gate). Numbers are recorded in `docs/evidence/`.

## Out of scope (belongs to later goals)

Collectible session pool, `AsyncLocal` stream isolation, Windows file-lock sweeping, `--json`,
`--session`, `--timeout`, drain-and-respawn, custom preludes, NuGet resolution cache, packaging,
CI, README.
