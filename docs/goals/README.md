# Fsion Goal Contracts

Each numbered file in `docs/goals/` is an independently addressable outcome contract generated via `define-goal`. Creating or updating these files does not activate any goal; hand one ready goal file at a time to an execution session.

Read [Core Design](../core-design.md) for enduring architectural invariants and [Converged Specification](../specs/2026-11-22-fsion-core-design.md) for the agreed scope, non-goals, and public testing seam.

## Lifecycle Convention

- **Active queue**: Goal `0008`.
- **Archive**: Once a goal's outcome is accepted, move the completed goal file unchanged into `docs/goals/archive/`. Never rewrite archived contracts.

## Active Series

| Goal | Status | Prerequisite | Intended Outcome |
|---|---|---|---|
| [0008-stdin-exit-parity-and-state-guards.md](0008-stdin-exit-parity-and-state-guards.md) | Ready | `0007` | Demand-driven piped `stdin` forwarding in `file`/`-e` modes without open-pipe hangs, `exit <n>` parity inside `try ... with` blocks without marker leaks, and `< 0.1 ms` post-run global state guards (`Console.SetOut`, `AppDomain` handlers, culture, `Trace`) |

## Completed and Archived

| Goal | Status | Prerequisite | Intended Outcome |
|---|---|---|---|
| [0001-native-cli-and-warm-reactor-slice.md](archive/0001-native-cli-and-warm-reactor-slice.md) | Completed | None | 100% F# (`net10.0;net11.0`) NativeAOT `fsion` thin client + auto-started `fsiond` in-memory reactor over private local IPC with `dotnet fsi` output/exit parity |
| [0002-collectible-fission-pool-and-isolation.md](archive/0002-collectible-fission-pool-and-isolation.md) | Completed | `0001` | Background-replenished `collectible = true` session pool, multi-threaded `AsyncLocal` stream/context isolation, Windows file-handle hygiene, and structured `--json` diagnostics |
| [0003-stateful-sessions-and-runaway-recovery.md](archive/0003-stateful-sessions-and-runaway-recovery.md) | Completed | `0002` | Opt-in `--session <id>` multi-turn stateful sessions + `--timeout <ms>` cooperative cancellation and automatic drain-and-respawn recovery on infinite loops |
| [0004-agent-prelude-and-cached-nuget.md](archive/0004-agent-prelude-and-cached-nuget.md) | Completed | `0002` | Built-in zero-dependency `Fsion` agent prelude, configurable pre-warmed user `prelude.fsx`, and persistent `#r "nuget: ..."` resolution cache |
| [0005-cross-platform-packaging-and-benchmark-gate.md](archive/0005-cross-platform-packaging-and-benchmark-gate.md) | Completed | `0001`–`0004` | Interleaved unseen-script benchmark harness (30 measured rounds, byte-verified parity; `42.1x` median over `dotnet fsi` at 71.8 ms, strict `<= 1.00x bun` bar measured at 1.52x and reported `FAIL` per contract diagnostic clause), host-RID NativeAOT packaging with fresh-temp smoke test, 3-OS CI, and operator/agent documentation |
| [0006-hot-path-and-burst-resilience.md](archive/0006-hot-path-and-burst-resilience.md) | Completed | `0005` | Default-path hot-path slimming, two retained prelude pools, deterministic handle release, and a [measured 12-script zero-gap burst](../evidence/0006-benchmark-report.md#zero-gap-burst) |
| [0007-protocol-security-and-fault-isolation.md](archive/0007-protocol-security-and-fault-isolation.md) | Completed | `0006` | Build/TFM-isolated endpoints & version-tolerant control frames, `CurrentUserOnly` / `0700`/`0600` + `flock` + `setsid` IPC, fair FIFO cancellable `ProcessStateGate` with nested deadlock prevention, non-collateral wedged drain, named-session generation tombstones, and idle LOH compaction + Windows working-set trim |

Evidence: [../evidence/0001-evidence.md](../evidence/0001-evidence.md), [../evidence/0002-evidence.md](../evidence/0002-evidence.md), [../evidence/0003-evidence.md](../evidence/0003-evidence.md), [../evidence/0004-evidence.md](../evidence/0004-evidence.md), [../evidence/0005-evidence.md](../evidence/0005-evidence.md), [../evidence/0006-evidence.md](../evidence/0006-evidence.md), [../evidence/0007-evidence.md](../evidence/0007-evidence.md).
