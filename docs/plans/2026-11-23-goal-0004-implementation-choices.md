# Goal 0004 — Implementation Choices

The accepted plan is [docs/goals/0004-agent-prelude-and-cached-nuget.md](../goals/0004-agent-prelude-and-cached-nuget.md),
constrained by [docs/core-design.md](../core-design.md) and
[docs/specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md).
This note records the implementation choices those documents leave open, the measured evidence plan, and
the deliberately deferred work.

## Built-in `Fsion` prelude and `--no-prelude`

- The built-in prelude is one FSI interaction appended to the existing infrastructure prelude
  (`fsionFsiState`, `exit` shadowing), so a default warm session still emits exactly one
  `FSI-ASSEMBLY` per session and the 0003 assembly-census assertions keep their meaning. It defines a
  single top-level `module Fsion` with `cwd`, `path`, `env`, `envOr`, `readText`, `writeText`, and
  `json`; a `private` value inside the module touches `System.IO`, `System.Text`,
  `System.Text.Json`, `System.Text.RegularExpressions`, `System.Net.Http`, and `System.Linq` types so
  their metadata is imported while the pool is built. No namespace is `open`ed and no unqualified
  binding is added outside the module, so scripts that ignore `Fsion` keep byte-identical behavior.
- `Fsion.cwd ()` and `Fsion.path rel` read the process state that `ProcessStateGate` installs for the
  run: the daemon sets the OS working directory to the request's working directory before evaluation,
  so the helpers are request-scoped without per-run session work. `Fsion.env`/`envOr` read the
  per-run environment snapshot the same way.
- `--no-prelude` (`ExecuteRequest.NoPrelude`, protocol version 4) evaluates only the infrastructure
  prelude: the built-in `Fsion` module and the configured user `prelude.fsx` are skipped, so `Fsion`
  is unbound (`FS0039`) while `exit`, `fsionFsiState`, cancellation, and error/stream semantics stay
  intact. `--prelude <path>` combined with `--no-prelude` is rejected at argument parsing (both
  client and daemon), matching the existing `--reset`/`--no-daemon` validation style.

## Configurable user `prelude.fsx`

- Resolution precedence is `--prelude <path>` -> `FSION_PRELUDE` env var -> user default
  (`<FSION_CONFIG_DIR>/prelude.fsx` when that variable is set, otherwise
  `%APPDATA%/fsion/prelude.fsx` on Windows and `~/.config/fsion/prelude.fsx` elsewhere) -> none. The
  user default is used only when the file exists; explicitly configured flag/env paths that do not
  exist are a client-side error. `FSION_CONFIG_DIR` is the one addition beyond the contract text: the
  Windows known-folder API ignores `APPDATA`, so without it the default-location branch could not be
  exercised black-box without writing into the operator's real profile.
- Explicit relative paths are made absolute against the client's working directory; the daemon's own
  working directory is the binary directory, so relative daemon-side resolution would be wrong. The
  resolved absolute path travels to an auto-started daemon as `--prelude <path>` (or `--no-prelude`)
  and on every `ExecuteRequest` (`PreludePath`/`NoPrelude`).
- The daemon evaluates the user prelude inside `createWarmSession` (not per run) after the built-in
  module, from the prelude's own directory, so `#r`, `#load`, `open`, and helper bindings are all
  preloaded at pool-creation time. A prelude that fails to compile aborts session creation with the
  captured compiler text instead of silently running bare. The prelude is evaluated as an FSI
  interaction, not `EvalScript`: FSI's `EvalScript` bindings measured invisible to later interactions
  in the same session (observed with a named session file turn followed by an `-e` turn on `c65d63f`),
  so `EvalScript` cannot preload anything for a run.
- Each warm session is tagged `Bare | Builtin | File(path, SHA-256 of the evaluated content)`. A run
  derives its expected tag from its request; a claim discards every pooled or named session whose tag
  differs, and the background replenisher re-hashes the configured path on each wake and recycles
  mismatched sessions. Editing `prelude.fsx` therefore refreshes the pool with no daemon restart. The
  tag carries the hash of the content the session actually evaluated, so a mid-edit creation cannot
  label stale content as fresh.
- The run's request is authoritative for the tag it executes with; the pool's desired tag is seeded
  from the daemon configuration and updated by each request, so a daemon that outlives a client-side
  configuration change converges on the configuration clients actually use instead of churning.

## Persistent `#r "nuget: ..."` resolution cache

- A single-pass scanner finds top-level `#r "nuget: ..."` (and `#r @"nuget: ..."`) lines while
  tracking line comments, nested block comments, normal, verbatim, and triple-quoted strings, so a
  directive-looking string inside a literal or comment is ignored. Directive text after `nuget:` is
  trimmed and whitespace around commas normalized; the cache key is
  `SHA-256("fsion-nuget-cache-v1" | TFM | RID | sorted distinct normalized directives | nuget.config
  fingerprint)`, stored as `<FSION_CACHE_DIR or %LOCALAPPDATA%/fsion/nuget-cache>/<key>.json`. The
  fingerprint is the digest of every `nuget.config` from the script directory up to the root plus the
  user-level `NuGet.Config`, so a config edit invalidates the entry.
- Misses go through the official `FSharp.Compiler.DependencyManager.DependencyProvider`
  (`TryFindDependencyManagerByKey` + `Resolve`) with `executionTfm`/`executionRid` from the running
  runtime and the script's directory as `implicitIncludeDir`; nothing re-implements NuGet
  resolution. `Resolutions` (resolved assembly paths) and `Roots` (native probing directories) are
  persisted. A failed official resolution is not cached and lets FSI's own directive path produce the
  familiar error text.
- Hits feed `#r @"<dll>"` and `#I @"<dir>"` lines into the session in one interaction, then evaluate
  the source with the `#r "nuget: ..."` lines blanked (replaced by nothing, keeping the newline) so
  line numbers and diagnostic spans are preserved (verified for eval mode and file mode: an error on
  line 3 still reports line 3). A hit is validated by `File.Exists` on every referenced assembly, so a
  wiped package folder degrades to a miss instead of a broken script. File mode with directives switches
  from `EvalScript` to `EvalInteraction` with the script's file name; `#cd`, `__SOURCE_DIRECTORY__`,
  `#load`, and the emitted diagnostics are equivalent because the daemon already `#cd`s to the script
  directory. The rewritten file-mode path therefore shares `EvalInteraction`'s property that top-level
  bindings persist into later named-session turns, unlike the plain `EvalScript` file path.
- The configured user `prelude.fsx` goes through the same scanner/cache path during session creation,
  so a prelude that carries a `#r "nuget: ..."` package pays official resolution once and every
  subsequent warm-session creation feeds the cached references.
- The dependency manager's own per-user disk cache (`~/.packagemanagement/nuget/Cache`) already makes
  repeated `dp.Resolve` calls ~15 ms on a warm machine, but FSI's internal directive handling measured
  ~6.9 s on a fresh daemon even with that cache present and ~18 ms for `dp.Resolve`; the black-box
  test therefore uses a unique package per invocation for the first run and requires the second,
  structurally impossible to re-resolve, run to succeed from `FSION_CACHE_DIR`.

## Protocol, client, and daemon surface

- Protocol version 4: `ExecuteRequest.PreludePath: string option` and `NoPrelude: bool`. The client
  gains `--prelude <path>` and `--no-prelude`; the daemon gains the same two arguments and the
  `FSION_PRELUDE`/`FSION_CONFIG_DIR` fallbacks. The drain-and-respawn replacement generation re-passes
  the prelude configuration, like every other daemon option.
- `fsion warmup`, `fsion daemon start|restart`, and `--no-daemon` all spawn with the client's resolved
  prelude configuration, so the warm pool is built with the prelude the next run will request.

## Verification plan (clause -> test -> first observed evidence)

| Contract clause | Test | Expected failure observed first |
|---|---|---|
| 1. Built-in `Fsion` helpers on warm sessions | `PreludeTests` built-in helpers (`ColdClaims` unchanged, file/env/json round-trips) | `Fsion.cwd()` returned `FS0039` on `c65d63f` |
| 1. `--no-prelude` hides `Fsion` and user prelude | `PreludeTests` opt-out (FS0039 plus working `exit`) | `--no-prelude` was `unknown option`, exit 1 |
| 1. Standard scripts unchanged | existing `ParityTests`/`HygieneTests`/`JsonTests` in both TFMs | n/a (regression guard) |
| 2. User prelude with a package preloaded at pool speed | `PreludeTests` configured prelude (helper + package type, `ColdClaims` unchanged) | binding `FS0039`; `--prelude` was `unknown option` |
| 2. Live prelude edit without restart | `PreludeTests` edit (new binding visible, daemon pid unchanged) | n/a (no prelude existed) |
| 2. Resolution precedence flag -> env -> config dir | `PreludeTests` precedence (`--prelude` beats `FSION_PRELUDE`; `FSION_CONFIG_DIR` default branch) | n/a |
| 3. Persistent directive cache across daemon generations | `NugetCacheTests` (run 1 unique package + full resolution; official cache purged, feed removed, empty `NUGET_PACKAGES`; run 2 different script with re-spaced directive succeeds and is faster) | with the same conditions and no cache the current binary failed `NU1301` in 11.8 s |
| 3. Directive extraction respects strings/comments | `NugetCacheTests` commented/string directive is not rewritten (no cache file, full resolution) | n/a |

The focused pre-implementation observations were: `fsion -e 'Fsion.cwd()'` -> `FS0039`;
`--no-prelude`/`--prelude` -> `unknown option` with exit 1; a fresh daemon resolving a unique local
package paid ~7 s with `dp.Resolve` bypassed, while `dp.Resolve` in a `dotnet fsi` probe returned in
15 ms once the dependency manager's own per-user disk cache existed; and the second-run structural
setup (feed removed, `NUGET_PACKAGES` empty, `.packagemanagement` entries purged) failed `NU1301` in
11.8 s on the unmodified binary, which is what `NugetCacheTests` now pins by requiring success under
the same conditions.

`docs/evidence/0004-evidence.md` records the gate run, per-clause transcripts, and the measured
numbers.

## Out of scope (later goals)

3-OS CI, packaging, and the formal `>= 20x` benchmark gate (0005). Native library probing on cache
hits is registered as `#I` directories (the contract's stated mechanism); the caching test package is
managed-only, so a package with native assets is code-reviewed but not exercised.
