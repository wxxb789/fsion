# Fsion Goal Contracts

Each numbered file in `docs/goals/` is an independently addressable outcome contract generated via `define-goal`. Creating or updating these files does not activate any goal; hand one ready goal file at a time to an execution session.

Read [Core Design](../core-design.md) for enduring architectural invariants and [Converged Specification](../specs/2026-11-22-fsion-core-design.md) for the agreed scope, non-goals, and public testing seam.

## Lifecycle Convention

- **Active queue**: none; all planned goals are completed and archived.
- **Archive**: Once a goal's outcome is accepted, move the completed goal file unchanged into `docs/goals/archive/`. Never rewrite archived contracts.

## Active Series

None.

## Completed and Archived

| Goal | Status | Prerequisite | Intended Outcome |
|---|---|---|---|
| [0001-native-cli-and-warm-reactor-slice.md](archive/0001-native-cli-and-warm-reactor-slice.md) | Completed | None | 100% F# (`net10.0;net11.0`) NativeAOT `fsion` thin client + auto-started `fsiond` in-memory reactor over private local IPC with `dotnet fsi` output/exit parity |
| [0002-collectible-fission-pool-and-isolation.md](archive/0002-collectible-fission-pool-and-isolation.md) | Completed | `0001` | Background-replenished `collectible = true` session pool, multi-threaded `AsyncLocal` stream/context isolation, Windows file-handle hygiene, and structured `--json` diagnostics |
| [0003-stateful-sessions-and-runaway-recovery.md](archive/0003-stateful-sessions-and-runaway-recovery.md) | Completed | `0002` | Opt-in `--session <id>` multi-turn stateful sessions + `--timeout <ms>` cooperative cancellation and automatic drain-and-respawn recovery on infinite loops |
| [0004-agent-prelude-and-cached-nuget.md](archive/0004-agent-prelude-and-cached-nuget.md) | Completed | `0002` | Built-in zero-dependency `Fsion` agent prelude, configurable pre-warmed user `prelude.fsx`, and persistent `#r "nuget: ..."` resolution cache |
| [0005-cross-platform-packaging-and-benchmark-gate.md](archive/0005-cross-platform-packaging-and-benchmark-gate.md) | Completed | `0001`–`0004` | Interleaved unseen-script benchmark harness (30 measured rounds, byte-verified parity; `41.9x` median over `dotnet fsi` at 73.3 ms, strict `<= 1.00x bun` bar measured at 1.48x and reported `FAIL`), host-RID NativeAOT packaging with fresh-temp smoke test, 3-OS CI, and operator/agent documentation |

Evidence: [../evidence/0001-evidence.md](../evidence/0001-evidence.md), [../evidence/0002-evidence.md](../evidence/0002-evidence.md), [../evidence/0003-evidence.md](../evidence/0003-evidence.md), [../evidence/0004-evidence.md](../evidence/0004-evidence.md), [../evidence/0005-evidence.md](../evidence/0005-evidence.md).
