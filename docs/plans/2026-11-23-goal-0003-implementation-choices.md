# Goal 0003 — Implementation Choices

The accepted plan is [docs/goals/0003-stateful-sessions-and-runaway-recovery.md](../goals/0003-stateful-sessions-and-runaway-recovery.md),
constrained by [docs/core-design.md](../core-design.md) and
[docs/specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md).
This note records the implementation choices those documents leave open, the measured evidence plan, and
the deliberately deferred work.

## Named stateful sessions

- `--session <id>` travels to `fsiond` in `ExecuteRequest.SessionId` (protocol version 3).
  `SessionHost` keeps a `Dictionary<string, NamedSessionEntry>` under one lock; each entry owns a
  `SemaphoreSlim(1,1)` gate, so runs against the same `<id>` serialize while different ids and default
  runs execute independently. An entry claims a warm pooled session on first use (cold claim if the pool
  is empty) and keeps it alive across turns: normal named returns skip `releaseSession` entirely, so
  bindings, types, opened modules, and live objects persist with no per-turn dispose/unload cost.
- `--reset` acquires the id's gate, disposes the previous session through `releaseSession` (queued
  collectible unload), and lets the same run claim a fresh session.
- Isolation follows from the data structure: default runs never look at the dictionary, other ids have
  other entries, and every named entry has its own `FsiEvaluationSession` and prelude state.
- The idle TTL is a daemon option (`--session-ttl <minutes>`, env `FSION_SESSION_TTL_MINUTES`, default
  **15 min**; the client forwards it when auto-starting a daemon). A background sweeper wakes every
  `max(250, min(5000, ttl/4))` ms, takes the entry's gate without waiting (a busy session is left alone),
  re-checks idle time after acquiring, marks the entry evicted, removes it from the dictionary, and
  disposes it through `releaseSession`. `GetMetrics` gained `NamedSessions` so a black-box test can
  observe creation and eviction; `AliveDisposedSessions` and `ScriptAssemblies` prove the unload.
- `--reset` without `--session` and `--no-daemon` with `--session` are rejected at argument parsing; a
  named session cannot outlive a one-shot daemon, and silently ignoring the combination would mislead.

## Cooperative cancellation and timeout

- `SessionHost.Execute` creates one `CancellationTokenSource` per invocation. `--timeout <ms>` travels in
  `ExecuteRequest.TimeoutMs` and arms a `Timer` (`CancelAfter` semantics); the client's connection handler
  registers a disconnect token (a pending 1-byte `ReadAsync` on the connection) that cancels the same
  source. The token is set on the session's prelude state as `fsionFsiState.CancellationToken` for the
  run and passed to `EvalInteraction(..., cancellationToken = ...)`; `EvalScript` has no token overload in
  FCS 43.13.101, so file-mode scripts rely on the prelude token at runtime.
- FCS 43.13.101 exposes `EvalInteraction`/`EvalInteractionNonThrowing` with an optional
  `CancellationToken` and no `EvalScript` token overload (checked by reflection before implementing).
  FSharp.Core 11.0.101's `Async.DefaultCancellationToken` is backed by a single process-global
  `CancellationTokenSource` (`AsyncPrimitives.defaultCancellationTokenSource` just returns the static
  `$Async` field), so replacing or cancelling it per run would bleed cancellation into concurrent runs
  and permanently poison the daemon. The per-run token is therefore exposed on the prelude state and
  passed explicitly; the documented cooperative pattern is
  `Async.RunSynchronously(Async.Sleep 10000, cancellationToken = fsionFsiState.CancellationToken)`.
- The evaluation runs on a dedicated `LongRunning` task so the connection-handling thread can enforce the
  deadline without stapling itself (and so a wedged run never consumes a thread-pool slot that another
  run needs). After cancellation the caller waits a **150 ms** grace window for the worker. If the worker
  returns, the outcome is `ExitCode = 124`, `TimedOut = true` when the deadline (not a disconnect) fired,
  `"timedOut": true` in `--json`, and `fsion: script timed out after <ms> ms` appended to `stderr`; the
  daemon is untouched. A cancellation that races a normal completion does not mark the run timed out
  (`Task.WaitAny` ordering decides).
- `ExecutionResult.TimedOut` is the wire and JSON field. Timeout exit code is **124** (`timeout(1)`
  convention) in both the daemon response and the client-side deadline fallback. The client waits up to
  `timeoutMs + 2000` ms for the response and then synthesizes the same timeout result, so a daemon that
  dies mid-run cannot hang `fsion`.

## Uncooperative drain-and-respawn

- If the worker has not returned 150 ms after cancellation, `SessionHost` reports `Wedged = true` with the
  best-effort captured output snapshot (the per-run writers are lock-guarded so the still-running worker
  cannot corrupt the snapshot).
- The connection handler then, **before sending the response**: stops the listener (no new connections,
  pending pipe instance disposed), releases endpoint ownership, and schedules a hard self-termination
  (`Process.GetCurrentProcess().Kill()` 1 s after detach). It then sends the timeout `Executed` response
  over the existing connection and spawns a replacement generation (`Ipc.spawnDetached` with the same
  endpoint/idle-TTL/session-TTL/pool-size/log arguments). By the time the client returns, the old endpoint
  is detached; the replacement is either already listening (next call connects) or the next call's normal
  auto-start wins the ownership race.
- Windows endpoint ownership changed from a `Mutex` to a named `Semaphore` (`Local\fsion-endpoint-<name>`)
  because mutex ownership is thread-affine and the drain path must release it from the connection handler
  thread. Ownership is still "object exists" based: `createdNew = false` means another daemon owns the
  endpoint, and disposing the handle at detach lets the replacement create it.
- The self-termination is a backstop: normally the accept loop exits, the drain loop waits for the
  handler, `main` returns 0, and the process exits in ~0.1–1 s; the `Kill` guarantees the wedged thread
  cannot outlive the 1 s bound even if FSI's process-wide `AppDomain.ProcessExit` cleanup blocks.
- One-shot (`--no-daemon`) wedges retire the endpoint and self-terminate but do not respawn; the client's
  unique endpoint disappears and the global endpoint is unaffected.

## Protocol and client surface

- Protocol version bumped to **3**. `ExecuteRequest` gained `SessionId: string option`, `Reset: bool`,
  `TimeoutMs: int`; `ExecutionResult` gained `TimedOut: bool`; `MetricsInfo` gained `NamedSessions: int`;
  `FSION_SESSION_TTL_MINUTES` / `DefaultSessionTtlMinutes = 15.0` were added next to the existing
  environment variables. `--json` now always includes `"timedOut": true|false`.
- `Ipc.spawnDetached` moved the Windows `CreateProcessW` (`bInheritHandles=false`,
  `CREATE_NO_WINDOW | DETACHED_PROCESS`) and managed `Process.Start` spawn paths into `Fsion.Protocol` so
  both `fsion` (auto-start) and `fsiond` (replacement) use one implementation.

## Verification plan (clause → test → first observed evidence)

| Contract clause | Test | Expected failure observed first |
|---|---|---|
| 1. Multi-turn continuity + isolation | `SessionTests` continuity | `--session` was `unknown option`, exit 1 |
| 2. Reset + TTL eviction | `SessionTests` reset / TTL (`Metrics.NamedSessions`) | `--reset`/`--session-ttl` were `unknown option`, exit 1; no named sessions existed |
| 3. Cooperative timeout in place | `RecoveryTests` cooperative (exit 124, `timedOut`, same daemon pid) | `--timeout` was `unknown option`, exit 1 |
| 4. Drain-and-respawn | `RecoveryTests` wedge (bounded return, old pid gone, new generation serves) | `--timeout` was `unknown option`; without it the loop wedges the only session forever |
| 5. Client disconnect cancels in place | `RecoveryTests` disconnect (killed client, daemon pid unchanged, next turn works) | no cancellation token existed at all |

`docs/evidence/0003-evidence.md` records the gate run, per-clause transcripts, and the measured numbers.

## Out of scope (later goals)

`Fsion` agent prelude helpers, user `prelude.fsx`, and the NuGet resolved-path cache (0004); 3-OS CI,
packaging, and the formal `>= 20x` benchmark gate (0005).
