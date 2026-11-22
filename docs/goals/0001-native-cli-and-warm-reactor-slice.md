# Goal 0001 — Native Thin Client & Warm Reactor Vertical Slice

## Purpose and Intended Advance

`fsion` (CLI `fsion`, daemon `fsiond`) aims to execute freshly generated, short-lived F# scripts at native-tool latency (~30–70 ms warm) across Windows, macOS, and Linux, giving coding agents and developers a `bun`-speed alternative to `dotnet fsi` (~2.6 s) without sacrificing F# typechecking or `.fsx` ergonomics.

This first goal delivers a working, end-to-end product baseline in `Q:/repos/fsion`: a clean 100% F# repository targeting `net10.0;net11.0` where invoking the NativeAOT-compiled `fsion` binary automatically starts or connects to a background `fsiond` reactor over private local IPC and evaluates F# scripts purely in memory. After this goal, a user or coding agent can already run `<file.fsx>`, `-e "<code>"`, and `stdin` scripts with faithful `dotnet fsi` output/exit-code parity and an immediate multi-fold startup speedup over `dotnet fsi`.

Read [../core-design.md](../core-design.md) and [../specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md) for the enduring architectural invariants and agreed project boundaries.

## Prerequisite

None.

## Material Scope, Constraints, and Assumptions

- **Pure F# & Multi-Targeting**: All product and test code is written in F# and builds cleanly across `net10.0` and `net11.0`.
- **Two Cooperating Executables**:
  - `fsion`: compiled with NativeAOT (`PublishAot=true`) with zero AOT analysis or trim warnings, paying zero managed CoreCLR startup tax on the client path.
  - `fsiond`: runs on managed CoreCLR, hosts `FSharp.Compiler.Service`, and listens on a user-scoped private local IPC endpoint (Windows Named Pipe on Windows; Unix Domain Socket on macOS/Linux).
- **Zero Disk-Emitted Temporary Assemblies**: Script evaluation inside `fsiond` happens in memory (`Reflection.Emit` / `FsiEvaluationSession` with `--optimize- --multiemit-`), never writing temporary per-script `.dll` files to disk on the hot path.
- **Inputs & Daemon Controls**: `fsion` supports `<script.fsx> [args...]`, `-e "<code>"`, `stdin`, `--no-daemon` (standalone single-run fallback), and basic `fsion daemon start|stop|restart|status` plus `fsion warmup`. Idle `fsiond` instances exit automatically after a configurable idle TTL (defaulting to a sensible reversible value such as 30 minutes).
- **Script Compatibility Baseline**: Default output passes `stdout`, `stderr`, and exit codes through verbatim without banner noise. `fsi.CommandLineArgs` (with `args[0]` reflecting the script identifier) and `__SOURCE_DIRECTORY__` match expected `.fsx` behavior, and calling `exit <n>` inside a script returns exit code `<n>` to `fsion` without terminating `fsiond`.
- **Out of Bounds**: Public HTTP/network listeners, MCP servers, disk-cached compiled script `.dll` / per-script AOT caches, custom untyped interpreters, and administrator-only OS services are excluded. Pooled collectible session fission, multi-threaded `AsyncLocal` isolation, `--json` diagnostics, named `--session`, timeout respawn, custom preludes, and NuGet resolution caching belong to Goals 0002–0005; this goal need only evaluate cleanly and safely for sequential one-shot runs.

## Done when

- If anything only the user can supply is missing at the start of the run, stop and report at once. Otherwise take and report reversible defaults, stopping only for irreversible or out-of-bounds decisions.
- A clean build and test run passes across `net10.0` and `net11.0`, and publishing `fsion` with NativeAOT succeeds with zero AOT/trim warnings and produces a native `fsion` executable alongside `fsiond`.
- Automated black-box integration tests driving the compiled NativeAOT `fsion` binary over real IPC against `fsiond` pass and demonstrate:
  1. On-demand `fsiond` auto-start, `fsion warmup`, `fsion daemon status|restart|stop`, and `--no-daemon` execution.
  2. Exact `stdout`, `stderr`, exit code, `fsi.CommandLineArgs`, and `__SOURCE_DIRECTORY__` parity with `dotnet fsi` across `<file.fsx> [args...]`, `-e "<code>"`, and `stdin` inputs.
  3. A script calling `exit 42` causes `fsion` to exit with code `42` while `fsiond` stays alive and serves the next invocation without restarting.
  4. Compiler syntax/type errors write diagnostics to `stderr` and return a non-zero exit code without crashing `fsiond`.
  5. Zero per-script temporary `.dll` files are written during warm script runs, and measured warm `fsion` median wall-clock time on a freshly generated unseen script is already dramatically faster than `dotnet fsi` on the same host.
- End the run with a concise report pairing each item above with the exact command output, test result, and timing measurement that proves it.
