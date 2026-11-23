# Goal 0004 Evidence — Batteries-Included Agent Prelude & Zero-Cost Cached NuGet Directives

Contract: [../goals/archive/0004-agent-prelude-and-cached-nuget.md](../goals/archive/0004-agent-prelude-and-cached-nuget.md)
Implementation choices: [../plans/2026-11-23-goal-0004-implementation-choices.md](../plans/2026-11-23-goal-0004-implementation-choices.md)

## Environment

| Item | Value |
|---|---|
| Host | `CPC-lhan-XF3D0M` (Microsoft Windows 11 Enterprise 10.0.26300.0) |
| Base commit | `c65d63f46aa2d81ddf9d68bd2bc8f38c65b53a37` (goal 0004 tree on top) |
| .NET SDK | 11.0.100 |
| `FSharp.Compiler.Service` (fsiond) | 43.13.101 |
| FSharp.Core (fsiond / scripts) | 11.0.101 |
| PowerShell | 7.6.10 |

## Gate

Single runnable gate: `cd Q:/repos/fsion && pwsh -NoProfile -File eng/gate.ps1`.

Recorded run on this tree: **exit code 0** (`GATE PASSED`).

| Step | Result |
|---|---|
| `dotnet build fsion.slnx -c Release` | Build succeeded, 0 warnings, 0 errors |
| `dotnet publish src/fsion -c Release -f net10.0` | exit 0, `artifacts/publish/fsion/net10.0/fsion.exe` (4,129,280 bytes, 0 trim/AOT warnings) |
| `dotnet publish src/fsion -c Release -f net11.0` | exit 0, `artifacts/publish/fsion/net11.0/fsion.exe` (4,154,880 bytes, 0 trim/AOT warnings) |
| `dotnet test tests/Fsion.Tests -c Release -f net10.0` | Failed 0, Passed 67, Skipped 0, Total 67, 11 m 30 s |
| `dotnet test tests/Fsion.Tests -c Release -f net11.0` | Failed 0, Passed 67, Skipped 0, Total 67, 11 m 6 s |
| `Assert-LineEndings` | `line endings: 51 tracked and 51 non-ignored files clean` |

Focused detailed run (prior to the full gate, non-concurrent):

| Command | Result |
|---|---|
| `dotnet test tests/Fsion.Tests -c Release -f net11.0 --no-build --filter "FullyQualifiedName~PreludeTests\|FullyQualifiedName~NugetCacheTests" --logger "console;verbosity=detailed"` | exit 0, Passed 5, Total 5, 1 m 18 s |

Baseline observed on `c65d63f` before Goal 0004:
- `fsion -e 'Fsion.cwd()'` -> exit 1 (`error FS0039: The value, namespace, type or module 'Fsion' is not defined`).
- `fsion --no-prelude -e 1` / `fsion --prelude p.fsx -e 1` -> exit 1 (`unknown option`).

## Clause 1 — Ergonomic Built-In Prelude + `--no-prelude` Compatibility

- **Warm BCL & `Fsion` module**: Pre-warmed pool sessions import core BCL metadata (`System`, `System.IO`, `System.Text`, `System.Text.Json`, `System.Text.RegularExpressions`, `System.Net.Http`, `System.Linq`) and expose `module Fsion` (`cwd`, `path`, `env`, `envOr`, `readText`, `writeText`, `json`) inside the single combined session-creation interaction (`1` `FSI-ASSEMBLY` per warm session, zero third-party runtime packages).
- **Black-box proof (`PreludeTests.built-in Fsion helpers are preloaded on warm sessions and --no-prelude hides them`, Passed [12 s])**:
  - After `fsion warmup`, running a dynamic script exercising `Fsion.writeText`, `Fsion.cwd()`, `Fsion.path`, `Fsion.env`, `Fsion.envOr`, `Fsion.readText`, and `Fsion.json` exits `0` with exact round-trip output and **`ColdClaims` unchanged** (proving warm-pool reuse with zero per-run preload cost).
  - Running `fsion --no-prelude -e 'printfn "%s" (Fsion.cwd())'` exits `1` with `FS0039`, while `fsion --no-prelude -e 'printfn "bare"; exit 3'` prints `bare` and exits `3` (proving infrastructure `exit` shadowing stays intact when opting out of `Fsion` and user preludes).
  - All 60 pre-existing parity, isolation, session, recovery, JSON, lifecycle, and fault tests pass unchanged across both `net10.0` and `net11.0`.

## Clause 2 — 0 ms Preloaded User `prelude.fsx` & Live Edit Invalidation

- **Precedence & black-box proof (`PreludeTests.prelude resolution prefers the flag, then the environment, then the config directory`, Passed [12 s])**:
  - Verified `<FSION_CONFIG_DIR>/prelude.fsx` -> overridden by `FSION_PRELUDE` -> overridden by `--prelude <path>`, `--no-prelude` hiding all user prelude bindings (`FS0039`), missing `--prelude` returning exit `1` (`prelude file not found`, both raw stderr and structured `--json` via `emitFailure`), `--prelude` + `--no-prelude` rejected (`cannot be combined`), and `--version` / `daemon status` succeeding even when `FSION_PRELUDE` points to a missing path.
- **Pool-creation preloading + UTF-8 BOM + live SHA-256 invalidation (`PreludeTests.configured prelude with a nuget package preloads bindings and refreshes on edit` & `a named session recycles when its prelude tag changes`)**:
  - A custom UTF-8-with-BOM `prelude.fsx` containing `#r "nuget: fsiontiny..., 1.0.0"` and a side-effecting `File.AppendAllText(preloadCountPath, "1\n")` is evaluated **during `fsion warmup` pool creation** (`preloadCountPath` already contains `>= PoolWarm` lines BEFORE the first script run ever executes!).
  - First one-shot script `printfn "%s|%s" (preludeGreeting()) (FsionTinyPkgNs.Greeter.Hello())` prints `first|tiny-package-ok` at warm-pool speed (`ColdClaims` unchanged), and sequential turns in `--session alpha` observe a `let mutable preludeTurns` binding defined in `prelude-a.fsx` increment `1 -> 2` (proving the prelude is not re-evaluated per run).
  - Editing `prelude.fsx` on disk to return `"edited"` causes the next one-shot run to print `edited|tiny-package-ok` while `context.DaemonPid()` remains identical; switching `--prelude` from `prelude-a` to `prelude-b` on `--session alpha` rebuilds the session so `onlyInPreludeA()` fails with `FS0039` (proving genuine session recycle rather than in-place shadowing).
## Clause 3 — Persistent `#r "nuget: ..."` Resolution Cache

- **Official `DependencyProvider` + lexical scanner**:
  - `src/fsiond/NugetCache.fs` scans top-level `#r "nuget: ..."` directives (ignoring directives inside `//` comments, nested `(* ... *)` comments, normal strings, `@"..."` verbatim strings, and `"""..."""` triple-quoted strings), keys entries by `SHA-256("fsion-nuget-cache-v1" | TFM | RID | sorted normalized directives | nuget.config fingerprint)` under `FSION_CACHE_DIR` (`%LOCALAPPDATA%/fsion/nuget-cache` default on Windows), delegates cache misses to FCS's official `DependencyProvider.Resolve`, and rewrites cache hits to `#r @"<dll>"` + `#I @"<root>"` while blanking directive lines in place so source line numbers stay exact.
- **Black-box proof & side-by-side timing (`NugetCacheTests`, Passed [24 s & 6 s])**:
  - `a nuget directive inside a comment or string is neither rewritten nor cached`: uses a real resolvable local package inside `(* ... *)` and `"""..."""`, exits `0` (`ok=true`), and writes `0` cache files; `cached nuget hits preserve source line numbers and subdirectory __SOURCE_DIRECTORY__` verifies that a subdirectory script (`sub/srcdir.fsx`) carrying `#r "nuget: ..."` preserves `__SOURCE_DIRECTORY__ = <root>/sub` and that `--json` diagnostics on `sub/broken.fsx` report the full `sub/broken.fsx` path and exact `startLine = 3` on both file and `-e` cache hits.
  - `cached nuget directives skip official resolution on a later daemon generation`:
    - **Run 1 (`first.fsx`, cache miss — full official NuGet DependencyManager resolution)**: **`6,346 ms`**, prints `first=tiny-package-ok`, persists JSON cache entry under `FSION_CACHE_DIR`.
    - Daemon is stopped (`fsion daemon stop`), FCS's own `~/.packagemanagement/nuget` cache is purged, the local NuGet feed directory is moved away, and `NUGET_PACKAGES` is pointed at a brand-new empty directory (`packages-second`).
    - **Run 2 (`second.fsx`, distinct unseen script with re-spaced `#r "nuget:  <pkg>,1.0.0"` on a fresh daemon generation — persistent cache hit)**: **`452 ms`** (**`14.0x` faster**, `7.1%` of first-run latency!), prints `second=tiny-package-ok`, and `packages-second` does not even exist on disk (proving zero MSBuild/NuGet restore ran).

## Known Limits

- Native library probing directories (`result.Roots`) on cache hits are emitted as `#I @"<dir>"` directives; the deterministic local test package is managed-only, so native-asset NuGet packages are code-reviewed rather than black-box exercised here.
- Non-Windows `XDG_CACHE_HOME` / `~/.config/fsion/prelude.fsx` paths are verified by build/typecheck on this Windows host until Goal 0005 adds 3-OS CI.
