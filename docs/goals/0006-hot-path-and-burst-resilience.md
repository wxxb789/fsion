# Goal 0006 — Hot-Path Latency Slimming & Zero-Gap Burst Resilience

## Purpose and Intended Advance

Goal 0005 proved that `fsion` beats `dotnet fsi` by `42.1x` on unseen `typical` scripts (`71.8 ms` vs `3,023.3 ms`), yet exposed two critical gaps when audited against real coding-agent workloads:

1. **Avoidable per-invocation overhead (~15–25 ms) keeps `hello` (`54.4 ms`) and `typical` (`71.8 ms`) behind `bun` (`46.3 ms` / `47.2 ms`)**, even when zero compilation cache hits are allowed:
   - Default client calls invoke Win32 shell folder resolution (`GetFolderPath(ApplicationData)`) and account lookup on every invocation (`~7–11 ms` on Windows when `FSION_CONFIG_DIR` is unset), pay async IOCP pipe setup and console codepage syscalls unnecessarily, and re-apply all ~180+ environment variables twice inside `fsiond` (`~4 ms`) even when consecutive calls share an identical environment.
   - Inside `fsiond`, every stateless run unconditionally executes a synchronous `GC.Collect(0)` + `GC.WaitForPendingFinalizers()` sweep (`~2–15 ms`) even when no OS handle was opened by the script, runs a redundant `#cd` interaction on fresh default-prelude sessions, scans prelude-emitted types during static-field cleanup, and leaves `fsiond` / FCS warm-path codegen under-prewarmed across diverse AST shapes.
2. **Back-to-back zero-gap bursts collapse into `2.3–2.7 s` tail spikes**:
   - Goal 0005's interleaved benchmark separated `fsion` invocations by ~3 seconds of `dotnet fsi`, masking pool replenishment and unload contention.
   - When an agent runs unseen scripts back to back with zero delay, a request arriving on an empty pool blocks behind the replenisher's creation gate and then builds a duplicate cold session instead of taking the newly replenished one, `--no-daemon` spawns a multi-session pool it never uses, prelude variations thrash a single global `desiredSpec`, and background unload runs two blocking compacting Gen-2 GCs (`~530–670 ms` STW pause) directly across active foreground evaluations.

This goal eliminates avoidable client and daemon hot-path overhead under the **strict zero-script-cache constraint** (every benchmarked script remains freshly synthesized and unseen), fixes pool claim, oneshot sizing, multi-tag prelude retention, and idle-deferred non-compacting session unload so zero-gap bursts stay smooth, and upgrades the benchmark harness so it cannot hide default-env client costs, AST shape overfitting, or back-to-back burst contention.

Read [../core-design.md](../core-design.md) and [../specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md) for the non-negotiable architectural invariants (zero temporary `.dll` writes on the hot path, zero script-result/compiled-script disk caching, NativeAOT `fsion` with zero warnings, deterministic Windows file-handle release before `fsion` returns).

## Prerequisite

Accepted completion of [0005-cross-platform-packaging-and-benchmark-gate.md](archive/0005-cross-platform-packaging-and-benchmark-gate.md).

## Material Scope, Constraints, and Assumptions

- **Zero-Cache Hot-Path Slimming (`fsion` + `fsiond`)**:
  - Eliminate per-invocation client shell/account/console/pipe overhead on the default unconfigured path (when `FSION_CONFIG_DIR` and `FSION_PIPE` are unset and output may be redirected), without changing precedence or observable CLI behavior.
  - Eliminate redundant per-run environment mutations and off-critical-path restoration in `fsiond` while guaranteeing that the daemon's working directory never holds a user directory lock after a run completes.
  - Preserve Goal 0002's deterministic file-unlock guarantee (undisposed `FileStream` / `StreamReader` handles opened anywhere in a throwaway script—including when mid-run Gen-0 collections promote the stream object before evaluation returns—are closed before `fsion` exits) while avoiding synchronous finalizer sweeps on solo runs that opened no new OS handles.
  - Eliminate avoidable per-claim evaluation overhead (such as redundant compiler-directory `#cd` interactions on warm sessions and scanning unchanged prelude assemblies during static cleanup) and evaluate build/warmup levers for `fsiond` itself (for example ReadyToRun publishing and broader AST warmup in the built-in prelude) without caching or memoizing user scripts.
- **Zero-Gap Burst & Multi-Prelude Pool Resilience**:
  - Cold/empty-pool claims never pay double session creation behind an in-flight replenishment, and `--no-daemon` / oneshot runs never pre-build extra warm sessions beyond the single execution they serve.
  - Alternating between at least two prelude specs (for example default built-in prelude and `--no-prelude` or a user `prelude.fsx`) does not synchronously destroy and cold-rebuild the entire warm pool on every alternating request, and file-prelude tag checks avoid hashing unchanged files repeatedly under locks.
  - Background collectible-session unload (`drainPendingCleanups`) defers full GC collection until `fsiond` has no active evaluations (with a bounded safety ceiling so sustained load still reclaims memory without unbounded growth) and avoids unnecessary compacting STW pauses during active bursts.
- **Falsifiable Benchmark Upgrade (`bench/`)**:
  - Keep `hello` and `typical` interleaved unseen-script workloads, and remove the blind spots that hid real latency:
    1. Measure `fsion` on its **default unconfigured client path** alongside isolated pipe routing (do not mask default config-path lookup via `FSION_CONFIG_DIR` unless an isolated empty-appdata home is used that still exercises the real default resolution code path).
    2. Add a **`diverse` unseen-script workload** (cycling across distinct F# constructs per round—records/DUs, pattern matching, `printfn`/`sprintf` formatting, method overload chains such as `StringBuilder`, `Seq`/`Array` pipelines, and exception handling—with unique per-round nonces and verified stdout parity against peer runners) so compiler warmup cannot overfit one AST template.
    3. Add a **`burst` zero-gap unseen-script workload** running at least **12 back-to-back freshly generated unseen `typical` scripts with zero inter-run sleep** against `fsion` (after initial warmup, recording every individual invocation's wall time, median, p95, max, `ColdClaims`, and post-drain unload health) alongside `bun` back-to-back on the same 12 scripts.
- **Out of Scope**:
  - Wire-protocol versioning, cross-build daemon coexistence, IPC ACLs, cancellable `ProcessStateGate` fairness, and non-collateral wedged drain (owned by Goal `0007`).
  - Piped `stdin` forwarding in file/`-e` modes, `try ... with` caught `exit` semantics, and post-run global state guards (owned by Goal `0008`).
  - Persistent `--stdio` client multiplexing or multi-process worker-pool re-architecture.

## Done when

- If anything only the user can supply is missing at the start of the run, stop and report at once. Otherwise take and report reversible defaults, stopping only for irreversible or out-of-bounds decisions. Do not push commits, open PRs, or publish external releases unless explicitly authorized in that run's prompt.
- Single repository gate `pwsh -NoProfile -File eng/gate.ps1` (Release build across `net10.0;net11.0`, NativeAOT `fsion` publish for both TFMs with zero warnings, full `Fsion.Tests` suite on both TFMs, LF line endings) and `pwsh -NoProfile -File eng/package.ps1 -Framework net10.0` pass with exit code `0`.
- **Verification & Falsifiability Contract** — every new capability and fix is pinned by automated black-box tests in `Fsion.Tests` (running on both `net10.0` and `net11.0`, with pre-fix failure / baseline characterization recorded in `docs/evidence/0006-evidence.md`):
  1. **Promoted-Object Handle Hygiene Control**: A throwaway script opens an undisposed `FileStream` / `StreamReader`, explicitly triggers at least one `GC.Collect(0)` *inside* the script so the undisposed stream survives into Gen-1 before the script exits, and returns; the caller immediately deletes (or opens with `FileShare.None`) that file on Windows right after `fsion` exits **without** retry sleeps, proving handle release holds even when objects survive Gen-0 and even when handle-count gating skips sweeps on handle-free solo runs.
  2. **Solo No-Handle Fast Path vs Handle-Opening Path**: Under an isolated daemon, running a handle-free script leaves zero locked files while exercising the fast release path, and running a handle-leaking script immediately afterward still releases its Windows file handle before `fsion` returns.
  3. **Single-Producer Burst Claim & Oneshot Pool=1**: Starting with a drained pool (`--pool-size 2`), firing concurrent or immediate back-to-back requests never creates more sessions than needed to satisfy the in-flight claims plus target pool watermark, and a `--no-daemon` invocation evaluates cleanly while creating **at most 1** session (`Executed = 1`, zero background extra warm sessions built).
  4. **Alternating Prelude Zero-Thrash**: Alternating 6 invocations (`builtin` -> `--no-prelude` -> `builtin` -> `--no-prelude` -> `builtin` -> `--no-prelude`) after warming both tags incurs **0 additional `ColdClaims`** after the first warm turn of each tag (whereas a single-`desiredSpec` pool would cold-claim on every switch).
  5. **Bounded Post-Burst Memory & Collectible Unload**: After a 20-script zero-gap unseen burst completes and the idle unload window settles, `GetMetrics` reports `AliveDisposedSessions = 0`, `ScriptAssemblies` bounded by warm pool capacity, and bounded post-GC heap matching Goal 0002's ceiling.
- **Measured Benchmark Campaign (`docs/evidence/0006-benchmark-report.md` + `.json`)**:
  - Run `python bench/run-bench.py` (30 interleaved warmup-preceded rounds on unseen scripts, zero concurrent test/build load, repo-local `.work/` carrier, byte-for-byte stdout parity verified on every round across `fsion`, `dotnet-fsi`, `bun`, `node`, and `python`) plus the new `diverse` and 12-run zero-gap `burst` measurements on the exact shipping binaries.
  - Record pre-change vs post-change medians, p95, and max on the same host (`CPC-lhan-XF3D0M`):
    - Unseen `hello`: `fsion` median beats or ties `bun` median (`<= 1.00x bun` or within `3 ms` host noise, down from `54.4 ms`), and achieves `>= 50x` over `dotnet fsi`.
    - Unseen `typical`: `fsion` median drops materially below Goal 0005's `71.8 ms` baseline (target `<= 58 ms`, `>= 45x` over `dotnet fsi`), with exact phase breakdown (`client + IPC` vs `daemon gate/release` vs `FCS compile+exec`) documented honestly alongside `bun`.
    - Zero-gap 12-script unseen `burst` (`--pool-size 3`): `fsion` median stays within `1.25x` of spaced `typical` and worst-case `max` across all 12 back-to-back runs stays `< 750 ms` (eliminating the `2,300–2,700 ms` double-creation + compacting-GC cliff).
- Follow the Goal Close Ritual in `AGENTS.md`: record `docs/plans/2026-11-24-goal-0006-implementation-choices.md` and `docs/evidence/0006-evidence.md` (with a clause-by-clause verification table and exact command exit codes), move `docs/goals/0006-hot-path-and-burst-resilience.md` unchanged to `docs/goals/archive/`, and update `docs/goals/README.md`, `README.md`, and `AGENTS.md`.
