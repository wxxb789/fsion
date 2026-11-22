# docs/goals/ — Goal Contracts

## Structure & Lifecycle

- Active goal prompts live directly in `docs/goals/NNNN-<slug>.md` and are indexed in [README.md](README.md).
- Completed and accepted goals move unchanged to `docs/goals/archive/NNNN-<slug>.md`.
- Always read [../core-design.md](../core-design.md) and [../specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md) before planning or executing a goal.

## Rules

- **Never edit files under `archive/`**: archived goals are immutable historical completion contracts.
- **Never silently weaken a goal's `Done when` criteria or violate `core-design.md` invariants** (such as writing per-script temporary `.dll` files to disk or dropping NativeAOT on `fsion`) to make a check pass. Surface real blockers explicitly instead.
