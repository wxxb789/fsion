# fsion

`fsion` runs short, freshly generated F# scripts at native-tool latency. It is a fast
`dotnet fsi` replacement built for coding-agent Code Mode workflows and everyday `.fsx`
scripting — especially on Windows, where `dotnet fsi` pays roughly 2.6 seconds of
startup per invocation on loading `FSharp.Compiler.Service`, scanning SDK reference
assemblies, and importing `FSharp.Core`.

A NativeAOT thin client (`fsion`) talks over a private local IPC endpoint to a warm
CoreCLR reactor daemon (`fsiond`) that keeps a pool of pre-warmed, collectible
`FSharp.Compiler.Service` sessions. Stateless runs claim a warm session in ~0 ms,
evaluate the script in memory, and unload the collectible session; named sessions keep
bindings alive across agent turns.

## Measured performance

Goal 0006 measured the Windows `win-x64` package on `CPC-lhan-XF3D0M`: 30 interleaved rounds
per workload after three warmups. Every round generated fresh F# / TS / JS / Python source and
verified byte-identical stdout across all five runners. The table shows process wall time in ms.

| Workload | `fsion` median / p95 | `dotnet fsi` median | `bun` median | `node` median | `python` median |
|---|---:|---:|---:|---:|---:|
| `hello` (formatted write) | **49.0 / 193.6** | 3059.0 | 53.6 | 76.2 | 48.1 |
| `typical` (JSON parse, env, filter/sort, formatted stdout) | **69.6 / 548.4** | 3246.4 | 53.4 | 76.6 | 69.9 |
| `diverse` (rotating F# constructs) | **50.3 / 76.5** | 3253.9 | 53.7 | 79.4 | 48.9 |

`fsion` was 62.4x, 46.6x, and 64.6x faster than `dotnet fsi` at the median, respectively.
It beat `bun` by 4.6 ms on `hello` but was 1.304x `bun` on `typical`, missing the separate
AC-07 `<= 1.00x` criterion. A separate 12-sample logged `typical` probe measured medians of
72.8 ms wall, 36.3 ms client + IPC, 7.9 ms daemon claim/gate/release, and 26.8 ms FCS
compile + execution. The phase medians need not sum to the wall-time median.

In the 12-script zero-gap burst, `fsion` measured 66.7 ms median and 215.9 ms max, down from
109.9 ms and 2,366.2 ms on the same-harness baseline. The packaged daemon added zero cold
claims and reported zero live disposed sessions after idle. The `bun` burst measured
54.9 ms median and 188.9 ms max. All nine Goal 0006 thresholds passed.

After the build and test gate has finished, run `python bench/run-bench.py` from the repository
root. It writes the [Goal 0006 report](docs/evidence/0006-benchmark-report.md) and
[raw results](docs/evidence/0006-benchmark-results.json) without changing the historical
Goal 0005 files. Exit 0 means the run completed and output parity held; check the threshold
verdicts separately. See [Goal 0006 evidence](docs/evidence/0006-evidence.md) for the
same-harness baseline and exact packaged command. Never run the benchmark concurrently with
the test suite.

## Architecture

```
fsion  (NativeAOT, no CLR startup)          fsiond  (CoreCLR reactor)
  arg parsing, daemon discovery               named pipe (Windows) / Unix socket
  ── ExecuteRequest ───────────────────▶      pre-warmed collectible FSI session pool
  ◀── ExecutionResult / Response ────         AsyncLocal stdout/stderr routing
  raw stream + exit-code passthrough          per-request cwd/env snapshot
  --json structured diagnostics               prelude + NuGet directive cache
```

Design laws live in [`docs/core-design.md`](docs/core-design.md); the converged scope and
black-box testing seam are in
[`docs/specs/2026-11-22-fsion-core-design.md`](docs/specs/2026-11-22-fsion-core-design.md).

- **Zero disk-emitted assemblies on the hot path.** Scripts are compiled and executed in
  memory (`--optimize- --multiemit-`), so Windows Defender never scans a per-run `.dll`.
- **Collectible pool with background replenishment.** `fsiond` defaults to 12 warm sessions per retained prelude
  (`--pool-size`, `FSION_POOL_SIZE`) that already imported core BCL metadata and the
  `Fsion` prelude; the pool refills in the background while the agent does other work.
- **Stateful named sessions.** `--session <id>` persists bindings across calls until
  `--reset` or the idle session TTL; default runs stay isolated and stateless.
- **Fault containment.** `--timeout <ms>` cancels cooperatively first; a wedged
  synchronous loop drains the endpoint and a fresh daemon generation takes over.
- **Agent-friendly output.** Default mode is byte-faithful `stdout`/`stderr`/exit-code
  passthrough; `--json` adds structured diagnostics with file/line/column.

## Install

Requirements: building the multi-targeted solution (`net10.0;net11.0`) from source requires
the .NET 11 SDK (plus the .NET 10 runtime when running `net10.0` tests) and, for the
NativeAOT client, a C/C++ toolchain for the host OS (Visual Studio 2022+/Build Tools on
Windows, `clang` + `zlib1g-dev` on Linux, Xcode Command Line Tools on macOS). Running a
pre-packaged bundle requires only the matching .NET 10 or .NET 11 runtime for `fsiond`
(`fsion` itself is NativeAOT).

```powershell
git clone <this repository>; cd fsion
dotnet build fsion.slnx -c Release
dotnet publish src/fsion -c Release -f net10.0    # NativeAOT client
# daemon: dotnet build already produced src/fsiond/bin/Release/net10.0/fsiond(.exe)
```

`fsion` looks for `fsiond` next to its own binary; set `FSIOND_PATH` to override. The
daemon is framework-dependent and needs the matching .NET runtime installed.

If the NativeAOT publish fails with `'vswhere.exe' is not recognized` (ILCompiler
captures the Visual Studio locator's stderr), prepend
`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to `PATH` for that shell;
`eng/gate.ps1` and `eng/package.ps1` do this automatically.

### Packaging a distribution bundle

```powershell
pwsh -NoProfile -File eng/package.ps1 -Framework net10.0
```

Publishes the NativeAOT client and the framework-dependent ReadyToRun daemon side by side into
`artifacts/package/fsion-<rid>-<tfm>/`, archives it (`.zip`/`.tar.gz`), and runs the
bundle end to end from a fresh temporary directory (warmup, script file, `Fsion` helpers,
exit-code passthrough, `--json` diagnostics, daemon stop). `-Runtime <win-x64|linux-x64|osx-arm64>`
defaults to the host RID; NativeAOT cannot cross-compile to another OS, so run the script
on each OS (the 3-OS CI matrix does exactly that). ReadyToRun applies to this host-RID bundle;
ordinary builds keep the standard framework-dependent daemon.

### 3-OS CI

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs `windows-latest`,
`macos-latest`, and `ubuntu-latest`, each executing the repository gate (build, NativeAOT
publish for both TFMs, black-box tests for both TFMs, LF hygiene), the packaging smoke
test, and uploads the distribution archive.

## Usage

```powershell
fsion script.fsx arg1 arg2        # run a script file
fsion -e 'printfn "hello"'        # run inline code
'printfn "hi"' | fsion -          # run code from stdin
fsion warmup                      # start the daemon and fill the warm pool
fsion daemon start|stop|restart|status
fsion --json script.fsx           # structured result + compiler diagnostics
fsion --session alpha -e 'let x = 41'
fsion --session alpha -e 'printfn "%d" (x + 1)'   # prints 42
fsion --timeout 2000 slow.fsx     # cooperative cancel, exit 124 on timeout
```

### CLI reference

| Flag | Meaning |
|---|---|
| `<script.fsx> [args...]` | Run a script file; `args[0]` is the script path (`fsi.CommandLineArgs` parity) |
| `-e <code> [args...]` | Run inline code; `args[0]` is `eval.fsx` |
| `- [args...]` | Run code read from redirected stdin; `args[0]` is `stdin.fsx` |
| `warmup` | Start the daemon and wait for the pool to fill (`fsiond ready ... warm=True`) |
| `daemon <start\|stop\|restart\|status>` | Explicit daemon lifecycle management |
| `--no-daemon` | Run a single-shot daemon with one session (cannot combine with `--session`) |
| `--json` | Emit one JSON document: `exitCode`, `stdout`, `stderr`, `timedOut`, `elapsedMs`, `diagnostics[]` |
| `--session <id>` | Bind the run to a named stateful session (created on first use) |
| `--reset` | Discard the named session before this run (requires `--session`) |
| `--timeout <ms>` | Cancel a run after its execution deadline; cooperative scripts use `fsionFsiState.CancellationToken`; exit 124 |
| `--prelude <path>` | Evaluate this `prelude.fsx` in warm sessions (env: `FSION_PRELUDE`) |
| `--no-prelude` | Skip the built-in `Fsion` module and the user prelude |
| `--pipe <name>` | Daemon endpoint name (env: `FSION_PIPE`, default `fsion-<user>`) |
| `--idle-ttl <minutes>` | Auto-started daemon idle TTL (env: `FSION_IDLE_TTL_MINUTES`, default 30) |
| `--session-ttl <min>` | Named-session idle TTL (env: `FSION_SESSION_TTL_MINUTES`, default 15) |
| `--pool-size <n>` | Warm session target per retained prelude for the auto-started daemon; `0` disables warm sessions (env: `FSION_POOL_SIZE`, default 12) |
| `-h`, `--help`, `--version` | Help / version |

Other environment variables: `FSIOND_PATH` (daemon binary), `FSION_CONFIG_DIR` (default
prelude directory), `FSION_CACHE_DIR` (NuGet resolution cache), `FSIOND_LOG` (daemon log
file).

### Built-in `Fsion` helpers

Warm sessions preload a small zero-dependency module: `Fsion.cwd ()`, `Fsion.path rel`,
`Fsion.env name`, `Fsion.envOr name fallback`, `Fsion.readText path`,
`Fsion.writeText path content`, and `Fsion.json text` (a parsed `JsonDocument`). Core BCL
namespaces (`System`, `System.IO`, `System.Text`, `System.Text.Json`,
`System.Text.RegularExpressions`, `System.Net.Http`, `System.Linq`) are imported at pool
build time so scripts using them do not pay the first-use import cost.

### User prelude

A `prelude.fsx` evaluated once per warm session (not per run) can define helpers, `open`
namespaces, or reference packages with `#r "nuget: ..."`. Resolution order: `--prelude`
flag → `FSION_PRELUDE` → `<FSION_CONFIG_DIR>/prelude.fsx` → `%APPDATA%/fsion/prelude.fsx`
on Windows (`$XDG_CONFIG_HOME/fsion/prelude.fsx` or `~/.config/fsion/prelude.fsx` elsewhere)
→ built-in prelude only. The daemon retains the two most recent prelude pools and tags
file-backed sessions with the SHA-256 of the prelude they evaluated. Timestamp or size
changes trigger a refresh without a restart; a broken prelude fails session creation
instead of silently running without it.

Repeated top-level `#r "nuget: ..."` directives resolve through the official
`FSharp.Compiler.Service` dependency manager once and are then served from a persistent
`(TFM, RID, directives, nuget.config) -> assembly paths` cache under
`%LOCALAPPDATA%/fsion/nuget-cache` (`$XDG_CACHE_HOME/fsion/nuget-cache` elsewhere), with
source line numbers preserved.

## Repository layout

| Path | Purpose |
|---|---|
| `src/Fsion.Protocol` | Wire framing/DTOs and local IPC (named pipe, Unix socket, detached spawn) |
| `src/fsion` | NativeAOT thin CLI: arguments, daemon discovery/auto-start, IPC client, output passthrough |
| `src/fsiond` | Reactor: session pool, `AsyncLocal` console routing, prelude, NuGet cache, drain-and-respawn |
| `tests/Fsion.Tests` | Black-box tests driving the published `fsion` over real IPC against `dotnet fsi` |
| `bench/` | Interleaved unseen-script benchmark (`run-bench.py`, `data.json`) |
| `eng/gate.ps1` | The runnable repository gate |
| `eng/package.ps1` | Distribution bundle build + fresh-temp smoke test |
| `docs/` | Core design, spec, goal contracts, plans, evidence |

## Development

```powershell
cd Q:/repos/fsion
pwsh -NoProfile -File eng/gate.ps1
```

The gate builds Release for `net10.0` and `net11.0`, publishes the NativeAOT `fsion`,
runs the black-box test suite for both TFMs, and verifies LF line endings. Product and
test code is 100% F# with `TreatWarningsAsErrors=true`. Keep tracked text files
LF-terminated.
