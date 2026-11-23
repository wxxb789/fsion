# Goal 0002 — Collectible Fission Pool, Multi-Threaded Isolation & Structured Diagnostics

## Purpose and Intended Advance

Goal 0001 establishes the NativeAOT `fsion` thin client and warm `fsiond` reactor, eliminating the ~2.1 s compiler bootstrap cost. However, creating a fresh evaluation session on demand still costs ~500 ms, sequential single-session reuse leaks bindings across runs, concurrent invocations mix `Console` streams, undisposed file streams in throwaway AI scripts lock files on Windows, and coding agents must scrape raw compiler text on failure.

This goal turns `fsiond` into a true multi-threaded fission reactor: stateless runs claim pre-warmed `collectible = true` evaluation sessions in ~0 ms from a background-replenished pool, concurrent runs execute safely in parallel with isolated I/O and context, unloaded sessions deterministically release Windows file locks and memory, and `fsion --json` delivers structured execution outcomes and line/column F# compiler diagnostics directly to coding agents.

Read [../core-design.md](../core-design.md) and [../specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md) for the architectural invariants and testing seam.

## Prerequisite

Accepted completion of [0001-native-cli-and-warm-reactor-slice.md](0001-native-cli-and-warm-reactor-slice.md).

## Material Scope, Constraints, and Assumptions

- **Background-Replenished Collectible Pool**: `fsiond` maintains a configurable target watermark of pre-warmed `collectible = true` evaluation sessions with core BCL namespaces already warm. Each default stateless invocation claims an unused warm session immediately, evaluates in memory without disk `.dll` emission, disposes/unloads the session on completion, and triggers asynchronous background replenishment so the ~500 ms session-creation cost stays off the caller's critical path whenever capacity is available.
- **Multi-Threaded In-Process Isolation**:
  - Concurrent script runs in `fsiond` route `Console.Out`, `Console.Error`, and `Console.In` exclusively to their own caller via `AsyncLocal` wrappers with zero cross-talk.
  - Per-request client working directory and environment variable snapshots are exposed accurately to the script; any unavoidable OS-global state transitions are safely guarded so concurrent runs cannot silently corrupt one another's paths.
  - Stateless runs do not leak `let` bindings, types, or static state from one invocation to the next.
- **Windows File-Handle Hygiene & Unload Verification**: After a one-shot collectible session completes, `fsiond` ensures pending finalizers from undisposed streams/handles opened inside the throwaway script are swept promptly so the caller can immediately rename, overwrite, or delete files touched by the script on Windows without `IOException` sharing violations, and repeated runs of distinct dynamically generated scripts do not grow managed memory unboundedly.
- **Agent-Ready `--json` Mode**: Default mode remains raw stream/exit-code passthrough; passing `--json` outputs a single valid UTF-8 JSON document capturing `exitCode`, `stdout`, `stderr`, `elapsedMs`, and structured compiler/runtime diagnostics (diagnostic code/number, severity, message, file/input name, start/end line and column).

## Done when

- If anything only the user can supply is missing at the start of the run, stop and report at once. Otherwise take and report reversible defaults, stopping only for irreversible or out-of-bounds decisions.
- All `net10.0` and `net11.0` builds, NativeAOT publish checks (zero warnings), and black-box integration suites pass.
- Automated black-box tests through the compiled `fsion` binary verify:
  1. **Stateless Fission Isolation**: Defining `let secret = 123` in one run causes a subsequent default run referencing `secret` to fail with an unbound-value compiler error.
  2. **Concurrent Stream Isolation**: Firing at least 4 concurrent `fsion` processes that interleave `stdout` and `stderr` writes returns each process's exact lines with zero missing or cross-contaminated output.
  3. **Windows File-Lock Release**: Running a throwaway script that opens and reads/writes a temporary file via `new StreamReader(...)` / `new FileStream(...)` *without* `use` or `.Dispose()`, then immediately deleting that file from the caller right after `fsion` returns, succeeds cleanly on Windows without file-in-use errors.
  4. **Collectible Memory Stability**: Executing at least 50 unique dynamically generated scripts sequentially through `fsiond` unloads collectible assemblies and keeps `fsiond` post-GC managed heap bounded rather than growing linearly without bound.
  5. **Structured `--json` Contract**: Both successful runs and runs with deliberate F# type errors (`FS0001` / `FS0039`) emit valid JSON containing accurate `exitCode`, `stdout`, `stderr`, `elapsedMs`, and exact line/column diagnostic spans.
  6. **Warm Pool Latency**: Back-to-back invocations spaced past background replenishment run freshly generated unseen scripts without paying the ~500 ms session-creation cost on the caller's critical path.
- End the run with a concise report pairing each item above with its test and measurement evidence.
