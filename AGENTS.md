# fsion — Project Agent Instructions

Fast, Windows-first F# script execution (`fsion` NativeAOT thin CLI + `fsiond` CoreCLR reactor daemon) built for coding-agent dynamic F# workflows and everyday `.fsx` scripting.

## Read First

- Architectural laws, latency budget, and forbidden anti-patterns: [docs/core-design.md](docs/core-design.md)
- Converged scope, non-goals, and black-box testing seam: [docs/specs/2026-11-22-fsion-core-design.md](docs/specs/2026-11-22-fsion-core-design.md)
- Active and archived goal contracts: [docs/goals/README.md](docs/goals/README.md) and [docs/goals/AGENTS.md](docs/goals/AGENTS.md)

## Build & Verification Rules

- **100% F# (`net10.0;net11.0`)**: Keep all product and test projects in F# under `Directory.Build.props` (`TreatWarningsAsErrors=true`, `Deterministic=true`).
- **Two shipping binaries**:
  - `fsion`: NativeAOT (`PublishAot=true`) thin client with zero AOT/trim warnings and zero `printf` reflection traps (`MakeGenericMethod`).
  - `fsiond`: CoreCLR reactor hosting `FSharp.Compiler.Service` over private local IPC (Windows Named Pipe on Windows; Unix Domain Socket on macOS/Linux).
- **Zero temporary `.dll` writes on the hot path**: Evaluate scripts in memory (`Reflection.Emit` / `FsiEvaluationSession` with `--optimize- --multiemit-`) so Windows Defender never scans per-run binaries.
- **Process & file hygiene in tests**:
  - Never kill processes globally by name (`taskkill /IM dotnet.exe`, `pkill -f dotnet`); isolate test daemon endpoints/pipes per test run and shut down only the test's own daemon instance.
  - Keep all tracked text files LF-terminated (`git ls-files --eol` -> `i/lf`).
- **Comments**: Default to zero comments; keep short inline notes only for non-obvious platform/IPC/AOT hazards.

## Goal Close Ritual

When closing an active goal `docs/goals/NNNN-<slug>.md`:
1. Move the goal file unchanged into `docs/goals/archive/NNNN-<slug>.md`.
2. Update [docs/goals/README.md](docs/goals/README.md) (`Active Series` -> `Completed and Archived`).
3. Update this `AGENTS.md` with exact project paths, build/test/publish gate commands, and newly established module boundaries so subsequent agents do not rediscover them.
