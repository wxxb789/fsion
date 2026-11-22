# Goal 0004 — Batteries-Included Agent Prelude & Zero-Cost Cached NuGet Directives

## Purpose and Intended Advance

For coding agents generating short F# workflows, startup latency has two remaining friction points beyond raw compiler boot: boilerplate ceremony for everyday tasks (JSON inspection, file/path/text IO, HTTP fetches, environment/working-directory access) and the 1.5–4 second NuGet resolution penalty whenever AI-generated scripts repeat `#r "nuget: ..."` directives across turns.

This goal equips `fsiond`'s pre-warmed session pool with a zero-dependency built-in `Fsion` agent prelude, a configurable user `prelude.fsx` hook (so favorite micro-libraries can be preloaded into the warm pool at 0 ms per-run cost), and a persistent NuGet directive resolution cache on top of the official SDK dependency manager so recurring `#r "nuget: ..."` specs resolve instantaneously after their first restore.

Read [../core-design.md](../core-design.md) and [../specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md) for the architectural invariants and non-goals.

## Prerequisite

Accepted completion of [0002-collectible-fission-pool-and-isolation.md](0002-collectible-fission-pool-and-isolation.md). *(Recommended after Goal 0003 so stateful sessions and fault recovery are already in place, though outcome-dependent only on Goal 0002's pool.)*

## Material Scope, Constraints, and Assumptions

- **Curated BCL Warmup & Zero-Dependency `Fsion` Prelude**:
  - Pre-warmed pool sessions come with high-value core BCL namespaces/assemblies warmed (`System`, `System.IO`, `System.Text`, `System.Text.Json`, `System.Text.RegularExpressions`, `System.Net.Http`, `System.Linq`) plus a compact, zero-external-dependency `Fsion` module offering ergonomic helpers tailored for concise agent scripts (request-scoped working-directory path resolution, environment variable lookup, concise UTF-8 file/JSON helpers) without polluting or breaking standard `.fsx` scripts that ignore `Fsion`.
  - Provide an opt-out flag (such as `--no-prelude` or bare compatibility mode) when strict stock-`fsi` symbol visibility is desired.
- **Configurable User `prelude.fsx` Preloading**:
  - Operators can configure a user or workspace `prelude.fsx` (resolved predictably via CLI flag -> `FSION_*` environment variable -> user config -> none by default).
  - When configured, `fsiond` evaluates `prelude.fsx` (including any `#r` / `#r "nuget: ..."` micro-libraries and `open` statements inside it) during background session-pool creation, and automatically invalidates/rebuilds warm sessions if the configured `prelude.fsx` content changes.
- **Persistent `#r "nuget: ..."` Resolution Cache**:
  - Delegate initial resolution to the official .NET SDK / FCS NuGet dependency manager (never invent a custom NuGet solver).
  - Persist a deterministic cache keyed by `(TFM, RID, normalized #r "nuget:" directive set, NuGet config fingerprint)` mapping to the resolved assembly and native probing paths on disk, so subsequent fresh scripts carrying the same `#r "nuget: ..."` directives bypass repeated MSBuild/NuGet restore overhead and bind resolved references directly into the collectible session.

## Done when

- If anything only the user can supply is missing at the start of the run, stop and report at once. Otherwise take and report reversible defaults, stopping only for irreversible or out-of-bounds decisions.
- All `net10.0` and `net11.0` builds, NativeAOT publish checks (zero warnings), and black-box integration suites pass with zero third-party runtime packages forced onto default scripts.
- Automated black-box tests and timing checks through the compiled `fsion` binary demonstrate:
  1. **Ergonomic Built-In Prelude + Compatibility**: Short dynamic scripts can use `Fsion` helpers immediately without extra imports, standard `.fsx` scripts run unchanged, and opting out via `--no-prelude` hides `Fsion`-specific prelude additions cleanly.
  2. **0 ms Preloaded User `prelude.fsx`**: Pointing configuration at a custom `prelude.fsx` that loads a small NuGet package and defines helper bindings makes both available immediately inside fresh one-shot scripts at warm-pool speed, and editing `prelude.fsx` causes subsequent runs to observe the updated prelude without manual daemon restarts.
  3. **Cached `#r "nuget: ..."` Acceleration**: Running two distinct, freshly generated one-shot scripts that share the same `#r "nuget: <Package>, <Version>"` directive shows the first run completing full official NuGet resolution and the second unseen script hitting the persistent resolution cache at a fraction of first-run latency (measured and reported side by side).
- End the run with a concise report pairing each item above with its test and timing evidence.
