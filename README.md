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

Interleaved, byte-verified 30-round benchmark on the Windows development host
(`CPC-lhan-XF3D0M`, AMD64, 64 GB, .NET SDK 11.0.100, bun 1.4.5, node v26.13.1,
Python 3.15.2). Every round renders brand-new F# / TS / JS / Python source with a fresh
nonce for both workloads, and a round's timings are recorded only after all runners
produced byte-identical stdout. Full report and raw per-round data:
[`docs/evidence/0005-benchmark-report.md`](docs/evidence/0005-benchmark-report.md),
[`docs/evidence/0005-benchmark-results.json`](docs/evidence/0005-benchmark-results.json).

`typical` workload (JSON parse + `BENCH_DOMAIN` + filter/sort + formatted stdout), ms per
process invocation, n = 30:

| Runner | Median | Mean | Stdev | Min | p95 | Max | vs fsion median |
|---|---|---|---|---|---|---|---|
| `fsion` (warm pool, unseen script per round) | **73.3** | 87.3 | 41.5 | 67.6 | 209.9 | 214.3 | — |
| `dotnet fsi` (fresh process per round) | 3075.5 | 3137.8 | 223.1 | 2941.4 | 3372.7 | 4174.2 | **41.9x slower** |
| `bun` 1.4.5 (`bun run *.ts`) | 49.4 | 70.7 | 50.7 | 43.5 | 190.8 | 192.1 | 1.48x faster |
| `node` v26.13.1 (`*.js`) | 71.2 | 72.0 | 4.9 | 63.2 | 79.7 | 81.4 | 1.03x faster |
| `python` 3.15.2 (`*.py`) | 62.5 | 68.3 | 28.9 | 58.5 | 76.8 | 220.2 | 1.17x faster |

`hello` workload (single formatted write), median / p95, n = 30: `fsion` **54.6** / 85.0
(`54.1x` faster than `dotnet fsi`), `dotnet fsi` 2950.7 / 3137.7, `bun` 47.9 / 56.1, `node`
73.4 / 82.3, `python` 44.6 / 52.7.

**AC-07 outcome on this host.** The `>= 20x` median speedup over `dotnet fsi` is met with
wide margin (**41.9x** on `typical`, **54.1x** on `hello`), and warm `fsion` matches `node`
(73.3 ms vs 71.2 ms on `typical`, and 18.8 ms faster than `node` on `hello`). On `hello`,
`fsion` is within 6.7 ms (`1.14x`) of `bun`; on `typical`, the strict `<= 1.00x bun` bar is
**not** met (`fsion` 73.3 ms vs `bun` 49.4 ms, `1.48x`). The measured breakdown of the
73.3 ms is: ~28 ms Windows process spawn + NativeAOT client + Named Pipe round-trip, ~2 ms
process-state transition, ~32 ms in-memory F# parse + Hindley-Milner typecheck + IL emit +
Tier-0 execution of the unseen script, and ~6–11 ms `releaseSession` (the Gen-0 finalizer
sweep that closes handles left open by throwaway scripts). The remaining ~24 ms gap to `bun`
on `typical` is F#'s static Hindley-Milner typecheck + IL emission vs `bun`'s zero-typecheck
TS type-stripping. The harness reports this verdict itself: `--require-ac07` exits 1 when
`<= 1.00x bun` is missed, and the committed report records `FAIL` for that sub-bar honestly.

Reproduce it yourself after building (the harness needs the published client and the
built daemon on the same machine):

```powershell
pwsh -NoProfile -File eng/gate.ps1      # build + publish + test the repo
python bench/run-bench.py --require-ac07
```

`bench/run-bench.py` writes the committed report to
[`docs/evidence/0005-benchmark-report.md`](docs/evidence/0005-benchmark-report.md) plus
the raw per-round timings. Never run the benchmark while the test suite is running.

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
- **Collectible pool with background replenishment.** `fsiond` keeps 3 warm sessions
  (`--pool-size`, `FSION_POOL_SIZE`) that already imported core BCL metadata and the
  `Fsion` prelude; the pool refills in the background while the agent does other work.
- **Stateful named sessions.** `--session <id>` persists bindings across calls until
  `--reset` or the idle session TTL; default runs stay isolated and stateless.
- **Fault containment.** `--timeout <ms>` cancels cooperatively first; a wedged
  synchronous loop drains the endpoint and a fresh daemon generation takes over.
- **Agent-friendly output.** Default mode is byte-faithful `stdout`/`stderr`/exit-code
  passthrough; `--json` adds structured diagnostics with file/line/column.

## Install

Requirements: the .NET 10 or .NET 11 SDK (both TFMs are built and tested) and, for the
NativeAOT client, a C/C++ toolchain for the host OS (Visual Studio 2022+/Build Tools on
Windows, `clang` on Linux, Xcode Command Line Tools on macOS).

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

Publishes the NativeAOT client and the framework-dependent daemon side by side into
`artifacts/package/fsion-<rid>-<tfm>/`, archives it (`.zip`/`.tar.gz`), and runs the
bundle end to end from a fresh temporary directory (warmup, script file, `Fsion` helpers,
exit-code passthrough, `--json` diagnostics, daemon stop). `-Runtime <win-x64|linux-x64|osx-arm64>`
defaults to the host RID; NativeAOT cannot cross-compile to another OS, so run the script
on each OS (the 3-OS CI matrix does exactly that).

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
| `--no-daemon` | Run a single-shot daemon for this invocation only (cannot combine with `--session`) |
| `--json` | Emit one JSON document: `exitCode`, `stdout`, `stderr`, `timedOut`, `elapsedMs`, `diagnostics[]` |
| `--session <id>` | Bind the run to a named stateful session (created on first use) |
| `--reset` | Discard the named session before this run (requires `--session`) |
| `--timeout <ms>` | Cancel a run after its execution deadline; cooperative scripts use `fsionFsiState.CancellationToken`; exit 124 |
| `--prelude <path>` | Evaluate this `prelude.fsx` in warm sessions (env: `FSION_PRELUDE`) |
| `--no-prelude` | Skip the built-in `Fsion` module and the user prelude |
| `--pipe <name>` | Daemon endpoint name (env: `FSION_PIPE`, default `fsion-<user>`) |
| `--idle-ttl <minutes>` | Auto-started daemon idle TTL (env: `FSION_IDLE_TTL_MINUTES`, default 30) |
| `--session-ttl <min>` | Named-session idle TTL (env: `FSION_SESSION_TTL_MINUTES`, default 15) |
| `--pool-size <n>` | Warm session pool size for the auto-started daemon; `0` disables warm sessions (env: `FSION_POOL_SIZE`, default 3) |
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
on Windows (`~/.config/fsion/prelude.fsx` elsewhere) → built-in prelude only. The daemon
tags each warm session with the SHA-256 of the prelude it evaluated, so editing
`prelude.fsx` refreshes the pool without a restart; a broken prelude fails session
creation instead of silently running without it.

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
