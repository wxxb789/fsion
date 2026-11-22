# Goal 0003 — Named Stateful Sessions & Runaway Fault Recovery

## Purpose and Intended Advance

Coding agents using F# dynamically have two conflicting needs: rock-solid continuity when exploring data across multiple turns, and rock-solid survival when synthesized code hangs or loops forever. In a multi-threaded CoreCLR daemon where `Thread.Abort` does not exist, a single synchronous infinite loop (`while true do ()`) would normally wedge a worker permanently or block the entire tool workflow.

This goal adds opt-in named stateful sessions (`--session <id>`) for sub-20 ms multi-turn notebook-style agent workflows, alongside cooperative timeout cancellation (`--timeout <ms>`) backed by automatic **drain-and-respawn** eviction when uncooperative code refuses to stop—guaranteeing that a bad AI-generated script never stalls subsequent `fsion` calls.

Read [../core-design.md](../core-design.md) and [../specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md) for the architectural invariants and fault-containment model.

## Prerequisite

Accepted completion of [0002-collectible-fission-pool-and-isolation.md](0002-collectible-fission-pool-and-isolation.md).

## Material Scope, Constraints, and Assumptions

- **Named Stateful Sessions (`--session <id>`)**:
  - Passing `--session <id>` binds execution to a named live evaluation session inside `fsiond`, preserving `let` bindings, types, opened modules, and live in-memory objects across sequential calls under that `<id>`.
  - Supporting controls include `--session <id> --reset` (discarding and recreating that named session cleanly) and automatic idle-TTL eviction so abandoned named sessions are disposed and unloaded automatically.
  - Named sessions remain completely isolated from default stateless one-shot runs and from other `<id>` sessions.
- **Cooperative Cancellation & Timeout (`--timeout <ms>` + Client Interrupt)**:
  - Each invocation exposes a cancellation token to async/cooperative F# code and triggers it when `--timeout <ms>` expires or when the `fsion` client disconnects/interrupts.
  - Cooperative workloads (such as `Async.Sleep` / token-observing tasks) cancel cleanly in place, returning a clear timeout/cancelled exit status (and structured status in `--json`) without evicting `fsiond`.
- **Uncooperative Runaway Drain-and-Respawn**:
  - When a script ignores cooperative cancellation past a short grace period (for example, `while true do ()` or a blocked synchronous call), `fsion` returns a deterministic non-zero timeout result promptly at the timeout boundary rather than hanging.
  - Meanwhile, the wedged `fsiond` generation atomically relinquishes/detaches the active IPC endpoint, enters a bounded draining/self-termination timer so it cannot leak a zombie background process forever, and a replacement `fsiond` generation starts immediately so the very next `fsion` invocation succeeds cleanly.

## Done when

- If anything only the user can supply is missing at the start of the run, stop and report at once. Otherwise take and report reversible defaults, stopping only for irreversible or out-of-bounds decisions.
- All `net10.0` and `net11.0` builds, NativeAOT publish checks (zero warnings), and black-box integration suites pass.
- Automated black-box tests through the compiled `fsion` binary demonstrate:
  1. **Multi-Turn Stateful Continuity**: Evaluating `let data = [1 .. 1000]` with `fsion --session alpha -e ...` and then summing `data` in a second `fsion --session alpha -e ...` call succeeds without recomputing `data`, while a default stateless call and `--session beta` cannot see `data`.
  2. **Session Reset & TTL Cleanup**: `fsion --session alpha --reset` clears prior bindings cleanly, and configuring a short session idle TTL evicts and unloads expired named sessions automatically.
  3. **Cooperative Timeout In-Place**: A cooperative async delay exceeding `--timeout 300` terminates promptly with a timeout exit code and `--json` timeout indicator while keeping the existing `fsiond` process alive (verified via daemon PID/status).
  4. **Uncooperative Infinite-Loop Drain-and-Respawn**: Running `fsion --timeout 400 -e "while true do ()"` returns a non-zero timeout outcome within a bounded margin of 400 ms, spawns/switches to a fresh `fsiond` generation, allows an immediately subsequent `fsion -e "1 + 1"` call to succeed cleanly, and terminates the drained wedged daemon process once its bounded drain timer elapses (leaving zero orphaned runaway processes).
- End the run with a concise report pairing each item above with its black-box test and process-lifecycle evidence.
