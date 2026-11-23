# Goal 0005 — Implementation Choices

The accepted plan is [docs/goals/archive/0005-cross-platform-packaging-and-benchmark-gate.md](../goals/archive/0005-cross-platform-packaging-and-benchmark-gate.md),
constrained by [docs/core-design.md](../core-design.md) and
[docs/specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md).
This note records the implementation choices those documents leave open, the measured evidence
plan, and the deliberately deferred work.

## Unseen-script benchmark harness (`bench/`)

- `bench/data.json` is the machine-readable benchmark definition: rounds and warmup defaults,
  `BENCH_DOMAIN`, workload list, the `typical` payload shape, the `AC-07` thresholds, and one entry
  per runner (`fsion`, `dotnet-fsi`, `bun`, `node`, `python`) with its command template and source
  extension. `bench/run-bench.py` (Python standard library only) reads it, renders fresh source per
  round, executes the interleaved rounds, verifies byte parity, and writes the report.
- Every round renders **fresh source for every runner** from a per-invocation random salt plus the
  round index: the nonce (`r<round>-<8 hex>`) appears in the printed output and in the JSON
  payload's names and scores, and an arithmetic constant (`seed * 2 + 7`) is embedded in the
  expression itself. No runner can reuse a previously compiled artifact; the salt of the harness is
  also recorded in the committed report (`salt`) so a re-run is visibly a different corpus.
- Workloads: `hello` (a single formatted write) and `typical` (parse an embedded JSON array, read
  `BENCH_DOMAIN` from the environment, filter `active`, sort by score descending and name, write
  `domain=` / one `name|score` line per row / `count=` with explicit `stdout.write`-style calls).
  Output uses `\n` in all four languages so byte parity is meaningful.
- Parity gate: a round's timings are only recorded after **every present runner** exited 0 and
  produced stdout byte-identical to the round's expected rendering; a mismatch aborts the harness
  with both outputs printed. Warmup rounds use the same generator and parity gate but are discarded.
- Interleaving: all runners execute within each round, and the runner order rotates per round so no
  runner permanently runs first. `fsion` runs its published NativeAOT client against a dedicated
  daemon (`FSION_PIPE` per harness invocation, `FSION_CONFIG_DIR` empty, `FSION_PRELUDE` cleared,
  `FSIOND_PATH` pointing at the built daemon) so a developer's `prelude.fsx` or warm daemon cannot
  skew the measurement. The default inter-round gap (bun, node, python, and one fresh `dotnet fsi`
  process ≈ 3 s) is what lets the pool replenish between `fsion` rounds.
- Work directory: `bench/.work/run-<salt>` (gitignored) inside the repository, overridable with
  `--work-dir`. The first draft used the system temp directory; measurement showed `bun` is ~2x
  slower there on this host (median 105–120 ms vs 48–56 ms) because `Q:\tmp` holds 58,335 entries,
  while `fsion`, `node`, and `python` are path-insensitive. Charging the baseline a penalty the
  implementation does not pay would be an unfair comparison, so the default carrier moved into the
  repo tree; the sensitivity is also recorded in the evidence as a measured caveat.
- Statistics: median, mean, sample stdev, min, max, and nearest-rank p95 per runner and workload,
  plus per-round `fsion` medians and a count of rounds above 5x the fsion median (cold-claim
  detector). AC-07 verdicts compare the `typical` medians against `dotnet fsi` (>= 20x) and `bun`
  (<= 1.0x); `--require-ac07` turns a missed threshold into a non-zero exit so the evidence run
  cannot silently pass.
- Outputs: `docs/evidence/0005-benchmark-report.md` (committed, generated) and
  `docs/evidence/0005-benchmark-results.json` (raw per-round timings, source/output SHA-256, runner
  environment) so the report's numbers stay checkable.

## Warm-run latency policy

The contract's `AC-07` bar is measured, not assumed. Pre-work measurement on this host showed cold
claims (pool drained by back-to-back invocations) at 1.2–3.4 s, so the harness's interleaved spacing
is part of the design. Five avoidable per-invocation costs were eliminated in `SessionHost.fs` and
`bench/run-bench.py` (cutting `typical` median from 104.4 ms to **73.3 ms** and `hello` from 83.4 ms
to **54.6 ms**):

1. File-mode scripts now read UTF-8 source directly and evaluate via `EvalInteraction(code + "\n()", sourcePath)`
   (normalizing file-mode stderr diagnostic prefixes to `Path.GetFileName` for byte parity with
   `dotnet fsi`), avoiding `EvalScript`'s ~25–30 ms synthetic `#load` module wrapper.
2. `Settings` sets `ShowDeclarationValues/ShowIEnumerable/ShowProperties = false` so FSI never spends
   reflection formatting cycles on bound values that `--quiet` discards.
3. `WarmSession.CompilerDirectory` tracks the session's compiler directory to skip redundant `#cd`
   evaluations (~10 ms saved when matching), and `builtinPrelude` pre-warms `JsonDocument` + `Seq`
   compiler codegen paths during background pool creation.
4. `runInSession` releases `replenishSignal` *after* `releaseSession` finishes so background warm-up
   never contends with the active turn's Gen-0 finalizer sweep; `sweepTransientFinalizers` skips
   `WaitForPendingFinalizers` when `FinalizationPendingCount = 0` (keeping `IsolationTests` file-unlock
   green).
5. `bench/run-bench.py` resolves runner executables via `shutil.which` once and invokes the resolved
   binary directly (avoiding version-manager shim overhead) using lean `Console.Out.Write` output.

The remaining physical floor is ~28 ms Windows process spawn + IPC plus ~32 ms FCS Hindley-Milner
typechecking + IL emission + Tier-0 execution (`73.3 ms` `fsion` vs `49.4 ms` `bun` and `71.2 ms`
`node` on `typical`; `54.6 ms` `fsion` vs `47.9 ms` `bun` and `73.4 ms` `node` on `hello`). The
`>= 20x` bar passes at **41.94x**, while the strict `<= 1.00x bun` threshold is reported honestly as
**FAIL** (`1.484x`, `--require-ac07` exit 1).

## Packaging (`eng/package.ps1`)

- One bundle per (RID, TFM): NativeAOT `fsion` + framework-dependent `fsiond` published side by
  side, `*.pdb` removed, a short `README.txt` added, and the directory archived (`.zip` on Windows,
  `.tar.gz` elsewhere) under `artifacts/package/`.
- `-Runtime` defaults to the host RID; NativeAOT cannot cross-compile to another OS, so a RID whose
  OS differs from the host fails with an explicit message instead of producing a broken bundle. The
  3-OS CI matrix runs one host RID per OS (`win-x64`, `linux-x64`, `osx-arm64` among them).
- The smoke test copies the bundle into a fresh temporary directory, sets a unique `FSION_PIPE` and
  temp `FSION_CONFIG_DIR`/`FSION_CACHE_DIR`, and deliberately does **not** set `FSIOND_PATH`, so
  the packaged `fsion` must discover the packaged `fsiond` next to itself. It then runs `warmup`, a
  `.fsx` file using `Fsion` helpers and JSON parsing, an `exit 7` passthrough, a `--json` failure
  with `FS0001` diagnostics, and `daemon stop` plus `daemon status` afterwards.

## `--pool-size` client flag

The goal's documentation clause lists `--pool-size` among the flags the README must cover, but only
`fsiond --pool-size` / `FSION_POOL_SIZE` existed. The client gains `--pool-size <n>` (`0` disables
warm sessions) resolved like `--idle-ttl`/`--session-ttl` and passed to the auto-started daemon and
its drain-and-respawn replacement; the daemon-side parsing is unchanged. A black-box test pins that
the flag reaches the daemon.

## 3-OS CI (`.github/workflows/ci.yml`)

- One job matrix over `windows-latest`, `macos-latest`, `ubuntu-latest`; each runs the repository
  gate (`eng/gate.ps1`: Release build, NativeAOT publish for both TFMs, black-box tests for both
  TFMs, LF hygiene) and then `eng/package.ps1 -Framework net10.0` (which includes the fresh-temp
  smoke test) and uploads the archive.
- `actions/setup-dotnet` installs `10.0.x` **and** `11.0.x`: the net10.0 test host needs the
  Microsoft.NETCore.App 10 runtime while the 11.0 SDK drives the build. Ubuntu additionally installs
  `clang` and `zlib1g-dev` for NativeAOT before the gate.
- The workflow is static configuration on this host (no push is authorized), so its Linux/macOS
  behavior is a residual risk recorded in the evidence, not a verified claim.

## Documentation

- New root `README.md`: architecture, installation/packaging, the full CLI/env surface verified
  against `src/fsion/Program.fs` and `src/fsiond/Program.fs`, prelude and NuGet-cache behavior, the
  measured 30-round benchmark table, and development/gate instructions.
- `AGENTS.md` gains the new module boundaries (`bench/`, `eng/package.ps1`, CI workflow), the
  packaging and benchmark-run rules, and the refreshed goal state.

## Verification plan (clause -> check -> observed evidence)

| Contract clause | Check | Observed evidence |
|---|---|---|
| Fresh unseen source per round with nonces | `python bench/run-bench.py --dry-run` (two runs differ), report `salt` + per-round nonces | per-round nonces and source SHA-256 committed for all 30 rounds |
| Byte-identical output before timings | parity gate in every round; non-zero exit on mismatch | 30/30 rounds verified across 5 runners, exit 0 each |
| `>= 20x` median vs `dotnet fsi`, bun-class | `python bench/run-bench.py --require-ac07` and the committed report | 41.94x over fsi (PASS, 73.3 ms vs 3075.5 ms); 1.484x bun (FAIL, harness exit 1); breakdown in the evidence |
| Packaging for host RID + archive + fresh-temp run | `pwsh -File eng/package.ps1 -Framework net10.0` | exit 0, `PACKAGE OK`, 27.2 MB bundle, 11.4 MB archive, full smoke transcript in the evidence |
| 3-OS CI | `.github/workflows/ci.yml` review; gate + package steps mirrored locally | static configuration only; unexecuted on GitHub runners (recorded limit) |
| README/AGENTS accuracy | flag-by-flag diff against `Program.fs` usage text; benchmark table copied from the generated report | table values match `0005-benchmark-results.json` |
| `AC-01`..`AC-06` stay green | full `eng/gate.ps1` at the end | gate exit 0 with both TFMs green (see evidence gate table) |
| `--pool-size` flag | `IsolationTests` flag case (`PoolTarget = 0` after `--pool-size 0 warmup`) | pre-change: `--pool-size` was `unknown option`, exit 1; now Passed on both TFMs |

`docs/evidence/0005-evidence.md` records the gate run, the clause-by-clause transcripts, the AC
compliance table, and the measured numbers; `docs/evidence/0005-benchmark-report.md` is the
generated benchmark report.

## Out of scope

- Any change to the protocol version, the wire format, or the daemon's pool/session semantics beyond
  the `--pool-size` client passthrough.
- Cross-OS NativeAOT publishing from one host (not supported by the toolchain; CI covers each OS).
- Running the benchmark in CI (host noise, peer-runtime availability, and the 30-round budget make
  it a development-host gate, not a CI gate).
