# Goal 0002 — Implementation Choices

The accepted plan is [docs/goals/0002-collectible-fission-pool-and-isolation.md](../goals/0002-collectible-fission-pool-and-isolation.md),
constrained by [docs/core-design.md](../core-design.md) and
[docs/specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md).
This note only records choices those documents leave open, the measured evidence plan, and what is
deliberately out of scope.

## Warm collectible pool

- `fsiond` keeps a background-replenished pool of pre-warmed `collectible = true`
  `FsiEvaluationSession` instances. Default watermark **3**; `--pool-size <n>` on `fsiond` and the
  `FSION_POOL_SIZE` environment variable override it (`0` disables warm sessions and makes every run
  claim cold).
- A default run dequeues a warm session in ~0 ms. If the pool is empty the run creates a session
  inline (`coldClaims` counter) so executions never stall behind replenishment. The completed run
  releases its session — disposing `IDisposable` static bindings and running a Gen-0 finalizer
  sweep synchronously — and signals the replenisher, which unloads and disposes it in the
  background; a single background task refills to the watermark while the client is already gone,
  and that task also runs with `--pool-size 0` so pending cleanups are still drained promptly.
- `fsion warmup` (the `Ping` request) blocks until `poolWarm + pendingCreations >= target`, so a
  warmed daemon has no session-creation cost on the next run's critical path.
- `Executed` runs remain stateless: every default invocation claims an unused session and disposes
  it; no binding, type, or static state survives into the next default run.

## Deterministic collectible unload (measured FCS hazard)

Measured on this host with FCS 43.13.101: a `collectible = true` session that is disposed and
force-collected *still* keeps the whole session graph alive (~27 MB per session) because FSI
installs process-wide roots that are never removed:

1. `AppDomain.CurrentDomain.ProcessExit` — an `EventHandler` from `FsiDynamicCompiler` that
   captures the session.
2. `AssemblyLoadContext.Default.ResolvingUnmanagedDll` — a `NativeDllResolveHandlerCoreClr`
   `Func<Assembly,string,IntPtr>` from the session's `DependencyProvider` (its `Dispose` is never
   called by `TcImports`), which reaches the session through `TcConfigBuilder`.
3. The `AssemblyLoadContext.AssemblyResolve` non-public static delegate field, whose added
   handlers also capture compiler state.
4. The `FSharp.Compiler.DiagnosticsLogger.diagnosticsLogger` / `buildPhase` `AsyncLocal` values left
   set on the creating/evaluating thread's execution context.
5. The `TcConfigBuilder` `tryGetMetadataSnapshot` callback closure, reachable from the
   `diagnosticsLogger` execution context even after the handler delegates are gone.

Independently, each session's collectible `FSI-ASSEMBLY` dynamic assembly keeps its top-level
bindings and `leaked@` closures in static fields; those statics root undisposed streams and file
handles, so the Windows locks from clause 3 survive until the bindings are disposed or the fields
are nulled. Without cleanup,
30 sequential create/dispose rounds grow the managed heap linearly (~27.3 MB per round,
`GC.GetTotalMemory(true)`). `fsiond` therefore, per session:

- snapshots the `ProcessExit`, `ResolvingUnmanagedDll`, and `AssemblyResolve` invocation lists
  immediately before `FsiEvaluationSession.Create` and records the delegates added by that creation
  (reference-equality set difference, not positional skip, so concurrent creators cannot
  misattribute handlers);
- removes exactly those delegates when the session is released, under the same `creationGate`
  semaphore that serializes session creation, because the runtime events are plain read-modify-write
  delegate fields and adds/removes must not interleave; the release path never takes the gate, it
  queues the removal (together with the session unload) as a `CleanupWork` item drained by the
  background replenisher — which runs with `--pool-size 0` too — and synchronously by `GetMetrics`
  before it samples;
- severs the `TcConfigBuilder` `tryGetMetadataSnapshot*` callback fields and asks the checker to
  `ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients` in that drain, breaking the
  remaining closure chain to the session;
- on the request thread before the response, disposes every `IDisposable` static binding of the
  session's `FSI-ASSEMBLY` types (a top-level `let reader = new StreamReader(...)` otherwise keeps
  its Windows file handle locked, and `initonly` fields cannot be nulled) and nulls the remaining
  static fields so those objects become collectible and their finalizers can run;
- clears the two diagnostics `AsyncLocal` values on the thread that created or evaluated the
  session;
- nulls its strong references to the session, the prelude `state` object, and the cached
  `PropertyInfo`s, and records a `WeakReference` for the `AliveDisposedSessions` metric;
- runs `GC.Collect(0, Forced, true, false)` + `GC.WaitForPendingFinalizers()` synchronously on
  release so throwaway function-local streams also close before the response, and runs the full
  `GC.Collect(2, Forced, true, true)` + `WaitForPendingFinalizers` + second collect once per
  cleanup batch in the drain so the collectible assembly really unloads and `AliveDisposedSessions`
  stays 0.

With the cleanup, 50 sequential create/eval/dispose rounds on the same host kept post-GC managed
heap at 41.0 → 44.6 MB (all but the last probe session collected), versus 41 → 831 MB without it.

## Evaluation isolation

- `fsiond` installs `AsyncLocal`-backed routing `TextWriter`/`TextReader` instances for
  `Console.Out`/`Console.Error`/`Console.In` once at startup; each run sets its per-run
  `StringWriter`s in the ambient context, so concurrent connection handlers can never write into
  each other's buffers. FSI sessions are created with those same writers.
- Process working directory and environment are OS-global, and FSI's `#cd` updates per-session
  compiler state (`implicitIncludeDir`) that 0001 parity depends on. `fsiond` therefore guards them
  with `ProcessStateGate`: runs that need the same (working directory, environment snapshot) share
  one epoch and execute concurrently; a run that needs a different epoch waits until all active
  runs have drained, then applies its snapshot, and the last run to exit restores the daemon's
  previous cwd/env. This is the "guarded unavoidable OS-global transition" the contract allows: no
  two scripts ever observe each other's path or environment, while the 4-way concurrent
  same-directory test really executes in parallel. Stream isolation remains per-connection through
  `AsyncLocal`, and the pool/replenisher still uses background threads.
- `ExecuteRequest` carries the client's full environment snapshot (`(string * string)[]`). The
  daemon applies it exactly (missing daemon-only variables are removed for the duration of the run)
  and restores its own snapshot in `finally`.
- The `fsion` client keeps sending `WorkingDirectory` as before; `#cd` is issued per run, so a
  session's creation-time directory never leaks into a run.

## Metrics request (memory-clause evidence)

`Request.GetMetrics` / `Response.Metrics` are added to the internal protocol for tests and
diagnostics:

| Field | Meaning |
|---|---|
| `PoolTarget` | configured watermark |
| `PoolWarm` | sessions currently idle in the pool |
| `ColdClaims` | runs that could not claim a warm session |
| `Executed` | completed runs |
| `ManagedHeapBytes` | `GC.GetTotalMemory(true)` after two forced collections |
| `ScriptAssemblies` | loaded `FSI-ASSEMBLY` dynamic assemblies after the forced collections |
| `AliveDisposedSessions` | `WeakReference`s to disposed sessions still reachable after the forced collections (must be 0) |

`GetStatus` stays cheap (no GC); the plain `fsion daemon status` line keeps its existing
`pid=`, `uptimeMs=`, `idleMs=`, `executed=`, `warm=` tokens so the 0001 lifecycle assertions keep
passing.

## `--json` output contract

`fsion --json` writes one UTF-8 JSON document to stdout (nothing else), built with
`System.Text.Json.Utf8JsonWriter` (NativeAOT-safe, no reflection/source generators):

```json
{
  "exitCode": 1,
  "stdout": "",
  "stderr": "eval.fsx(1,15): error FS0001: ...",
  "elapsedMs": 399.2,
  "diagnostics": [
    {
      "code": "FS0001",
      "number": 1,
      "severity": "error",
      "message": "This expression was expected to have type 'int' but here has type 'string'",
      "file": "eval.fsx",
      "startLine": 1,
      "startColumn": 14,
      "endLine": 1,
      "endColumn": 20
    }
  ]
}
```

- The throwing `Eval*` APIs construct `FsiCompilationException` with `ErrorInfos = None` in
  FCS 43.13.101, and compile errors print `file(line,col): error FSnnnn:` while a warning can
  precede a runtime failure. Only when the client asked for diagnostics (`--json`, carried as
  `ExecuteRequest.IncludeDiagnostics`) and the text contains a compile-error line does the daemon
  re-run the same source once through `EvalInteractionNonThrowing`/`EvalScriptNonThrowing` with
  console output redirected to discard writers and map the returned `FSharpDiagnostic` list; a
  failed compilation never executed user code, so nothing runs twice, and a warning before a
  runtime failure never triggers a second execution. Successful runs report an empty array;
  warnings printed on successful runs stay in the `stderr` text. Rendered compiler text numbers
  columns from 1, while the JSON `startColumn`/`endColumn` carry FCS's 0-based values (the example
  above is `eval.fsx(1,15)` in text and columns 14..20 in JSON).
- `elapsedMs` is the client-observed wall time of the whole invocation (process start through the
  daemon response), matching what an agent actually pays.
- Infra failures (daemon unreachable, unexpected response) still emit the same JSON envelope with
  `exitCode: 1`, the message in `stderr`, and an empty `diagnostics` array; argument-parse errors
  keep the text usage output.

## Verification plan (clause → test → evidence)

| Contract clause | Test | First observed evidence |
|---|---|---|
| 1. Stateless fission isolation | `IsolationTests` `stateless runs do not leak bindings` | baseline 0001 prints `123`, exit 0 for `printfn "%d" secret` after `let secret = 123` |
| 2. Concurrent stream isolation | `IsolationTests` 4 concurrent processes interleaving out/err; plus different-cwd concurrency | baseline passes only because 0001 serializes one session; new design must keep byte-exact per-process output |
| 3. Windows file-lock release | `IsolationTests` undisposed `StreamReader`/`FileStream` then caller delete | baseline: `os.remove` fails `WinError 32` after `fsion` returned |
| 4. Collectible memory stability | `IsolationTests` 50 unique scripts, `GetMetrics` heap before/after + `ScriptAssemblies` | measured FCS hazard: +27.3 MB/round without hook cleanup; 41.0→44.6 MB with it |
| 5. Structured `--json` | `JsonTests` success, `FS0001`, `FS0039`, file-mode spans | baseline: `--json` is `unknown option`, exit 1 |
| 6. Warm pool latency | `IsolationTests` spaced runs with `-e`, asserts `ColdClaims = 0` and per-run wall time | measured cold session creation ~250–300 ms (warm process) and ~3–3.5 s (first in a fresh daemon) |

`docs/evidence/0002-evidence.md` records the gate run, per-clause transcripts, and the measured
numbers.

## Out of scope (later goals)

`--session <id>` stateful sessions, `--timeout`/cancellation and drain-and-respawn (0003); the
`Fsion` prelude, user `prelude.fsx`, and NuGet resolved-path cache (0004); 3-OS CI, packaging, and
the formal >= 20x benchmark gate (0005).
