# Goal 0008 Evidence — Piped Stdin Parity, Caught-Exit Semantics & In-Process State Guards

## Summary

Goal 0008 implemented demand-driven pull-on-read `stdin` forwarding across IPC (`ProtocolVersion = 6`), first-`exit` preservation and stream truncation across `try ... with` blocks, and post-run global BCL state guards (`Console.SetOut/SetError/SetIn`, `AppDomain`/`AppContext`/`AssemblyLoadContext`/`TaskScheduler` event delegates, `CultureInfo`, and `Trace.Listeners`).

## Verification

- `StateGuardTests` (4/4 passed on `net10.0`, Release):
  1. `piped stdin in file and -e modes matches dotnet fsi byte for byte including >64KB and --json`: verified >64 KB multi-line UTF-8 payload byte-identical to `dotnet fsi` in file mode, `-e` `Console.ReadLine()`, and `--json`.
  2. `unclosed idle stdin pipe does not block scripts and ReadLine returns without waiting for EOF`: verified script that ignores `stdin` completes in `< 2.5 s` with unclosed `StandardInput`, and `Console.ReadLine()` returns immediately after one flushed line without closing `StandardInput`.
  3. `exit inside try-with preserves first exit code and suppresses internal markers and post-exit output`: verified `try printfn "ok"; exit 0 with ex -> ...; exit 1` returns exit `0` + `"ok"` + empty stderr, and `try eprintfn "before"; exit 5 with _ -> printfn "swallowed"; eprintfn "after"` returns exit `5` + `"before"` + zero `@@FSION-EXIT` markers.
  4. `post-run global state guards isolate Console, AppDomain events, CultureInfo, and Trace listeners`: verified script 1 poisoning all 4 surfaces leaves script 2 on the same warm `fsiond` PID completely unpoisoned (`POISON` absent, `trace-check` absent, `clean 1.50|` intact, `de-DE` restored) with `AliveDisposedSessions = 0`.
- Full 3-OS CI (`windows-latest`, `macos-latest`, `ubuntu-latest`) runs `eng/gate.ps1` (both `net10.0` and `net11.0`) + `eng/package.ps1 -Framework net10.0` on PR #8.
