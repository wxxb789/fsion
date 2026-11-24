# Goal 0006 Evidence — Hot Path and Zero-Gap Burst

Contract: [0006-hot-path-and-burst-resilience.md](../goals/archive/0006-hot-path-and-burst-resilience.md)
Implementation choices: [2026-11-24-goal-0006-implementation-choices.md](../plans/2026-11-24-goal-0006-implementation-choices.md)
Campaign: [benchmark report](0006-benchmark-report.md) and [raw results](0006-benchmark-results.json)

**Archived closeout:** Both full suites passed all five black-box clauses, the retained Windows package passed its smoke test, and its original benchmark passed all nine Goal 0006 thresholds. **Round 1 fixes:** Both focused suites pass, but the refreshed campaign misses the `typical` median threshold. The separate historical AC-07 `typical`/`bun` criterion also still fails. Full shipping verification is pending Phase 5.

## Contract Amendment Disclosure

Before archival, the Carbon goal author / operator amended the active `docs/goals/0006-hot-path-and-burst-resilience.md` contract mid-increment under GDD Phase 0: amend an unsound or infeasible clause rather than dispatching it as written. This records the amendments; it does not change the archived contract.

- **Verification Clause 2:** The `c14f60c` draft called for skipping synchronous finalizer sweeps on handle-free solo runs. Attempt 2 used aggregate OS `GetProcessHandleCount` to decide when to skip. Closing 64 pre-existing `ManualResetEvent` handles while leaking a new `FileStream` drove the total from `449` to `431`, but the immediate exclusive open failed with Win32 `ERROR_SHARING_VIOLATION` (`32`). The operator replaced the solo fast-path clause with the **Aggregate Handle-Drop Counterexample Control** (`local streams unlock despite an aggregate handle drop`). See the [handle-count control](#handle-count-control).
- **Unseen `typical` latency:** The `c14f60c` draft inherited Goal 0005's `71.8 ms` baseline (`FSION_CONFIG_DIR` bypassed default config lookup) and targeted `<= 58 ms` and `>= 45x` over `dotnet fsi`. The upgraded default-path harness measured `84.9 ms` (`40.19x`) on the same base. The original closeout campaign (`6de4f2db`) measured `69.6 ms` (`46.62x`), a `15.3 ms` improvement that missed `58 ms` but met the operator-amended `< 84.9 ms` and `>= 40x` bars. Its `hello` (`49.0 ms`) and `diverse` (`50.3 ms`) both beat `bun`. The separate phase probe attributed `36.3 ms` to the Windows process creation + NativeAOT client + named-pipe round trip (measured as one residual), `26.8 ms` to FCS compile + execution including full typechecking and IL emission, and `7.9 ms` to daemon claim/gate/release including deterministic handle hygiene. These are phase medians, not an additive decomposition of the unlogged median. The refreshed Round 1 regression campaign is reported separately below.

## Environment and commands

Host: `CPC-lhan-XF3D0M`, Windows 11 build 26300, AMD64, .NET SDK 11.0.100. Candidate: the goal working tree on `feat/goal-0006-hot-path-and-burst-resilience`, based on `c14f60c8177ed004c30073ceb1731de7334ee1d5`. Commands ran from `Q:/repos/fsion`; full suites and benchmark campaigns did not overlap.

| ID | Command | Exit | Evidence |
|---|---|---:|---|
| G | `pwsh -NoProfile -File eng/gate.ps1` | 0 | Rerun after applying the accepted findings. `GATE PASSED`; full output in local `artifacts/apply-0006/gate.log`. |
| P | `pwsh -NoProfile -File eng/package.ps1 -Framework net10.0` | 0 | Prior run, as recorded by the operator. Six smoke operations: warmup, file execution, exit 7 passthrough, exit 1 JSON diagnostic, stop, and status after stop. Kept the intact `win-x64` package; did not republish during closeout. |
| B | `FSION_BINARY=Q:/repos/fsion/artifacts/package/fsion-win-x64-net10.0/fsion.exe FSIOND_PATH=Q:/repos/fsion/artifacts/package/fsion-win-x64-net10.0/fsiond.exe python bench/run-bench.py --phase-samples 12` | 0 | Original closeout run, as recorded by the operator. Salt `6de4f2db`; all nine Goal 0006 thresholds passed. Superseded as the current measurement by the Round 1 run below. |

| G step | Observed outcome |
|---|---|
| Release build, both TFMs | 0 warnings, 0 errors |
| NativeAOT publish, both TFMs | Both clients published under `artifacts/publish/fsion/`; no AOT or trim warnings reported |
| `net10.0` CLI/IPC suite | 78 passed, 0 failed, 0 skipped (15 m 55 s) |
| `net11.0` CLI/IPC suite | 78 passed, 0 failed, 0 skipped (15 m 57 s) |
| LF check | 67 tracked and 67 non-ignored paths clean at gate time |

The retained ZIP and extracted bundle match byte-for-byte for `fsion.exe`, `fsiond.exe`, and `fsiond.dll`. The packaged client also matches G's `net10.0` client byte-for-byte. The original closeout benchmark recorded paths, client size (4,180,480 bytes), and default pool target (12) consistent with the retained package. These are closeout fingerprints, not independently captured run-time hashes:

| Package file | SHA-256 |
|---|---|
| `fsion-win-x64-net10.0.zip` | `5c27db8408bcbf08190560db88b2cccb955cbb219a027af184a324556827587e` |
| `fsion.exe` | `3e8ddce44c3666bb9ac6cd60a2ace1fea117205a2e7156b3a9a320ff287ebc16` |
| `fsiond.exe` | `8b9187e2a65b25264bd121b8a1a80ac07605312374c78c6622f4c67c270441af` |
| `fsiond.dll` | `7d5333192e77d476decca805c339f533e8bddc7b3f96d00c632e189bd6559e3b` |

## Clause verification

All rows below ran in both TFMs under G (exit 0). The observations in the last column were carried forward from pre-fix and focused runs, not rerun during the apply stage.

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

### Handle-count control

A private Windows daemon with `--pool-size 0` collected after a leaking turn with a net handle rise (`422 -> 482`); the caller could open the file exclusively. A named setup turn then stored 64 live `ManualResetEvent` instances. A stateless turn opened an undisposed local `FileStream`, closed the 64 pre-existing events, read its first byte, and returned. The daemon saw `449 -> 431`, unchanged activity `10 -> 10`, and `release sweep=skipped`. The caller immediately invoked `CreateFileW` with share mode `0`, which returned `ERROR_SHARING_VIOLATION` (`32`); the control exited 1. This disproves the net-count skip, including a proposed equality-only variant: independent closes can cancel opens exactly.

## Baseline and refreshed packaged campaign

The carried-forward base run (salt `1c1e642f`, exit 0) used the upgraded default-path harness on pre-change binaries, all five runners, 30 measured rounds after 3 warmups, and byte parity. The campaigns used the repo-local `bench/.work/` carrier, fresh source each round, a private endpoint, and an empty app-data directory rather than setting `FSION_CONFIG_DIR`.

Baseline median / p95 / max in ms. The [generated report](0006-benchmark-report.md) and [raw results](0006-benchmark-results.json) are the source for refreshed candidate timings and thresholds.

| Workload | Runner | Baseline |
|---|---|---:|
| hello | `fsion` | 65.4 / 115.0 / 149.6 |
| hello | `bun` | 52.1 / 205.1 / 226.5 |
| hello | `dotnet fsi` | 3192.5 / 3827.1 / 3858.6 |
| typical | `fsion` | 84.9 / 242.9 / 278.6 |
| typical | `bun` | 52.4 / 70.2 / 114.6 |
| typical | `dotnet fsi` | 3412.5 / 3704.9 / 4430.7 |
| diverse | `fsion` | 75.6 / 166.0 / 307.3 |
| diverse | `bun` | 53.3 / 138.4 / 216.5 |
| diverse | `dotnet fsi` | 3322.1 / 3825.1 / 3921.4 |
| burst | `fsion` | 109.9 / 2366.2 / 2366.2 |
| burst | `bun` | 55.5 / 60.5 / 60.5 |

### Round 1 verification

- Release build and both NativeAOT publishes exited 0 without warnings. `dotnet test tests/Fsion.Tests/Fsion.Tests.fsproj -c Release -f <tfm> --no-build --filter 'FullyQualifiedName~IsolationTests|FullyQualifiedName~PreludeTests'` exited 0 with 24 passed (0 failed, 0 skipped) on each TFM. Before the fixes, the two new controls exited 1 on `net11.0`: 20 of 512 parallel log records survived, and the second cold prelude observed `"leaked"` instead of `"<null>"`.
- `pwsh -NoProfile -File eng/package.ps1 -Framework net10.0 -OutputDirectory artifacts/fix-0006-r1 -SkipSmokeTest` produced a separate NativeAOT / ReadyToRun bundle (exit 0). With `FSION_BINARY` and `FSIOND_PATH` pointing into that bundle, `python bench/run-bench.py --phase-samples 12` exited 0 (salt `c06ebf6d`): byte parity across all 90 workload/round pairs and 12 paired burst runs, plus 12 logged phase samples. Eight of nine Goal 0006 thresholds passed; `typical` median missed the baseline bar.
- A subsequent same-host run of the retained pre-review bundle with identical settings also missed the `typical` median bar (99.234 ms, salt `7494e8d7`, exit 0; `artifacts/fix-0006-r1/control-report.md`). It does not show a slowdown specific to the fix, but neither result clears the threshold. Only the candidate run updates the tracked benchmark artifacts. Run the full gate, package smoke, and final measurements in Phase 5 before claiming shipping verification.

### Diagnostic campaigns before acceptance

The first eight-session package (salt `0da9975c`, benchmark exit 0) recorded 396.8 ms burst median and 773.165 ms max; the 30-round output checks passed, but the burst thresholds did not. A logged replay measured 202.5–246.2 ms gate/release on runs 2–9 and idle unload before two queued evaluations despite fewer than 16 pending sessions. The later generation-specific sweep and claimant-aware unload brought a six-round probe to 153.4 ms burst median and 475.0 ms max, still 1.445x its spaced `typical`. Those probes are not acceptance evidence. The final package uses twelve warm sessions per retained tag to cover the default burst without relying on a synchronous refill.

A six-round daemon publishing comparison (ordinary framework-dependent vs ReadyToRun) recorded `typical` medians of 82.1 vs 74.2 ms and burst maxima of 960.5 vs 862.9 ms. Both commands exited 0 with parity but failed the burst max threshold; the probe does not establish a stable ReadyToRun speedup. ReadyToRun is enabled for host-RID bundles, not ordinary RID-less builds.

## Limits

The local gate and package campaign cover Windows `win-x64`; no macOS or Linux run was performed during closeout. The benchmark thresholds describe one 30-round campaign, not a general latency bound. The 12-script warm burst and isolated 20-script heap control verify different workloads. No commit, push, or external release was made.
