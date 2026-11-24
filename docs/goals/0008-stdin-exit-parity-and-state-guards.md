# Goal 0008 — Piped Stdin Parity, Caught-Exit Semantics & In-Process State Guards

## Purpose and Intended Advance

To serve as a trustworthy drop-in replacement for `dotnet fsi` in Unix/Windows pipelines and multi-turn coding-agent sessions, `fsion` must not silently alter script inputs, invert exit codes, or allow one throwaway script's BCL mutations to poison subsequent runs in the shared `fsiond` process. Code and runtime audits of `280cd94` reproduced three concrete correctness and isolation gaps:

1. **Silent Piped `stdin` Data Loss in File and `-e` Modes (`P1`)**: `fsion` only reads `stdin` when invoked as `fsion -` or with zero script arguments, and `SessionHost` hard-codes `Ambient.In = TextReader.Null`. Consequently, standard pipeline invocations like `cat data.json | fsion transform.fsx` or `echo "hello" | fsion -e "printfn \"%s\" (stdin.ReadToEnd())"` silently receive empty input (`""` / `null`) and exit `0` with wrong output—whereas eagerly reading `stdin` to EOF before every script would hang forever under agent harnesses that leave an idle open `stdin` pipe attached when the script never reads `Console.In`.
2. **Broken `exit <n>` Semantics Inside `try ... with` Blocks (`P1`/`P2`)**: `exit` is shadowed in the prelude by throwing `FsionExit`, and `SessionHost.evaluate` only inspects `Exited` and truncates the `@@FSION-EXIT-<guid>@@` marker inside the `with :? FsiCompilationException` branch:
   - Common defensive scripts (`try work (); exit 0 with ex -> eprintfn "err: %s" ex.Message; exit 1`) catch `FsionExit 0` in `with ex`, run the error handler, and exit `1` instead of `0`.
   - Scripts that swallow exceptions (`try exit 3 with _ -> printfn "after"`) return exit `0` instead of `3` **and** leak the raw internal `@@FSION-EXIT-<guid>@@` sentinel onto user `stderr`.
3. **Permanent In-Process Global State Poisoning Across Runs (`P1`)**: Because `fsiond` shares one CoreCLR `AppDomain` across evaluations:
   - Calling `Console.SetOut` / `Console.SetError` / `Console.SetIn` inside one script overwrites `Ambient.install`'s routing writers permanently, making all future runs' `printfn` / `eprintfn` output vanish into the discarded writer.
   - Registering runtime event handlers during script execution (`AppDomain.CurrentDomain.FirstChanceException`, `UnhandledException`, `ProcessExit`, `AssemblyResolve`, `ResolvingUnmanagedDll`, `TaskScheduler.UnobservedTaskException`) leaks across runs—injecting one script's callback output into subsequent scripts' `stdout` and rooting collectible `FSI-ASSEMBLY` contexts so they never unload.
   - Mutating `CultureInfo.DefaultThreadCurrentCulture` / `DefaultThreadCurrentUICulture` or `Trace.Listeners` bleeds across unrelated scripts.

This goal delivers demand-driven (lazy pull) `stdin` forwarding across IPC so pipeline scripts match `dotnet fsi` without hanging under open-stdin harnesses, fixes `exit <n>` exit-code and stream-truncation parity even across `try ... with` handlers, and installs sub-`0.1 ms` post-run global state restoration guards so shared-daemon runs cannot poison future executions.

Read [../core-design.md](../core-design.md) and [../specs/2026-11-22-fsion-core-design.md](../specs/2026-11-22-fsion-core-design.md) for the architectural invariants.

## Prerequisite

Accepted completion of [0007-protocol-security-and-fault-isolation.md](0007-protocol-security-and-fault-isolation.md).

## Material Scope, Constraints, and Assumptions

- **On-Demand Bidirectional `stdin` Forwarding (`file` and `-e` Modes)**:
  - When `fsion` runs `<script.fsx> [args...]` or `-e "<code>" [args...]`, `Console.In` / `stdin` inside the evaluated F# script reads live data from the `fsion` client's standard input (`Read`, `ReadLine`, `ReadToEnd`, `Peek`) matching `dotnet fsi` byte for byte.
  - Forwarding is **strictly demand-driven (pull-on-read)**: `fsion` must **not** read from its OS `stdin` handle unless and until the running script actually reads from `Console.In` / `stdin` (when `fsiond` requests an input chunk over the IPC connection). Scripts that do not read `Console.In` pay zero extra IPC round-trips and return immediately even when the caller holds an open, unclosed `stdin` pipe with zero bytes written.
  - `fsion -` and scriptless redirected `stdin` mode continue reading the F# program source from `stdin` as before.
- **`exit <n>` Parity Across Exception Handlers**:
  - Record the **first** `exit <n>` invocation's code and stream offsets (`stdout` and `stderr`) deterministically, and prevent broad F# `with ex ->` / `with _ ->` clauses in user scripts from silently turning `exit 0` into `exit 1` or swallowing `exit 3` into `exit 0` while leaking internal exit markers.
  - Ensure that whether evaluation exits via `FsiCompilationException` or normal return, `fsion` emits zero internal marker strings (`@@FSION-EXIT-...`) on `stdout` or `stderr`, honors the first `exit <n>` code, and truncates post-`exit` output cleanly (documenting any residual CLR `finally`/`use` disposal difference vs hard OS `exit` in `README.md` / `AGENTS.md`).
- **Post-Run Global State Guards (`< 0.1 ms` Overhead)**:
  - After every evaluation (`runInSession` `finally`, covering both stateless and named turns), inspect and restore process-wide mutable surfaces:
    1. Reinstall `Ambient` routing writers/reader whenever `Console.Out`, `Console.Error`, or `Console.In` reference changed during the run.
    2. Diff and detach runtime delegates added *during* script execution on `AppDomain.CurrentDomain` (`FirstChanceException`, `UnhandledException`, `ProcessExit`, `AssemblyResolve`), `AssemblyLoadContext.Default` (`Resolving`, `ResolvingUnmanagedDll`), and `TaskScheduler.UnobservedTaskException` so neither cross-run callback injection nor collectible-ALC rooting occurs.
    3. Snapshot and restore `CultureInfo.DefaultThreadCurrentCulture`, `CultureInfo.DefaultThreadCurrentUICulture`, `CultureInfo.CurrentCulture`, `CultureInfo.CurrentUICulture`, and `Trace.Listeners` additions.
  - Run evaluations on an explicit generous stack size where supported so deep F# recursion hits normal bounds rather than small threadpool stack limits, and ensure `Log` calls inside `SessionHost` / `NugetCache` write to `LogPath` rather than leaking into a running script's `AsyncLocal` `Console.Error` or vanishing silently when `--log` is configured.
- **Preserving Hot-Path Speed**:
  - Verify via `bench/run-bench.py` that adding lazy `stdin` framing, `exit` guards, and post-run global state restoration adds zero measurable regression to `hello`, `typical`, `diverse`, and `burst` latencies established in Goal 0006.
- **Out of Scope**:
  - Interactive terminal line-editing REPLs or raw binary `Console.OpenStandardInput()` / `OpenStandardOutput()` handle duplication across processes.

## Done when

- If anything only the user can supply is missing at the start of the run, stop and report at once. Otherwise take and report reversible defaults, stopping only for irreversible or out-of-bounds decisions. Do not push commits, open PRs, or publish external releases unless explicitly authorized in that run's prompt.
- `pwsh -NoProfile -File eng/gate.ps1` and `pwsh -NoProfile -File eng/package.ps1 -Framework net10.0` pass with exit code `0` across `net10.0` and `net11.0`.
- **Verification & Falsifiability Contract** — automated black-box tests in `Fsion.Tests` verify every behavior and negative control on both `net10.0` and `net11.0`:
  1. **Piped `stdin` Byte-for-Byte Parity (`file` and `-e`)**: Piping multi-line UTF-8 data (including >64 KB payload and line-by-line `Console.ReadLine()` stopping early before EOF) into `fsion script.fsx` and `fsion -e ...` matches `dotnet fsi` `stdout`, `stderr`, and exit code byte for byte, including under `--json` mode.
  2. **Unclosed-`stdin` Non-Blocking Negative Control**: Spawning `fsion script.fsx` (where `script.fsx` prints output and never touches `stdin` / `Console.In`) with `RedirectStandardInput = true` and **never closing or writing to `StandardInput`** completes with exit `0` in `< 2,000 ms` (proving `fsion` never blocks eagerly reading `stdin`). Conversely, a script that calls `Console.ReadLine()` while the caller writes one line and keeps `StandardInput` open receives that line immediately, prints its output, and exits `0` without waiting for EOF.
  3. **`exit <n>` Inside `try ... with` Parity**:
     - `try printfn "ok"; exit 0 with ex -> eprintfn "caught: %s" ex.Message; exit 1` exits with `0`, prints `"ok"`, and emits empty `stderr` (matching `dotnet fsi`).
     - `try eprintfn "before"; exit 5 with _ -> printfn "swallowed"; eprintfn "after"` exits with `5`, prints `"before"` on `stderr`, emits **zero** `@@FSION-EXIT` marker text anywhere on `stdout` or `stderr`, and suppresses `"swallowed"` / `"after"`.
  4. **In-Process Global State Isolation Proof**:
     - Run script 1 calling `Console.SetOut(new System.IO.StringWriter())`, `Console.SetError(new System.IO.StringWriter())`, `Console.SetIn(new System.IO.StringReader("fake"))`, registering `AppDomain.CurrentDomain.FirstChanceException.Add(fun _ -> printfn "POISON")`, setting `CultureInfo.DefaultThreadCurrentCulture <- CultureInfo.GetCultureInfo("de-DE")`, and adding a `ConsoleTraceListener` to `Trace.Listeners`.
     - Run script 2 on the **same warm `fsiond` PID** printing `printfn "clean %.2f" 1.5`, raising and catching a normal exception (`try failwith "boom" with _ -> ()`), calling `Trace.WriteLine("trace-check")`, and checking `AliveDisposedSessions` via `GetMetrics`: script 2 prints exact unpoisoned `stdout`/`stderr` (no `"POISON"`, no missing `printfn`, restored culture, no leaked `Trace` listener), and script 1's collectible assembly unloads cleanly (`AliveDisposedSessions = 0`).
- Re-run `python bench/run-bench.py` on the final shipping build and confirm in `docs/evidence/0008-evidence.md` that unseen `hello`, `typical`, `diverse`, and zero-gap `burst` latencies retain Goal 0006's gains without regression.
- Follow the Goal Close Ritual in `AGENTS.md`: record `docs/plans/2026-11-24-goal-0008-implementation-choices.md` and `docs/evidence/0008-evidence.md`, move `docs/goals/0008-stdin-exit-parity-and-state-guards.md` unchanged to `docs/goals/archive/`, and update `docs/goals/README.md`, `README.md`, and `AGENTS.md`.
