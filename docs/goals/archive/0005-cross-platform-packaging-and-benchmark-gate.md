# Goal 0005 — Cross-Platform Release Gate & Competitive Benchmark Proof

## Purpose and Intended Advance

`fsion` exists to make F# competitive with—and faster than—modern scripting runtimes like `bun` and `node` for short, dynamically generated scripts, especially on Windows where `dotnet fsi` suffers a ~28–36x cold-start penalty. Once Goals 0001–0004 are in place, that speed and cross-platform reliability must be proven reproducibly rather than assumed, and the project needs clean distribution and repository documentation so humans and coding agents can adopt `fsion` in real workflows immediately.

This goal delivers the reproducible head-to-head benchmark suite, verifies the >= 20x speedup over `dotnet fsi` and `bun`-class latency on freshly generated unseen scripts, sets up 3-OS (`windows-latest`, `macos-latest`, `ubuntu-latest`) CI and NativeAOT release packaging, and documents usage clearly.

Read [../core-design.md](../core-design.md) and [../specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md) for the acceptance criteria (`AC-01`..`AC-07`) and testing seam.

## Prerequisite

Accepted completion of [0001-native-cli-and-warm-reactor-slice.md](0001-native-cli-and-warm-reactor-slice.md), [0002-collectible-fission-pool-and-isolation.md](0002-collectible-fission-pool-and-isolation.md), [0003-stateful-sessions-and-runaway-recovery.md](0003-stateful-sessions-and-runaway-recovery.md), and [0004-agent-prelude-and-cached-nuget.md](0004-agent-prelude-and-cached-nuget.md).

## Material Scope, Constraints, and Assumptions

- **Honest Unseen-Script Benchmark Harness**:
  - Ship a reproducible interleaved black-box benchmark script/tool inside the repository that generates **brand-new, unique F# source code on every round** (preventing any hidden hash-memoization shortcut) for both `hello` and `typical` (JSON parse + environment lookup + filter/sort + formatted stdout) workloads, verifying identical output across runners before recording timings.
  - Compare `fsion` (warm pool, unseen dynamic script per round) head to head against `dotnet fsi` and installed peer runtimes (`bun`, `node`, `python` when present on the host), reporting median, mean, stdev, min, and p95 latencies.
  - Target gate (`AC-07`): on the Windows development host, warm `fsion` running unseen `typical` scripts must achieve at least a **20x median speedup over `dotnet fsi`** and reach `bun`-class latency (within noise or faster than `bun` on the same machine). If a physical bottleneck prevents hitting either bar, diagnose the exact millisecond breakdown, fix any avoidable regression in `fsion`/`fsiond`, and report measured reality honestly without fudging the harness.
- **Cross-Platform Packaging & 3-OS CI**:
  - Provide a single-command local packaging/install script or target producing ready-to-use `fsion` (NativeAOT) + `fsiond` distribution bundles for `win-x64`, `linux-x64`, and `osx-arm64` (plus current host RID), and wire a GitHub Actions workflow validating build, NativeAOT publish, and end-to-end black-box tests across Windows, macOS, and Linux.
- **Operator & Coding-Agent Documentation**:
  - Write concise, accurate root `README.md` and `AGENTS.md` covering architecture overview, installation, CLI flags (`-e`, `stdin`, `--json`, `--session`, `--timeout`, `warmup`, `daemon`), custom `prelude.fsx`, and measured benchmark tables.

## Done when

- If anything only the user can supply is missing at the start of the run, stop and report at once. Otherwise take and report reversible defaults, stopping only for irreversible or out-of-bounds decisions. Do not push commits, open PRs, or publish external releases unless explicitly authorized in that run's prompt.
- Full `net10.0` and `net11.0` builds, NativeAOT publish (zero warnings), packaging verification, and the complete black-box integration test suite pass (`AC-01` through `AC-06`).
- Running the repository's interleaved unseen-script benchmark harness on this host (at least 30 rounds after warmup, fresh generated script per round, verified output parity) produces a committed/saved benchmark report demonstrating `>= 20x` median speedup over `dotnet fsi` and `bun`-class latency (`AC-07`).
- Root `README.md`, `AGENTS.md`, and `.github/workflows/` 3-OS CI configuration are complete, internally consistent with `docs/core-design.md` and `docs/specs/2026-11-22-fsion-core-design.md`, and verified against actual CLI flags and measured numbers.
- End the run with a final compliance table mapping `AC-01`..`AC-07` from the converged spec to their concrete verification evidence.
