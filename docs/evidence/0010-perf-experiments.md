# Post-PR #10 Single-Variable Performance Experiments (`main` @ `16f61ef` / `3ee0c54`)

Host: `CPC-lhan-XF3D0M` (`Windows-11-10.0.26300-SP0`, `AMD64`, `.NET SDK 11.0.100`, `fsion` TFM `net10.0`, `Q:\` ReFS Dev Drive).
Policy: One independent variable per experiment; keep only changes with clear end-to-end gain; revert and record neutral or negative experiments so they are not retried blindly.

## 0. Windows Process Startup Ladder & Corrected Budget Attribution

Earlier probes mixed `fsion --json` internal `elapsedMs` (measured before `emit`, with `IncludeDiagnostics=true`) and `wall - elapsedMs` as "pure OS `CreateProcessW`". A controlled 30-round interleaved startup ladder under the same Python `subprocess.run(..., capture_output=True)` harness disproved that attribution:

| Binary | Size | Median | Mean | Min | p95 | Delta vs `win32-write` |
|---|---:|---:|---:|---:|---:|---:|
| `win32-noop` (zero-CRT `kernel32` only) | `2.5 KB` | `16.30 ms` | `16.52 ms` | `14.12 ms` | `20.21 ms` | `-0.21 ms` |
| `win32-write` (`WriteFile` version string) | `2.5 KB` | `16.51 ms` | `17.14 ms` | `14.41 ms` | `22.65 ms` | `0.00 ms` |
| `bun -e ''` / `bun --version` | `115 MB` | `22.32 / 22.92 ms` | `31.70 ms` | `20.24 ms` | `33.58 ms` | `+5.8 ~ +6.4 ms` |
| `min-fs-aot` (1-line F# NativeAOT) | `933 KB` | `32.94 ms` | `34.19 ms` | `28.91 ms` | `41.64 ms` | `+16.43 ms` |
| `fsion --version` (full F# NativeAOT) | `4.3 MB` | `35.42 ms` | `43.56 ms` | `31.62 ms` | `42.32 ms` | `+18.91 ms` |
| `fsion --version` (Zig bare `kernel32` shim) | `58 KB` | `19.44 ms` | — | `16.38 ms` | — | `+2.9 ms` |

Key findings:
- **`~16.5 ms`** is the Windows process spawn + Python `capture_output` floor (`win32-write`).
- **`~16.4–18.9 ms`** of `fsion.exe`'s startup cost was the **.NET NativeAOT runtime itself** (`RhInitialize` + 11–13 static DLL imports including `ADVAPI32`, `bcrypt`, `ole32`, `Secur32`, `WS2_32`, and 7 UCRT API sets), not Windows `CreateProcessW`.
- Replacing the hot-path CLI entry point with a zero-CRT native shim recovers **~17–22 ms** per invocation on Windows—enough to beat `bun`'s startup floor directly.

## 1. Experiment Log

### Exp #1 — Packaged ReadyToRun `fsiond` vs RID-less `bin/Release` `fsiond`
- **Verification**: PE header inspection confirmed `eng/package.ps1` (`PublishReadyToRun=true`) compiles R2R native code (`RTR\0` v16.0) into `FSharp.Compiler.Service.dll` (`42,569,728 B` PE32+ vs `21,423,400 B` pure IL), `FSharp.Core.dll`, `fsiond.dll`, and `Fsion.Protocol.dll`.
- **Measurement** (same `fsion.exe`, two separate 2-runner campaigns):
  - Clean run: spaced `117.3` vs `119.3 ms`; burst `99.6 ms` (max `132.7`) vs `128.6 ms` (max `428.0`), while co-run `bun` burst shifted `67.2 -> 79.7 ms`.
  - Contended run (`r2r-probe`): burst direction reversed (`112.5 ms` max `384.8` vs `99.0 ms` max `151.2`, both `ColdClaims=0`).
- **Decision**: **KEEP existing R2R packaging** in `eng/package.ps1`; do **not** claim a fixed `-29 ms` R2R win without RID-matched randomized interleaving.

### Exp #2 — `WinHandleTable` `ObjectTypeIndex` Pre-Filtering + Reusable Unmanaged Snapshot Buffer
- **Hypothesis**: Filtering `NtQueryInformationProcess(ProcessHandleInformation)` entries by Windows `File` `ObjectTypeIndex` before calling `GetFileType`/`GetFileInformationByHandle` cuts ~1.2–2.2 ms.
- **Measurement**:
  - `gateReleaseMs` on short `-e`: `3.11 ms -> 2.75 ms` (`-0.36 ms`).
  - End-to-end `typical` (9 rounds + burst): `119.3 -> 129.0 ms` (drowned in host noise).
- **Decision**: **REVERTED** (gain too small to justify P/Invoke struct-offset coupling).

### Exp #3 — NativeAOT Client Trimming (`OptimizationPreference=Size`, `StackTraceSupport=false`, `UseSystemResourceKeys=true`, `EventSourceSupport=false`, `DebuggerSupport=false`, `MetadataUpdaterSupport=false`)
- **Measurement** (30 interleaved reversed rounds of `fsion --version`):
  - Binary size: `4,298,752 B` -> `3,293,696 B` (`-23.4%`).
  - Latency: base median `39.4 ms` (mean `47.9`, min `35.7`) vs trimmed median `40.0 ms` (mean `39.5`, min `35.6`).
- **Decision**: **REVERTED** (does not remove the NativeAOT runtime / DLL import floor).

### Exp #4 — Env Payload Probe (Full 187-var 11.8 KB env vs Minimal 6-var 171 B env)
- **Measurement**:
  - `fsion -e '()'`: full env median `45.2 ms` (p95 `56.2`) vs min env median `45.2 ms` (p95 `267.7`).
  - 4-block `typical` (`full-1 / min-1 / min-2 / full-2`): medians `76.9 / 74.1 / 329.8 / 142.0 ms` (`min-2` had 6/9 rounds `> 300 ms`, `full-2` also elevated to `142.0 ms` due to host/background interference).
- **Decision**: **DEFERRED** (Track A env-delta protocol deferred for one-shot CLI; inconclusive under host noise and far smaller leverage than eliminating NativeAOT startup).

### Exp #5 — Removing `FxResolver.ClearStaticCaches()` + Slimming `builtinPrelude` (Multi-Variable, Reverted)
- **Measurement**:
  - Warmup (`pool=12` total): `~13.0 s -> ~11.0 s`.
  - Foreground `typical`: regressed (`evalMs` first turn `220.5 -> 413.5 ms`, wall `294.3 -> 483.9 ms`; no-log 9-hot median `96.6 -> 108.2 ms`).
- **Note**: This experiment bundled 3 changes (`FxResolver.ClearStaticCaches()`, per-session prelude slimming, host-level warm). Keep `builtinPrelude` intact; `FxResolver.ClearStaticCaches()` removal may be re-tested in isolation later.
- **Decision**: **REVERTED**.

### Exp #6 — `completeUnloadSweep` Non-Blocking Gen-2 GC / 4000 ms Quiet Window (W9)
- **Measurement**:
  - Switching `completeUnloadSweep` to `GCCollectionMode.Optimized, blocking=false` **FAILED** `IsolationGcTests.twenty unseen zero-gap runs unload their collectible sessions` (did not reach `alive=0` within 30 s).
  - Keeping blocking Gen-2 with a `4000 ms` quiet window passed `IsolationGcTests` (32 s) but showed no clear median improvement under host load (`87.1` / `108.0 ms`).
- **Decision**: **REVERTED** (preserve deterministic 500 ms unload hygiene).

### Exp #7 — Cross-Platform Zero-CRT Hot-Path Thin Client Shim (C Prototype -> Zig 0.17.1 Adopted)
- **Hypothesis**: Bypassing the 4.3 MB F# NativeAOT client runtime on the hot path with a zero-CRT native shim recovers `~16–20 ms` per invocation while delegating cold paths (`warmup`, `daemon`, `--json`, `--no-daemon`, auto-start, custom prelude) to colocated `fsion-cli`.
- **Measurement**:
  - **C prototype (`12 KB`, `kernel32` only)** vs F# NativeAOT (`4.3 MB`) on 10-round zero-gap `typical` burst (same R2R `fsiond`):
    - C shim: **`73.4 ms`** median (min `61.8 ms`; reached `54.5 ms` in another run) vs F# NativeAOT **`90.9 ms`** (min `75.7 ms`) — **`-17.5 ms` (-19.3%)**.
    - `wall - daemonMs` dropped from `~38–40 ms` to **`21.5–22.8 ms`**.
  - **Cross-platform Zig `0.17.1` shim** (`src/fsion-shim/main.zig`, `-OReleaseSmall -fstrip`: Windows `58 KB` bare `kernel32`, Linux `13 KB` zero-libc static, macOS `51 KB` `libSystem`):
    - `--version` (25 interleaved rounds): Zig **`19.44 ms`** vs C `19.12 ms` vs F# NativeAOT `41.46 ms` (**`-22.0 ms`**).
    - 8-round interleaved reversed `typical` (same R2R `fsiond`): Zig **`78.7 ms`** (min `66.1 ms`) vs C `86.5 ms` (min `71.7 ms`) vs F# NativeAOT `114.1 ms` (min `78.8 ms`).
  - **Full black-box gate (`eng/gate.ps1 -Framework net10.0 -Filter 'Category!=Perf'`)**: **99/99 PASS** in `10m06s`, 0 warnings, LF clean; `eng/package.ps1 -Framework net10.0` smoke test **PACKAGE OK**.
- **Decision**: **ADOPTED** (owner-approved amendment to `docs/core-design.md` §2.1 for a unified cross-platform Zig hot-path shim while keeping `fsiond`, `Fsion.Protocol`, `fsion-cli`, and `Fsion.Tests` 100% F#).
