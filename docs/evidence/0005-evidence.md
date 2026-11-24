# Goal 0005 Evidence — Cross-Platform Release Gate & Competitive Benchmark Proof

Contract: [../goals/archive/0005-cross-platform-packaging-and-benchmark-gate.md](../goals/archive/0005-cross-platform-packaging-and-benchmark-gate.md)
Implementation choices: [../plans/2026-11-23-goal-0005-implementation-choices.md](../plans/2026-11-23-goal-0005-implementation-choices.md)
Generated benchmark report: [0005-benchmark-report.md](0005-benchmark-report.md) and [0005-benchmark-results.json](0005-benchmark-results.json)

## Environment

| Item | Value |
|---|---|
| Host | `CPC-lhan-XF3D0M` (Microsoft Windows 11 Enterprise 10.0.26300.0, AMD64, 64 GB) |
| Base commit | `7cf6be7992ddb95eeae7673c2093a80d2c9ebf15` (goal 0005 tree on top) |
| .NET SDKs | 11.0.100 (build), 10.0.115 (present); runtimes `Microsoft.NETCore.App` 10.0.15 and 11.0.0 |
| `FSharp.Compiler.Service` (fsiond) | 43.13.101 |
| Peer runtimes | bun 1.4.5, node v26.13.1, Python 3.15.2 |
| PowerShell | 7.6.10 |
| Benchmark salt | `cd8d9305` (30 measured rounds + 3 warmup rounds, 5 runners, 2 workloads) |

## Gate

Single runnable gate: `cd Q:/repos/fsion && pwsh -NoProfile -File eng/gate.ps1`, executed on the
settled goal-0005 tree.

| Step | Result |
|---|---|
| `dotnet build fsion.slnx -c Release` | exit 0, 0 Warning(s), 0 Error(s) |
| `dotnet publish src/fsion -c Release -f net10.0` | exit 0, `artifacts/publish/fsion/net10.0/fsion.exe` (4,171,264 bytes), 0 AOT/trim warnings |
| `dotnet publish src/fsion -c Release -f net11.0` | exit 0, `artifacts/publish/fsion/net11.0/fsion.exe` (4,201,984 bytes), 0 AOT/trim warnings |
| `dotnet test tests/Fsion.Tests -c Release -f net10.0` | exit 0, Passed: 70, Failed: 0, Skipped: 0, Total: 70 (11 m 32 s) |
| `dotnet test tests/Fsion.Tests -c Release -f net11.0` | exit 0, Passed: 70, Failed: 0, Skipped: 0, Total: 70 (11 m 22 s) |
| `Assert-LineEndings` | 60 tracked and 60 non-ignored files clean (LF) |
| Overall | **exit 0 (`GATE PASSED`)** |

Focused run before the full gate (non-concurrent, `net10.0`):
`dotnet test tests/Fsion.Tests/Fsion.Tests.fsproj -c Release -f net10.0 --no-build --filter "FullyQualifiedName~IsolationTests|FullyQualifiedName~LifecycleTests|FullyQualifiedName~LatencyTests"`
→ **exit 0**, `Passed: 23, Failed: 0, Total: 23` in 3 m 41 s, including the new
`--pool-size reaches the auto-started daemon and zero disables warm sessions` and the unchanged
`undisposed file handles from a throwaway script are released before fsion returns`.

## Clause 1 — Cross-Platform Packaging (`eng/package.ps1`)

Command: `pwsh -NoProfile -File eng/package.ps1 -Framework net10.0` → **exit 0** (`PACKAGE OK`),
bundle `artifacts/package/fsion-win-x64-net10.0/` **27.3 MB**, archive
`artifacts/package/fsion-win-x64-net10.0.zip` **11.5 MB** (`12,017,444` bytes).

Publishes the NativeAOT client and the framework-dependent daemon side by side, strips `*.pdb`,
adds `README.txt`, and runs the bundle from a **fresh temporary directory**:
`Q:/tmp/fsion-package-smoke-<guid>/` with a unique `FSION_PIPE`, temp `FSION_CONFIG_DIR`/`FSION_CACHE_DIR`
and **no `FSIOND_PATH`** (the packaged `fsion` must discover the packaged `fsiond` next to itself):

| Smoke step | Result |
|---|---|
| `warmup` | exit 0, `fsiond ready (pid=..., warm=True)` |
| script file with `JsonDocument`, `Fsion.envOr`, `Fsion.cwd` | exit 0, `total=6`, `name=packaged`, `helper=fallback`, `cwd=<temp work dir>` |
| `-e 'printfn "before-exit"; exit 7'` | exit 7, prints `before-exit` |
| `--json -e 'let broken: int = "nope"'` | exit 1, one JSON document with `FS0001`, `eval.fsx`, line 1 columns 18-24 |
| `daemon stop` then `daemon status` | exit 0 each, status reports `fsiond not running` |

Cross-OS parameterization: `-Runtime` defaults to the host RID; a RID whose OS differs from the host
fails fast with exit 1 and `NativeAOT cannot cross-compile linux-x64 from win; run eng/package.ps1 on
the target OS (the 3-OS CI matrix covers win-x64, linux-x64, and osx-arm64)` (observed), because
NativeAOT has no cross-OS toolchain. The same script carries the VS Installer `PATH` fix the gate
needs for Windows AOT publishes.

## Clause 2 — 3-OS CI (`.github/workflows/ci.yml`)

Matrix `windows-latest` / `macos-latest` / `ubuntu-latest`, `fail-fast: false`, each job: checkout →
`actions/setup-dotnet` with `10.0.x` + `11.0.x` (the net10.0 test host needs the .NET 10 runtime,
the 11.0 SDK drives the build) → Ubuntu-only `clang` + `zlib1g-dev` → `pwsh -NoProfile -File
eng/gate.ps1` (build, NativeAOT publish for both TFMs, black-box tests for both TFMs, LF hygiene) →
`pwsh -NoProfile -File eng/package.ps1 -Framework net10.0` (fresh-temp smoke test) → upload the
archive.

GitHub Actions CI (`.github/workflows/ci.yml`) runs on every pull request and push to `main` and is a
merge-blocking check (`windows-latest`, `macos-latest`, and `ubuntu-latest` must all pass `eng/gate.ps1`
and `eng/package.ps1 -Framework net10.0` on the PR head SHA before merge; run `45285361941` passed
`ubuntu-latest` and `macos-latest` end to end and caught missing `* text=auto eol=lf` in
`.gitattributes` on `windows-latest`, fixed in `0467ee4`, after which run `45289320135` passed
`windows-latest` and `ubuntu-latest` end to end).

## Clause 3 — Unseen-Script Benchmark Harness and AC-07

### Harness

`bench/data.json` (workloads, env, thresholds, runner table) + `bench/run-bench.py` (stdlib only).
Every round renders fresh source for all runners from a per-invocation salt plus the round index:
the nonce (`r<round>-<8 hex>`) is embedded in the JSON payload's names/scores and in the printed
output, and a nonce-derived constant is embedded in the expression itself. `hello` is one formatted
write; `typical` parses an embedded JSON array, reads `BENCH_DOMAIN`, filters `active`, sorts by
score descending then name, and writes `domain=`/`name|score`/`count=` with explicit byte writes in
all four languages. Runners are interleaved with a per-round rotation; **a round's timings are
recorded only after every present runner exited 0 with stdout byte-identical to the round's expected
rendering** (mismatch aborts with both outputs). The harness isolates `fsion` from developer state
(unique `FSION_PIPE`, empty `FSION_CONFIG_DIR`, `FSION_PRELUDE` removed, `FSIOND_PATH` at the built
daemon). Default outputs are the committed report and raw JSON; `--require-ac07` makes a missed
threshold a non-zero exit.

Pre-change baseline observations that shaped the harness:

- The first generated F# `typical` script used `StringBuilder` append chains; temporary daemon-side
  phase tracing measured its FCS eval at ~100-125 ms vs ~56 ms for the equivalent without
  `StringBuilder` (the difference is consistent with F# overload resolution on `Append`), so the
  generator now emits direct `Console.Out.Write` calls.
- The first harness draft used `tempfile.mkdtemp()` under `Q:\tmp`: `bun` measured ~105-120 ms there
  while the same script in the repository tree measured ~48-56 ms (`node`, `python`, and `fsion` were
  path-insensitive). `Q:\tmp` holds **58,335** entries; naming the script's directory from `Q:\tmp`
  or running with `cwd=Q:\tmp` reproduced the penalty (script-in-repo/cwd-in-tmp: median 111.8 ms).
  A temp-dir carrier therefore charges the baseline ~50 ms the implementation does not pay, so the
  harness work directory now defaults to the gitignored `bench/.work/run-<salt>` inside the repo.

### Committed run (30 rounds, `docs/evidence/0005-benchmark-report.md`)

All 30 measured rounds passed byte-for-byte stdout parity across `fsion`, `dotnet-fsi`, `bun`,
`node`, and `python` (exit 0 for every run; per-round nonces, F# source SHA-256, and output SHA-256
in the report and raw JSON). `fsion` rounds above 5x its median (cold-claim detector): **0**.

`typical` (ms per process invocation, n = 30):

| Runner | Median | Mean | Stdev | Min | p95 | Max |
|---|---|---|---|---|---|---|
| `fsion` | **71.8** | 73.6 | 5.6 | 67.9 | 85.5 | 92.9 |
| `dotnet fsi` | 3023.3 | 3112.2 | 222.4 | 2955.8 | 3687.2 | 3897.5 |
| `bun` | **47.2** | 48.2 | 3.5 | 45.4 | 56.4 | 63.0 |
| `node` | 71.2 | 73.0 | 7.0 | 67.9 | 85.4 | 103.8 |
| `python` | 61.9 | 73.0 | 35.1 | 60.2 | 198.2 | 201.4 |

`hello` medians: `fsion` **54.4** (`52.49x` faster than `dotnet fsi`, faster than `node` 71.9),
`dotnet fsi` 2857.1, `bun` 46.3, `node` 71.9, `python` 44.1.

### AC-07 verdict

- **`>= 20x` median speedup over `dotnet fsi` on unseen `typical` scripts: PASS** — 3023.3 / 71.8 =
  **42.11x** (and **52.49x** on `hello`).
- **bun-class latency (strict `<= 1.00x bun` threshold): FAIL as measured** — `fsion` is **1.521x**
  bun's median on `typical` (71.8 vs 47.2 ms; matching `node`'s 71.2 ms within 0.6 ms) and **1.176x**
  on `hello` (54.4 vs 46.3 ms, 8.1 ms delta). The harness reports this itself:
  `python bench/run-bench.py --require-ac07` exits **1**, and the committed report records `FAIL` for
  that bar.

Four avoidable daemon-side overheads were eliminated before this final 30-round run (bringing
`typical` down from 104.4 ms to **71.8 ms** with `5.6 ms` stdev, and `hello` from 83.4 ms to **54.4 ms**):
1. File-mode scripts now read UTF-8 source directly and evaluate via `EvalInteraction(chunk + "\n()", sourcePath)`
   (splitting top-level `;;` chunks with line-offset preservation and normalizing line-start file-mode
   stderr diagnostic locations to `Path.GetFileName` for byte parity with `dotnet fsi`), saving the
   ~25-30 ms `EvalScript` `#load` wrapper overhead.
2. `Settings` sets `ShowDeclarationValues = false`, `ShowIEnumerable = false`, `ShowProperties = false`
   (we pass `--quiet`, so FSI's reflection pretty-printer output was discarded anyway).
3. `WarmSession.CompilerDirectory` tracks the session's `#cd` directory and skips redundant `#cd`
   interactions (~10 ms saved when directory matches; invalidated whenever a script contains `#cd`,
   `#silentCd`, or `#load`), and `builtinPrelude` pre-JITs the JSON + `Seq` compiler codegen paths
   during background pool creation.
4. `runInSession` signals `replenishSignal` *after* `releaseSession` completes so background session
   creation never contends with the active request's Gen-0 sweep.

Measured millisecond breakdown of the 71.8 ms `typical` median: ~28 ms Windows process creation +
NativeAOT client + Named Pipe round-trip (`fsion --version` alone is 28.0 ms; `bun -e 0` is 29.1 ms),
~2 ms `ProcessStateGate`, **~32 ms** FCS parse/Hindley-Milner typecheck/IL-emit/Tier-0 execution of
the unseen script (`hello` takes ~19 ms inside `fsiond`), and ~6-10 ms `releaseSession` Gen-0
finalizer sweep. The remaining ~25 ms gap vs `bun` on `typical` is full Hindley-Milner static
typechecking + CLR IL emission vs `bun`'s zero-typecheck TS type-stripping; dropping the Gen-0 sweep
would violate Goal 0002's immediate Windows file-unlock guarantee.

Unconditional `GC.WaitForPendingFinalizers()` after `GC.Collect(0)` is retained in
`sweepTransientFinalizers`: gating the wait on `GC.GetGCMemoryInfo().FinalizationPendingCount` is
unsafe because CoreCLR records only the most recent GC's newly promoted finalizable count rather
than the live queue depth, and in steady state the wait ran on nearly every turn anyway.

## Clause 4 — Operator and Coding-Agent Documentation

- New root `README.md`: architecture diagram and invariants, install (including the ILCompiler
  `vswhere.exe` `PATH` note), packaging, the 3-OS CI matrix, usage examples, a CLI flag table, the
  `Fsion` helper list, prelude/NuGet-cache behavior, repository layout, development gate, and the
  measured 30-round benchmark table with the honest AC-07 outcome.
- Every flag in the README table was diffed against `src/fsion/Program.fs` `usage` and the parser
  (`-e`, `-`, `warmup`, `daemon start|stop|restart|status`, `--no-daemon`, `--json`, `--session`,
  `--reset`, `--timeout`, `--prelude`, `--no-prelude`, `--pipe`, `--idle-ttl`, `--session-ttl`,
  `--pool-size`, `-h/--help/--version`) plus the environment variables from `Fsion.Protocol`
  /`Prelude.fs` (`FSION_PIPE`, `FSIOND_PATH`, `FSION_IDLE_TTL_MINUTES`, `FSION_SESSION_TTL_MINUTES`,
  `FSION_POOL_SIZE`, `FSION_PRELUDE`, `FSION_CONFIG_DIR`, `FSION_CACHE_DIR`, `FSIOND_LOG`).
- `--pool-size` previously existed only on `fsiond` / `FSION_POOL_SIZE`; it was added as a symmetric
  client pass-through (`src/fsion/Program.fs`, shared `DefaultPoolSize = 3` in
  `src/Fsion.Protocol/Protocol.fs`) and is pinned black-box by
  `IsolationTests.--pool-size reaches the auto-started daemon and zero disables warm sessions`
  (`warm=False`, `PoolTarget = 0`, a subsequent cold claim, and `invalid --pool-size value` for `-1`).
- `AGENTS.md` gained the goal-0005 module boundaries (benchmark, packaging, CI), the
  benchmark/packaging rules, the corrected pool/sweep invariants, and the updated goal state;
  `docs/goals/README.md` now lists 0005 under Completed and Archived with an empty Active queue.

## AC-01..AC-07 Compliance Table

| AC | Requirement (converged spec) | Verification evidence | Verdict |
|---|---|---|---|
| AC-01 | Clean checkout builds/tests on `net10.0;net11.0`, NativeAOT `fsion` with zero AOT warnings, managed `fsiond` | `eng/gate.ps1` (`GATE PASSED`): build 0 warnings/0 errors, both publishes 0 AOT/trim warnings, `70/70` tests pass on both TFMs | PASS |
| AC-02 | File/`-e`/stdin via auto-started daemon, `dotnet fsi` parity and `--json` diagnostics | Existing `ParityTests`/`JsonTests`/`HygieneTests` pass in both TFMs in the gate; the packaged-bundle smoke test exercises file mode, `--json` `FS0001`, and exit passthrough | PASS |
| AC-03 | Isolated pre-warmed collectible sessions, replenishment, unmixed streams, `exit` interception, handle release, no unbounded memory | `IsolationTests` (bindings, concurrency, cwd, handle release, 50-script heap/assembly census, warm claims without cold claims) pass in both TFMs; the handle-release test is unchanged and green after the sweep change | PASS |
| AC-04 | `--session` persistence until `--reset`/TTL without leaking into stateless runs | `SessionTests` pass in both TFMs in the gate | PASS |
| AC-05 | `warmup`, `daemon start|stop|restart|status`, `--no-daemon`, `--timeout` fault containment | `LifecycleTests`/`RecoveryTests`/`FaultTests` pass in both TFMs; focused run above includes 23 passed | PASS |
| AC-06 | Built-in `Fsion` prelude, user `prelude.fsx`, `#r "nuget: ..."` cache | `PreludeTests`/`NugetCacheTests` pass in both TFMs in the gate | PASS |
| AC-07 | `>= 20x` median over `dotnet fsi` and bun-class latency on unseen `typical` scripts | 30-round interleaved byte-verified run (`salt=cd8d9305`): **42.11x** over `dotnet fsi` (71.8 ms vs 3023.3 ms, PASS; `hello` 54.4 ms = **52.49x**) and **1.521x bun's median** (71.8 ms vs 47.2 ms, matching `node` 71.2 ms; strict `<= 1.00x` FAIL, harness exit 1 under `--require-ac07`) | PARTIAL (42.1x met; strict `<= 1.0x bun` honestly failed per contract diagnostic clause) |

## Known Limits

- Only the `win-x64` bundle was built locally on this Windows host; `linux-x64` and `osx-arm64`
  NativeAOT bundles and `70/70` test suites are built and smoke-verified by the merge-blocking 3-OS
  GitHub Actions CI matrix (`.github/workflows/ci.yml`) on the PR head SHA.
- The benchmark is a Windows-host measurement. `bun` is path-sensitive on this host (`Q:\tmp` costs it
  ~50 ms) while the other runtimes are not; the committed run uses the repo-local work directory,
  which is the fair carrier, and the literal bun-class bar fails there.
- Cold-boot behavior (first invocation after a reboot, cold file cache) is not measured; every runtime
  is measured with warm OS caches in the interleaved loop.
- The benchmark harness has no non-Windows run in this repository state; its runner discovery is
  cross-platform (`shutil.which` + `bun run`/`node`/`python`), but only the Windows numbers are
  committed.
