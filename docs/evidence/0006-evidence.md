# Goal 0006 Evidence — Hot Path and Zero-Gap Burst

Contract: [0006-hot-path-and-burst-resilience.md](../goals/archive/0006-hot-path-and-burst-resilience.md)
Implementation choices: [2026-11-24-goal-0006-implementation-choices.md](../plans/2026-11-24-goal-0006-implementation-choices.md)
Campaign: [benchmark report](0006-benchmark-report.md) and [raw results](0006-benchmark-results.json)

**Status: COMPLETE.** Both full suites passed all five black-box clauses and all regression controls (`82/82` per TFM), the Round 3 Windows `win-x64` package passed all six fresh-temp smoke checks, and the packaged benchmark (`--phase-samples 12 --require-goal0006`, salt `9c5fb055`) exited `0` with all nine Goal 0006 thresholds passing (`9/9 PASS`). Both `hello` (`49.8 ms` vs `51.9 ms`) and `diverse` (`52.0 ms` vs `53.0 ms`) beat `bun` outright; the separate historical AC-07 strict `typical <= 1.00x bun` criterion remains `FAIL` (`70.3 ms` vs `55.9 ms`, `1.259x`).

## Contract Amendment Disclosure

Before archival, the Carbon goal author / operator amended the active `docs/goals/0006-hot-path-and-burst-resilience.md` contract mid-increment under GDD Phase 0: amend an unsound or infeasible clause rather than dispatching it as written. This records the amendments; it does not change the archived contract.

- **Handle unlock (scope and verification clauses 1–2):** Replace the proposed Solo No-Handle Fast Path and its unsound net OS handle-count skip with deterministic, generation-escalating sweeps and promotion of warm sessions by the replenisher before enqueue. Tighten the promoted-object control to open the stream in a helper (no top-level static), survive mid-run collections into Gen-1 or Gen-2, and require both immediate exclusive open and delete without retries. Replace the solo control with the Aggregate Handle-Drop Counterexample Control: independent closes can lower the net count even while a new stream leaks.
- **Default pool and burst:** Add a larger configurable default warm pool (eight was the contract example; the shipping pool is twelve per tag). Measure the 12-script zero-gap burst at the default rather than at `--pool-size 3`. Preserve the `< 750 ms` max and the `<= 1.25x` spaced-`typical` median criterion while warm sessions cover the burst; once drained, allow single-session creation latency.
- **Burst-quiet background window:** Replace the generic idle unload rule with explicit quiet windows (`activeRuns = 0`) for both replenishment and full GC: about 250 ms before background creation and 500 ms before `drainPendingCleanups`, with a safety ceiling of 16 pending sessions.
- **Same-harness baseline and unseen `hello`:** Use the upgraded default-path `c14f60c` baseline (`hello` 65.4 vs 52.1 ms for `bun`; `typical` 84.9 vs 52.4 ms; `diverse` 75.6 vs 53.3 ms; burst 109.9 ms median and 2,366.2 ms max). Replace the masked-config 54.4 ms comparison and `<= 1.00x bun` bar with 65.4 ms and `<= 1.05x bun`; keep the within-3-ms alternative and `>= 50x` over `dotnet fsi`. The prior Round 2 run (`4224e3e4`) beat `bun` outright: 49.1 vs 51.4 ms (0.956x).
- **Unseen `typical` and `diverse`:** Replace the masked-config `typical` baseline of 71.8 ms and the `<= 58 ms` / `>= 45x` targets with material improvements over the measured default-path baselines (84.9 ms `typical`, 75.6 ms `diverse`) and `>= 40x` over `dotnet fsi` for both. When `typical` remains above `bun`, report the measured client/IPC, daemon gate/release and FCS compile/execute phases without weakening stdout parity.

The [handle-count control](#handle-count-control) records the observed counterexample.

## Environment and commands

Host: `CPC-lhan-XF3D0M`, Windows 11 build 26300, AMD64, .NET SDK 11.0.100. Shipping candidate: Round 3 on `feat/goal-0006-hot-path-and-burst-resilience`. Commands ran serially from `Q:/repos/fsion`.

| ID | Command | Exit | Evidence |
|---|---|---:|---|
| G | `pwsh -NoProfile -File eng/gate.ps1` | 0 | `GATE PASSED` (0 warnings, 0 errors; `82/82` passed on `net10.0` and `82/82` passed on `net11.0`; 67 paths LF-clean). |
| P | `pwsh -NoProfile -File eng/package.ps1 -Framework net10.0` | 0 | `PACKAGE OK`; all six smoke operations passed: warmup, file execution, exit 7 passthrough, exit 1 JSON diagnostic, stop, and status after stop. |
| B | `FSION_BINARY=Q:/repos/fsion/artifacts/package/fsion-win-x64-net10.0/fsion.exe FSIOND_PATH=Q:/repos/fsion/artifacts/package/fsion-win-x64-net10.0/fsiond.exe python bench/run-bench.py --phase-samples 12 --require-goal0006` | 0 | Salt `9c5fb055`: 30 measured rounds (+3 warmup) across `hello`, `typical`, `diverse` with 100% byte-for-byte stdout parity across all 5 runners, 12-script zero-gap burst (`0` cold claims, `0` alive disposed sessions), 12 logged phase samples, and `9/9` Goal 0006 thresholds `PASS`. |

| G step | Observed outcome |
|---|---|
| Release build, both TFMs | 0 warnings, 0 errors |
| NativeAOT publish, both TFMs | Both clients published under `artifacts/publish/fsion/`; no AOT or trim warnings reported |
| `net10.0` CLI/IPC suite | 82 passed, 0 failed, 0 skipped (14 m 57 s) |
| `net11.0` CLI/IPC suite | 82 passed, 0 failed, 0 skipped (14 m 20 s) |
| LF check | 67 tracked and 67 non-ignored paths clean at gate time |

The new ZIP and package directory match byte-for-byte for `fsion.exe`, `fsiond.exe`, and `fsiond.dll`. The packaged client also matches G's `net10.0` client. These fingerprints identify the smoke-tested and benchmarked Round 3 shipping package:

| Package file | SHA-256 |
|---|---|
| `fsion-win-x64-net10.0.zip` | `5dc8f13bc8af97b7bef54869d82b0d5da38be12c1e9fe375dc3e17992ec47264` |
| `fsion.exe` | `adfcf7f3d5ad7e341925850e7cb1c035b78dd48cabecaaf15afa149904e293db` |
| `fsiond.exe` | `57fc923264b0af34069a8f0a206dfe71bcf8732f4d12db9f834e600445498689` |
| `fsiond.dll` | `05a24e77cbd2aa8f8254c88d24d8cee29313c196da7f2749b4fbbd0315810f66` |

## Clause verification

All five clause tests ran in both TFMs under G (exit 0). The historical pre-fix observations in the last column are carried forward; the new Round 3 controls follow the clause table.

| Clause | Black-box test and passing assertion | Pre-fix observation or replacement proof |
|---|---|---|
| 1 — Promoted-object handle hygiene | `IsolationTests`: `promoted local streams unlock before a stateless response`. Two cases force helper-local `FileStream` promotion to Gen-1 and Gen-2, then require byte `112`, immediate `FileShare.None` open and delete, without retries. | The pre-fix `net11.0` focused test exited 1: the stream reached Gen-1 and printed `112`, but exclusive open failed. A diagnostic replay logged `320 -> 316` handles and `release sweep=skipped`. |
| 2 — Aggregate handle drop | `IsolationTests`: `local streams unlock despite an aggregate handle drop`. Close 64 pre-existing handles with the new stream still live, verify the Windows process count decreased, and open and delete the test file immediately. | A pre-fix two-test probe (`promoted local streams` and `aggregate handle drop`) exited 1 (0 passed, 2 failed). The independent [handle-count control](#handle-count-control) reproduced error 32. |
| 3 — Single producer and oneshot | `IsolationTests`: `an empty pool claimant takes the session made by its in-flight producer` and `no-daemon creates just the one session it executes`. At pool target 2, the test asserts five total creations for three executions plus two retained sessions, with zero new cold claims. Oneshot creates and executes once even with `--pool-size 5`. | Old logs did not identify duplicate producer work or count oneshot creations. The replacement proof blocks the producer, confirms the third claimant waits, then counts every creation after releasing the producer. |
| 4 — Alternating preludes | `PreludeTests`: six alternating builtin/bare turns after warming both tags add zero cold claims. | The single-tag base accumulated four extra cold claims in six turns. |
| 5 — Post-burst unload and heap | `IsolationTests`: `twenty unseen zero-gap runs unload their collectible sessions`. Before the metrics barrier, the background log must show `unload released=20 alive=0`. Metrics must show 20 executions, zero live disposed sessions, at most `PoolTarget` script assemblies, post-GC heap at most 400 MiB, and growth under 64 MiB. | The old logs did not expose the background census. The replacement proof requires that census before metrics can force a collection. An earlier focused pass with pool target 3 reported 3 warm assemblies and 103,499,312 → 131,842,392 bytes. |

The focused `net11.0` regression passed after rebuilding `fsiond` without the skip (rebuild exit 0):

```bash
dotnet test tests/Fsion.Tests/Fsion.Tests.fsproj -c Release -f net11.0 --no-build --filter 'FullyQualifiedName~promoted local streams|FullyQualifiedName~aggregate handle drop|FullyQualifiedName~an empty pool claimant|FullyQualifiedName~no-daemon creates|FullyQualifiedName~alternating six|FullyQualifiedName~twenty unseen' --logger 'console;verbosity=detailed'
```

Exit 0: seven cases passed, no failures. It used the older client with default pool 3; the final G suites independently assert the default is 12. The earlier gate that used the skip exited 1 (`net10.0`: 75 passed, 1 failed, 0 skipped) and did not reach `net11.0` or LF verification.

### Round 3 regression controls

From `Q:/repos/fsion`, before changing product code and again after the fixes:

```bash
dotnet test tests/Fsion.Tests/Fsion.Tests.fsproj -c Release -f net11.0 --filter 'FullyQualifiedName~replacement daemon cold prelude|FullyQualifiedName~concurrent fsiond processes'
```

| Finding | Before: exit 1, 0/2 passed | After: exit 0, 2/2 passed |
|---|---|---|
| F1 — replacement environment | The wedge exited 124, the old daemon terminated, and the replacement's cold prelude read `leaked` instead of `<null>`. | The server passes the startup environment directly to the replacement process without changing the retiring daemon's live environment. The daemon-spawned replacement's cold prelude reads `<null>`. |
| F3 — shared daemon log | Eight concurrent launchers drove 64 distinct `fsiond` processes writing to one log; only 63 records survived. | A path-keyed cross-process mutex holds through flush and close, including after abandoned ownership. All 64 records survived and matched their original contents. |

### Handle-count control

A private Windows daemon with `--pool-size 0` collected after a leaking turn with a net handle rise (`422 -> 482`); the caller could open the file exclusively. A named setup turn then stored 64 live `ManualResetEvent` instances. A stateless turn opened an undisposed local `FileStream`, closed the 64 pre-existing events, read its first byte, and returned. The daemon saw `449 -> 431`, unchanged activity `10 -> 10`, and `release sweep=skipped`. The caller immediately invoked `CreateFileW` with share mode `0`, which returned `ERROR_SHARING_VIOLATION` (`32`); the control exited 1. This disproves the net-count skip, including a proposed equality-only variant: independent closes can cancel opens exactly.

## Baseline and refreshed packaged campaign

The carried-forward base run (salt `1c1e642f`, exit 0) used the upgraded default-path harness on pre-change binaries, all five runners, 30 measured rounds after 3 warmups, and byte parity. The campaigns used the repo-local `bench/.work/` carrier, fresh source each round, a private endpoint, and an empty app-data directory rather than setting `FSION_CONFIG_DIR`.

Baseline (salt `1c1e642f`) vs final Round 3 packaged shipping candidate (salt `9c5fb055`, `--require-goal0006` exit `0`) median / p95 / max in ms. Full per-round details live in the [generated report](0006-benchmark-report.md) and [raw results](0006-benchmark-results.json).

| Workload | Runner | Baseline (`1c1e642f`) | Shipping (`9c5fb055`) | Goal 0006 threshold & verdict |
|---|---|---:|---:|---|
| hello | `fsion` | 65.4 / 115.0 / 149.6 | **49.8** / 200.1 / 200.1 | `61.24x` over FSI (`>= 50x`), `-2.18 ms` / `0.958x` vs `bun` (`<= 3 ms` or `<= 1.05x`) — **PASS** |
| hello | `bun` | 52.1 / 205.1 / 226.5 | 51.9 / 134.7 / 204.8 | reference |
| hello | `dotnet fsi` | 3192.5 / 3827.1 / 3858.6 | 3047.0 / 3689.7 / 3912.0 | reference |
| typical | `fsion` | 84.9 / 242.9 / 278.6 | **70.3** / 380.9 / 469.8 | `46.29x` over FSI (`>= 40x`), `< 84.9 ms` baseline — **PASS** |
| typical | `bun` | 52.4 / 70.2 / 114.6 | 55.9 / 179.3 / 198.6 | reference (strict AC-07 `<= 1.00x`: `1.259x` — FAIL) |
| typical | `dotnet fsi` | 3412.5 / 3704.9 / 4430.7 | 3256.3 / 3717.6 / 3834.9 | reference |
| diverse | `fsion` | 75.6 / 166.0 / 307.3 | **52.0** / 71.3 / 72.9 | `62.41x` over FSI (`>= 40x`), `< 75.6 ms` baseline (`0.980x` vs `bun`) — **PASS** |
| diverse | `bun` | 53.3 / 138.4 / 216.5 | 53.0 / 164.1 / 204.0 | reference |
| diverse | `dotnet fsi` | 3322.1 / 3825.1 / 3921.4 | 3242.7 / 3782.3 / 3797.5 | reference |
| burst (12 zero-gap) | `fsion` | 109.9 / 2366.2 / 2366.2 | **65.2** / 219.3 / **219.3** | `0.927x` spaced `typical` (`<= 1.25x`), max `< 750 ms`, `ColdClaims` delta `0`, `AliveDisposedSessions = 0` — **PASS** |
| burst (12 zero-gap) | `bun` | 55.5 / 60.5 / 60.5 | 50.6 / 54.8 / 54.8 | reference |

Separate `typical` phase probe (`n = 12`, daemon logging enabled on an isolated endpoint): wall median `68.6 ms` (`Client + IPC = 32.2 ms`, `Daemon gate/release = 8.0 ms`, `FCS compile + exec = 28.7 ms`).

### Review rounds & verification history

- Round 1 (`96cd9f8`): Release build and both NativeAOT publishes exited 0 without warnings; before the fixes, the two new controls exited 1 on `net11.0` (20 of 512 parallel log records survived, and the second cold prelude observed `"leaked"` instead of `"<null>"`). During Round 1's initial benchmark (`c06ebf6d`), concurrent `av2nmem.py import` processes elevated host CPU and pushed `typical` median to `97.1 ms`.
- Round 2 (`1c62c82`): added `--require-goal0006` to `bench/run-bench.py`, passed `eng/gate.ps1` (`80/80` per TFM) and `eng/package.ps1 -Framework net10.0`, and passed `python bench/run-bench.py --phase-samples 12 --require-goal0006` (`9/9 PASS`, salt `4224e3e4`).
- Round 3 (shipping candidate): passed clean baseline environment explicitly to wedged-respawn replacements (`F1`), expanded the full 5-item Contract Amendment Disclosure (`F2`), guarded `Fsiond.Log` appends with a path-keyed cross-process `Mutex` (`F3`), passed `eng/gate.ps1` (`82/82` per TFM) and `eng/package.ps1 -Framework net10.0` (`6/6` smoke checks), and passed `python bench/run-bench.py --phase-samples 12 --require-goal0006` with exit `0` (`9/9` Goal 0006 thresholds `PASS`, salt `9c5fb055`).

### Diagnostic campaigns before acceptance

The original closeout campaign (salt `6de4f2db`) measured `typical` at 69.6 ms (46.62x over FSI), improving 15.3 ms over the same-harness baseline without reaching the former 58 ms target. Its `hello` (49.0 ms) and `diverse` (50.3 ms) beat `bun`. Its separate logged phase probe measured median client + IPC at 36.3 ms (residual including Windows process creation), FCS compile + execution at 26.8 ms, and daemon claim/gate/release at 7.9 ms. These are phase medians, not an additive decomposition of the unlogged median.

The first eight-session package (salt `0da9975c`, benchmark exit 0) recorded 396.8 ms burst median and 773.165 ms max; the 30-round output checks passed, but the burst thresholds did not. A logged replay measured 202.5–246.2 ms gate/release on runs 2–9 and idle unload before two queued evaluations despite fewer than 16 pending sessions. The later generation-specific sweep and claimant-aware unload brought a six-round probe to 153.4 ms burst median and 475.0 ms max, still 1.445x its spaced `typical`. Those probes are not acceptance evidence. The final package uses twelve warm sessions per retained tag to cover the default burst without relying on a synchronous refill.

A six-round daemon publishing comparison (ordinary framework-dependent vs ReadyToRun) recorded `typical` medians of 82.1 vs 74.2 ms and burst maxima of 960.5 vs 862.9 ms. Both commands exited 0 with parity but failed the burst max threshold; the probe does not establish a stable ReadyToRun speedup. ReadyToRun is enabled for host-RID bundles, not ordinary RID-less builds.

## Limits

The local gate and package campaign cover Windows `win-x64`; macOS and Linux gates + packaging smoke run in 3-OS GitHub Actions CI. The benchmark thresholds describe one 30-round campaign on `CPC-lhan-XF3D0M`, not a cross-machine latency bound. The 12-script warm burst and isolated 20-script heap control verify different workloads.
