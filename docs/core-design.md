# Fsion Core Design

`fsion` (CLI `fsion`, daemon `fsiond`) makes dynamically generated, short-lived F# scripts run at native-tool latency on Windows, macOS, and Linux—built first for coding agents that synthesize brand-new F# code on every turn and invoke it immediately.

Read this document before planning or implementing any [goal](goals/README.md). Each goal defines its own completion contract; the [converged specification](specs/2026-11-22-fsion-core-design.md) defines the agreed scope and testing seam. This document anchors the physical laws and architectural invariants that prevent agent drift across goals.

## 1. The Physical Reality (Why `fsion` Exists)

In AI agent dynamic workflows (Code Mode), scripts are freshly generated per turn and run for milliseconds to a few seconds:

1. **Compiled-artifact disk caches miss ~100% of the time.** Paying 2.5–15 seconds to compile `.dll` or NativeAOT binaries per script makes total wall-clock latency worse, not better.
2. **`dotnet fsi` spends ~98.5% of its 2.6 s startup on repeated setup, not on your script.** Loading the 42 MB `FSharp.Compiler.Service` assembly, scanning ~160 SDK reference assemblies, and importing `FSharp.Core` symbol tables costs ~2,150 ms. Parsing, Hindley-Milner typechecking, in-memory IL emission, and Tier-0 QuickJIT execution of a typical short script take **~30 ms** once warm.
3. **Every extra process and every temporary `.dll` on Windows is a latency cliff.** Windows process creation plus Defender adds ~45–60 ms per child process, managed CoreCLR host startup adds ~160–200 ms, and writing a temporary `.dll` to disk triggers Windows Defender real-time scanning (~400+ ms).

## 2. Non-Negotiable Architectural Invariants

Implementations have freedom over internal module layout, wire framing, and data structures, provided these invariants hold:

1. **Native Thin Client (`fsion`) + Managed Reactor Daemon (`fsiond`), 100% F# (`q2a`, `q3c`)**
   - Multi-target `net10.0;net11.0`.
   - `fsion` compiles with NativeAOT (`PublishAot=true`) so the CLI entry point pays zero CoreCLR startup tax and stays free of reflection/printf AOT traps.
   - `fsiond` runs on CoreCLR, hosts `FSharp.Compiler.Service`, and communicates with `fsion` strictly over private local IPC (Windows Named Pipes / Unix Domain Sockets, `q1b`). Never spawn intermediate `dotnet` wrapper processes on the hot path.
2. **Zero Disk-Emitted Temporary Assemblies on the Hot Path**
   - Script evaluation must emit IL in memory (`Reflection.Emit` / `FsiEvaluationSession` with `--optimize- --multiemit-`), never writing per-run `.dll` files that trigger Windows Defender scans.
3. **Background-Replenished Collectible Session Pool (`q5a`, `q9a`)**
   - Even inside a warm `fsiond` process, creating a fresh `FsiEvaluationSession` and importing core namespaces takes ~500 ms.
   - `fsiond` maintains a pre-warmed pool of `collectible = true` sessions that have already imported core BCL references and evaluated the `Fsion` prelude (plus any configured user `prelude.fsx`).
   - Default stateless runs pop a warm session in ~0 ms, evaluate the script, dispose the session so its collectible assembly load context unloads, and replenish the pool asynchronously in the background.
   - Explicit `--session <id>` runs bind to a named live session (with idle TTL and `--reset`) for multi-step stateful agent exploration.
4. **Multi-Threaded In-Process Safety & Windows Handle Hygiene (`q6a`)**
   - Prefer threads over child processes on Windows, without letting concurrent or sequential scripts corrupt each other or `fsiond`:
     - Route `Console.Out`, `Console.Error`, and `Console.In` per execution via `AsyncLocal`.
     - Shadow `exit` in the prelude with a controlled termination exception that returns the script's exit code without terminating `fsiond`.
     - Populate per-request client working directory, environment snapshot, `__SOURCE_DIRECTORY__`, and `fsi.CommandLineArgs` (with `args[0]` matching the script identifier rather than an internal path); guard any unavoidable process-global state transitions safely.
     - Deterministically clean up collectible session finalizers after one-shot runs so undisposed streams in throwaway agent scripts never leave files locked on Windows.
5. **Bounded Fault Containment: Cooperative Cancel + Drain-and-Respawn (`q7a`)**
   - CoreCLR cannot forcibly abort a runaway synchronous thread (`Thread.Abort` does not exist).
   - Every run carries a cancellation token (`--timeout` and client disconnect/interrupt). If a runaway script ignores cancellation past the grace window, `fsion` fails closed with a timeout status while `fsiond` detaches its IPC endpoint (draining to bounded self-termination) and a fresh `fsiond` generation immediately takes over the endpoint so subsequent agent calls never block.
6. **Zero-Config Lifecycle + Agent-Friendly Output (`q8a`, `q10a`, `q11a`)**
   - `fsion` auto-starts `fsiond` on demand, retires idle daemons automatically after TTL, and exposes `fsion warmup`, `fsion daemon start|stop|restart|status`, and `--no-daemon`.
   - Default output is raw, byte-faithful `stdout`/`stderr` and exit code parity with `dotnet fsi`; `--json` emits structured outcome and line/column compiler diagnostics for direct LLM consumption.
   - Repeated `#r "nuget: ..."` directives reuse a persistent `(TFM, RID, directive-set) -> resolved paths` cache on top of the SDK NuGet dependency manager so recurring package specs resolve in ~0 ms after the first restore.

## 3. Anti-Patterns & Explicit Non-Goals

Do **not** introduce any of the following during goal execution:

- Public HTTP servers, remote network listeners, or MCP servers (`q1b`).
- Content-addressed disk caches of compiled script `.dll` or per-script NativeAOT artifacts.
- Custom untyped F# interpreters, type-strippers, or custom NuGet dependency solvers replacing the official F# compiler / NuGet dependency manager.
- System-level services requiring administrator/root privileges (Windows Services, `systemd` units).
- Interactive terminal REPL editors or IDE Language Server Protocol (LSP) features.
