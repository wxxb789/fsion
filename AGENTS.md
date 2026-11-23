# fsion — Project Agent Instructions

Fast, Windows-first F# script execution (`fsion` NativeAOT thin CLI + `fsiond` CoreCLR reactor daemon) built for coding-agent dynamic F# workflows and everyday `.fsx` scripting.

## Read First

- Architectural laws, latency budget, and forbidden anti-patterns: [docs/core-design.md](docs/core-design.md)
- Converged scope, non-goals, and black-box testing seam: [docs/specs/2026-11-22-fsion-core-design.md](docs/specs/2026-11-22-fsion-core-design.md)
- Active and archived goal contracts: [docs/goals/README.md](docs/goals/README.md) and [docs/goals/AGENTS.md](docs/goals/AGENTS.md)
- Measured evidence records: [docs/evidence/](docs/evidence/)

## Repository Layout

| Path | Kind | Purpose |
|---|---|---|
| `src/Fsion.Protocol` | F# library, `net10.0;net11.0` | Wire framing/DTOs (`Protocol.fs`) and local IPC (`Ipc.fs`: Windows named pipe, Unix domain socket). Shared by both executables; keep AOT-clean. |
| `src/fsion` | NativeAOT exe | Thin CLI: arg parsing, daemon discovery/auto-start, IPC client, stdout/stderr/exit passthrough. Never use F# `printf`/`sprintf`/`failwithf` here (AOT `MakeGenericMethod` trap). |
| `src/fsiond` | CoreCLR exe | Reactor: endpoint listener, background-replenished collectible `FsiEvaluationSession` pool and process-state gate (`SessionHost.fs`), `AsyncLocal` console routing (`Ambient.fs`), prelude `exit` shadowing. |
| `tests/Fsion.Tests` | xunit, `net10.0;net11.0` | Black-box tests driving the published `fsion` binary over real IPC against `dotnet fsi`; per-test `FSION_PIPE`/`FSIOND_PATH` isolation and per-test daemon shutdown. |
| `eng/gate.ps1` | script | The runnable repository gate. |
| `docs/evidence/` | docs | Per-goal measured evidence records. |

## Build & Verification Rules

- **Single gate**: `cd Q:/repos/fsion && pwsh -NoProfile -File eng/gate.ps1` builds Release (both TFMs), publishes NativeAOT `fsion` for `net10.0` and `net11.0` into `artifacts/publish/fsion/<tfm>/`, runs `dotnet test` per TFM, and checks LF hygiene. Tests resolve the published `fsion` and `src/fsiond/bin/Release/<tfm>/fsiond.exe`, so publish before testing.
- **100% F# (`net10.0;net11.0`)**: Keep all product and test projects in F# under `Directory.Build.props` (`TreatWarningsAsErrors=true`, `Deterministic=true`).
- **Two shipping binaries**:
  - `fsion`: NativeAOT (`PublishAot=true`) thin client with zero AOT/trim warnings and zero `printf` reflection traps (`MakeGenericMethod`).
  - `fsiond`: CoreCLR reactor hosting `FSharp.Compiler.Service` over private local IPC (Windows Named Pipe on Windows; Unix Domain Socket on macOS/Linux).
- **Windows AOT publish toolchain**: ILCompiler 10.0.15's `findvcvarsall.bat` captures VS18 `vcvarsall.bat` stderr; a bare `vswhere.exe` failure (`VS Installer` dir not on `PATH`) corrupts the captured `link.exe` path. `eng/gate.ps1` prepends `${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer` when `vswhere.exe` exists; keep that when scripting publishes elsewhere.
- **Zero temporary `.dll` writes on the hot path**: Evaluate scripts in memory (`Reflection.Emit` / `FsiEvaluationSession` with `--optimize- --multiemit-`) so Windows Defender never scans per-run binaries.
- **Process & file hygiene in tests**:
  - Never kill processes globally by name (`taskkill /IM dotnet.exe`, `pkill -f dotnet`); isolate test daemon endpoints/pipes per test run and shut down only the test's own daemon instance.
  - Keep all tracked text files LF-terminated (`git ls-files --eol` -> `i/lf`).
  - On Windows Git Bash, never run `find /` (it crawls entire drives and hangs); query `dotnet nuget locals global-packages -l` when inspecting the NuGet cache.
- **Comments**: Default to zero comments; keep short inline notes only for non-obvious platform/IPC/AOT hazards.

## Windows IPC & Process Hazards

- `fsion` spawns `fsiond` on Windows via Win32 `CreateProcessW` with `bInheritHandles=false` and `CREATE_NO_WINDOW | DETACHED_PROCESS` (`spawnDaemonWindows` in `src/fsion/Program.fs`). `Process.Start` sets `bInheritHandles=TRUE`, so a long-lived `fsiond` would inherit the caller's `stdout`/`stderr` pipe write ends and any redirected `ReadToEnd`/`capture_output` would block forever after `fsion` exits. Do not revert the Windows path to `Process.Start`.
- Daemon `Execute` runs are stateless: each claims a pooled collectible session and disposes it before the response. Runs that share a (working directory, environment) execute concurrently through `ProcessStateGate`; a different epoch serializes behind active runs. `exit <n>` is shadowed in the prelude and surfaces as the client exit code without terminating `fsiond`.
- Windows Named Pipe names (`--pipe` / `FSION_PIPE`) must be single-segment names without `\` or `/`; `resolveEndpoint` rejects path separators in `src/fsion/Program.fs` and `validateEndpointName` in `src/Fsion.Protocol/Ipc.fs` enforces it at connect/listen time.

## Daemon Pool & Isolation Invariants

- `src/fsiond/Ambient.fs` installs `AsyncLocal`-routed `Console.Out`/`Console.Error`/`Console.In` writers once at daemon startup; runs set their per-run `StringWriter`s in the ambient context. Never `Console.SetOut`/`SetError` a shared session per run.
- `src/fsiond/SessionHost.fs` owns the warm pool (default watermark 3; `fsiond --pool-size <n>` or `FSION_POOL_SIZE`, `0` disables warm sessions so every run claims cold), the `ProcessStateGate` cwd/env epochs, `--json` diagnostic collection, and the prelude `exit` shadowing. `fsion --json` emits one `Utf8JsonWriter` document (`exitCode`, `stdout`, `stderr`, `elapsedMs`, `diagnostics[]`); default mode stays raw stream/exit passthrough.
- FCS collectible sessions install process-wide GC roots that must be removed when the session is released: `AppDomain.ProcessExit`, `AssemblyLoadContext.Default.ResolvingUnmanagedDll`, the static `AssemblyLoadContext.AssemblyResolve` field, the `diagnosticsLogger`/`buildPhase` `AsyncLocal`s, and the `TcConfigBuilder` `tryGetMetadataSnapshot` callback closure. `releaseSession` must remove only the delegates its own creation added (under `creationGate`), sever the callback, clear checker caches, null `FSI-ASSEMBLY` static fields, dispose, and force a full GC sweep before responding, so undisposed script file handles cannot keep Windows file locks and `AliveDisposedSessions` stays 0.
- `ExecuteRequest` carries the client's full environment snapshot; the daemon applies and restores it around each run. `GetMetrics` (`PoolTarget`/`PoolWarm`/`ColdClaims`/`Executed`/`ManagedHeapBytes`/`ScriptAssemblies`/`AliveDisposedSessions`) is the black-box probe for pool and unload tests.

## Goal Close Ritual

When closing an active goal `docs/goals/NNNN-<slug>.md`:
1. Move the goal file unchanged into `docs/goals/archive/NNNN-<slug>.md`.
2. Update [docs/goals/README.md](docs/goals/README.md) (`Active Series` -> `Completed and Archived`).
3. Update this `AGENTS.md` with exact project paths, build/test/publish gate commands, and newly established module boundaries so subsequent agents do not rediscover them.
