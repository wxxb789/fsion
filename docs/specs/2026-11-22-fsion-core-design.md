# Fsion Core Design Specification

## Problem

`dotnet fsi` takes ~2.6 seconds per invocation on Windows (~28–36x slower than `bun`), spending ~98.5% of wall-clock time repeatedly bootstrapping the 42 MB F# compiler service and importing reference metadata. In coding-agent dynamic workflows (such as Code Mode), every script is freshly synthesized by an LLM and runs once for milliseconds to a few seconds, making traditional disk-cached JIT/NativeAOT script compilation useless (~0% cache hit rate plus Windows Defender scan penalties). Users and coding agents need a cross-platform, Windows-first F# script runner (`fsion` + background reactor daemon `fsiond`) that executes brand-new F# scripts with `bun`-class or faster warm startup (~30–70 ms) while preserving full F# typechecking, diagnostics, and `.fsx` ergonomics.

## Scope

- Build a 100% F# repository multi-targeting `net10.0;net11.0` (`q2a`, `q3c`) producing two cooperating executables:
  - `fsion`: a NativeAOT-compiled (`PublishAot=true`) native thin CLI client accepting `<script.fsx> [args...]`, `-e "<code>"`, and `stdin`, communicating with `fsiond` over private local IPC (Windows Named Pipes on Windows, Unix Domain Sockets on macOS/Linux) (`q1b`, `q2a`).
  - `fsiond`: a managed CoreCLR background reactor daemon hosting `FSharp.Compiler.Service` with pure in-memory evaluation (`Reflection.Emit`, `--optimize- --multiemit-`, zero temporary `.dll` writes on the hot path).
- Maintain a background-replenished pool of pre-warmed `collectible = true` evaluation sessions (`q5a`, `q9a`) with:
  - Default stateless one-shot fission: each invocation claims a warm session, evaluates in memory, unloads its collectible context, and deterministically sweeps pending finalizers so throwaway scripts do not leave files locked on Windows (`q6a`).
  - Optional named stateful sessions via `--session <id>` (plus `--reset` and idle TTL eviction) preserving bindings and in-memory values across sequential agent turns (`q5a`).
- Enforce in-process multi-threaded isolation and safety inside `fsiond` (`q6a`):
  - Per-invocation `AsyncLocal` routing for `Console.Out`, `Console.Error`, and `Console.In`.
  - Prelude shadowing of `exit` so script exit codes return cleanly to `fsion` without terminating `fsiond`.
  - Accurate per-request client working directory, environment snapshot, `__SOURCE_DIRECTORY__`, and `fsi.CommandLineArgs` semantics.
- Recover automatically from uncooperative runaway or infinite-loop scripts (`q7a`): support `--timeout <ms>` and client interrupts with cooperative `CancellationToken` signaling first, followed after grace expiry by detaching the wedged `fsiond` instance from the IPC endpoint (bounded drain/self-termination) and spawning a fresh `fsiond` generation so subsequent invocations do not stall.
- Provide zero-configuration daemon lifecycle management (`q8a`): automatic on-demand startup, idle-TTL shutdown, explicit `fsion warmup`, `fsion daemon start|stop|restart|status`, and a standalone `--no-daemon` fallback mode.
- Ship a curated BCL warmup + zero-dependency `Fsion` agent prelude + configurable user `prelude.fsx` hook (`q9a`), alongside a persistent `(TFM, RID, nuget-directives) -> resolved assembly paths` cache backed by the official SDK NuGet dependency manager (`q10a`).
- Support both raw `dotnet fsi`-compatible stream/exit-code passthrough by default and `--json` structured execution + line/column compiler diagnostics output, achieving at least a 20x median warm-pool speedup over `dotnet fsi` (and `bun`-class latency) on freshly generated unseen `typical` scripts on the same machine (`q11a`).

## Out of scope

- Public HTTP servers, remote network listeners, or MCP server surfaces (`q1b`).
- Content-addressed disk caches of compiled script `.dll` or per-script NativeAOT binaries.
- Custom untyped F# interpreters, type-strippers, or custom NuGet dependency resolvers replacing the official F# compiler / SDK NuGet dependency manager.
- Administrator/root system services (Windows Services, `systemd` daemons).
- Interactive terminal REPL line editors or IDE Language Server Protocol (LSP) endpoints.

## Acceptance

- **AC-01**: Clean checkout builds and tests cleanly across `net10.0` and `net11.0` (`q3c`), producing a NativeAOT `fsion` thin client binary with zero AOT analysis warnings/errors and a managed `fsiond` daemon binary (`q2a`).
- **AC-02**: `fsion` executes `<file.fsx>`, `-e "<code>"`, and `stdin` inputs via auto-started `fsiond` over local IPC, matching `dotnet fsi` stdout, stderr, exit codes, `fsi.CommandLineArgs`, and `__SOURCE_DIRECTORY__` in default mode, and emitting valid structured JSON with line/column compiler diagnostics under `--json` (`q1b`, `q6a`, `q11a`).
- **AC-03**: Default invocations run in isolated pre-warmed collectible sessions that replenish in the background, keep concurrent stdout/stderr streams unmixed, intercept `exit <n>` without killing `fsiond`, release file handles opened by scripts on Windows after completion, and do not leak unbounded memory across repeated dynamic-script runs (`q5a`, `q6a`).
- **AC-04**: Passing `--session <id>` persists bindings and values across calls until `--reset` or idle TTL expiry without leaking state into default stateless runs or other named sessions (`q5a`).
- **AC-05**: Lifecycle and fault-containment controls (`fsion warmup`, `fsion daemon start|stop|restart|status`, `--no-daemon`, and `--timeout <ms>`) work end to end; a synchronous infinite loop (`while true do ()`) fails closed on timeout, drains/evicts the wedged `fsiond` endpoint, and allows the immediately following `fsion` invocation to succeed at warm latency (`q7a`, `q8a`).
- **AC-06**: Pre-warmed sessions expose the built-in `Fsion` prelude and honor configured user `prelude.fsx` files at ~0 ms per-invocation overhead (`q9a`), while repeated `#r "nuget: ..."` directives hit the persistent resolved-path cache without re-running NuGet restore (`q10a`).
- **AC-07**: In an interleaved black-box benchmark on the host running freshly generated unseen `typical` scripts (JSON parse + env + filter/sort + output), warm `fsion` achieves at least a 20x median latency speedup over `dotnet fsi` and reaches `bun`-class latency (`q4a`, `q11a`).

## Testing seam

Black-box process CLI boundary of the compiled NativeAOT `fsion` executable (`stdin`, arguments, working directory, environment variables -> `stdout`, `stderr`, exit code, and `--json` payload) together with the local IPC lifecycle boundary of `fsiond`, exercised by automated end-to-end integration tests and an interleaved multi-round host benchmark harness (`q4a`).

## Open questions

None.
