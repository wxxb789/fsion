# Goal 0006 Evidence — Hot Path and Zero-Gap Burst

Contract: [0006-hot-path-and-burst-resilience.md](../goals/archive/0006-hot-path-and-burst-resilience.md)  
Implementation choices: [2026-11-24-goal-0006-implementation-choices.md](../plans/2026-11-24-goal-0006-implementation-choices.md)  
Campaign: [benchmark report](0006-benchmark-report.md) and [raw results](0006-benchmark-results.json)

**Status: COMPLETE.** Both full suites passed all five black-box clauses. The retained Windows package passed its smoke test, and the packaged benchmark passed all nine Goal 0006 thresholds. The separate historical AC-07 `typical`/`bun` criterion still fails.

## Environment and commands

Host: `CPC-lhan-XF3D0M`, Windows 11 build 26300, AMD64, .NET SDK 11.0.100. Candidate: the goal working tree on `feat/goal-0006-hot-path-and-burst-resilience`, based on `c14f60c8177ed004c30073ceb1731de7334ee1d5`. Commands ran from `Q:/repos/fsion`; full suites and benchmark campaigns did not overlap.

| ID | Command | Exit | Evidence |
|---|---|---:|---|
| G | `pwsh -NoProfile -File eng/gate.ps1` | 0 | Run once during closeout. `GATE PASSED`; full output in local `artifacts/gate-goal-0006-closeout.log`. |
| P | `pwsh -NoProfile -File eng/package.ps1 -Framework net10.0` | 0 | Prior run, as recorded by the operator. Six smoke operations: warmup, file execution, exit 7 passthrough, exit 1 JSON diagnostic, stop, and status after stop. Kept the intact `win-x64` package; did not republish during closeout. |
| B | `FSION_BINARY=Q:/repos/fsion/artifacts/package/fsion-win-x64-net10.0/fsion.exe FSIOND_PATH=Q:/repos/fsion/artifacts/package/fsion-win-x64-net10.0/fsiond.exe python bench/run-bench.py --phase-samples 12` | 0 | Prior run, as recorded by the operator. Salt `6de4f2db`; 30 measured rounds after 3 warmups, byte parity for all 90 workload/round pairs across five runners, 12 paired zero-gap runs, and 12 separate phase samples. Did not rerun during closeout. |

| G step | Observed outcome |
|---|---|
| Release build, both TFMs | 0 warnings, 0 errors |
| NativeAOT publish, both TFMs | Both clients published under `artifacts/publish/fsion/`; no AOT or trim warnings reported |
| `net10.0` CLI/IPC suite | 77 passed, 0 failed, 0 skipped (15 m 40 s) |
| `net11.0` CLI/IPC suite | 77 passed, 0 failed, 0 skipped (15 m 21 s) |
| LF check | 63 tracked and 67 non-ignored paths clean at gate time |

The retained ZIP and extracted bundle match byte-for-byte for `fsion.exe`, `fsiond.exe`, and `fsiond.dll`. The packaged client also matches G's `net10.0` client byte-for-byte. The benchmark records the paths, client size (4,180,480 bytes), and default pool target (12), all consistent with the retained package. The benchmark JSON does not contain binary hashes; these are closeout fingerprints, not independently captured run-time hashes:

| Package file | SHA-256 |
|---|---|
| `fsion-win-x64-net10.0.zip` | `5c27db8408bcbf08190560db88b2cccb955cbb219a027af184a324556827587e` |
| `fsion.exe` | `3e8ddce44c3666bb9ac6cd60a2ace1fea117205a2e7156b3a9a320ff287ebc16` |
| `fsiond.exe` | `8b9187e2a65b25264bd121b8a1a80ac07605312374c78c6622f4c67c270441af` |
| `fsiond.dll` | `7d5333192e77d476decca805c339f533e8bddc7b3f96d00c632e189bd6559e3b` |

## Clause verification

All rows below ran in both TFMs under G (exit 0). The observations in the last column were carried forward from pre-fix and focused runs, not rerun during closeout.

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

## Baseline and final packaged campaign

The carried-forward base run (salt `1c1e642f`, exit 0) used the upgraded default-path harness on pre-change binaries, all five runners, 30 measured rounds after 3 warmups, and byte parity. Both campaigns used the repo-local `bench/.work/` carrier, fresh source each round, a private endpoint, and an empty app-data directory rather than setting `FSION_CONFIG_DIR`. Goal 0005's historical 71.8 ms `typical` result used a different setup and is not the baseline here.

All rows are median / p95 / max in ms. The candidate numbers come from B without phase logging.

| Workload | Runner | Baseline | Packaged candidate |
|---|---|---:|---:|
| hello | `fsion` | 65.4 / 115.0 / 149.6 | 49.0 / 193.6 / 202.0 |
| hello | `bun` | 52.1 / 205.1 / 226.5 | 53.6 / 75.1 / 212.9 |
| hello | `dotnet fsi` | 3192.5 / 3827.1 / 3858.6 | 3059.0 / 3593.2 / 4077.3 |
| typical | `fsion` | 84.9 / 242.9 / 278.6 | 69.6 / 548.4 / 740.7 |
| typical | `bun` | 52.4 / 70.2 / 114.6 | 53.4 / 224.1 / 240.9 |
| typical | `dotnet fsi` | 3412.5 / 3704.9 / 4430.7 | 3246.4 / 3977.0 / 4188.0 |
| diverse | `fsion` | 75.6 / 166.0 / 307.3 | 50.3 / 76.5 / 159.2 |
| diverse | `bun` | 53.3 / 138.4 / 216.5 | 53.7 / 233.9 / 255.6 |
| diverse | `dotnet fsi` | 3322.1 / 3825.1 / 3921.4 | 3253.9 / 4072.6 / 4165.8 |
| burst | `fsion` | 109.9 / 2366.2 / 2366.2 | 66.7 / 215.9 / 215.9 |
| burst | `bun` | 55.5 / 60.5 / 60.5 | 54.9 / 188.9 / 188.9 |

- **Hello:** 62.399x over `dotnet fsi`; 4.579 ms faster than `bun` (0.915x).
- **Typical and diverse:** 46.616x and 64.647x over `dotnet fsi`, respectively. `Typical` was 1.304x `bun`, the separate AC-07 failure. `Typical` p95 rose from 242.9 ms to 548.4 ms despite the median improvement.
- **Burst:** 0.957x spaced `typical`; 215.869 ms max (<750 ms). `ColdClaims` delta fell from 3 to 0; `AliveDisposedSessions` was 0 after idle in both runs. The candidate returned to 12 warm sessions and 12 script assemblies. Its reported post-idle heap was 438,089,432 bytes after the combined campaign, not the isolated 20-script clause 5 test.
- **Separate typical phase probe:** 12 logged samples; medians 72.8 ms wall, 36.3 ms client + IPC residual, 7.9 ms daemon claim/gate/release, and 26.8 ms FCS compile + execution. Medians of separately measured phases need not sum to the wall median. `bun` does not do F# type inference or IL emission; the probe reports the FCS cost but does not attribute the entire cross-runtime difference to type checking alone.

### Diagnostic campaigns before acceptance

The first eight-session package (salt `0da9975c`, benchmark exit 0) recorded 396.8 ms burst median and 773.165 ms max; the 30-round output checks passed, but the burst thresholds did not. A logged replay measured 202.5–246.2 ms gate/release on runs 2–9 and idle unload before two queued evaluations despite fewer than 16 pending sessions. The later generation-specific sweep and claimant-aware unload brought a six-round probe to 153.4 ms burst median and 475.0 ms max, still 1.445x its spaced `typical`. Those probes are not acceptance evidence. The final package uses twelve warm sessions per retained tag to cover the default burst without relying on a synchronous refill.

A six-round daemon publishing comparison (ordinary framework-dependent vs ReadyToRun) recorded `typical` medians of 82.1 vs 74.2 ms and burst maxima of 960.5 vs 862.9 ms. Both commands exited 0 with parity but failed the burst max threshold; the probe does not establish a stable ReadyToRun speedup. ReadyToRun is enabled for host-RID bundles, not ordinary RID-less builds.

## Limits

The local gate and package campaign cover Windows `win-x64`; no macOS or Linux run was performed during closeout. The benchmark thresholds describe one 30-round campaign, not a general latency bound. The 12-script warm burst and isolated 20-script heap control verify different workloads. No commit, push, or external release was made.
