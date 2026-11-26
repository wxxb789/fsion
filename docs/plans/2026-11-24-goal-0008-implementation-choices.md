# Goal 0008 Implementation Choices

- **Demand-driven (pull-on-read) bidirectional `stdin` forwarding (`Protocol.fs`, `fsion/Program.fs`, `fsiond/Server.fs`, `fsiond/SessionHost.fs`)**:
  - `ProtocolVersion` bumped to `6`, adding `Response.StdinReadRequest(maxBytes: int)` (`17uy`) and `Request.StdinChunk(data: byte[], eof: bool)` (`6uy`).
  - `Server.fs` wraps a `PullStdinStream` inside `StreamReader` for `file` and `-e` executions. Zero IPC frames or OS stdin reads occur unless the running script actually reads `Console.In` / `stdin`, so unclosed idle pipes never block scripts and `Console.ReadLine()` returns immediately once a line arrives without waiting for EOF.
  - `fsion/Program.fs` opens `Console.OpenStandardInput()` lazily on the first `StdinReadRequest`, reads up to `min(64 KB, maxBytes)`, and replies with `StdinChunk`. `fsion -` / redirected scriptless stdin continues reading program source as before.
- **`exit <n>` parity inside `try ... with` (`SessionHost.fs`)**:
  - The prelude `exit` helper records the **first** exit invocation (`if not fsionFsiState.Exited`) and writes `exitMarker` to both `Console.Out` and `Console.Error` before raising `FsionExit code`.
  - `evaluate` invokes `applyExitIfTriggered ()` on both normal completion and `FsiCompilationException`, truncating both `outWriter` and `errWriter` at the marker and returning the first `ExitCode` so caught or swallowed `FsionExit` never flips exit codes, leaks `@@FSION-EXIT-...` markers, or emits post-`exit` output.
- **Sub-0.1 ms post-run global state guards (`Ambient.fs`, `SessionHost.fs`)**:
  - `Ambient.ensureInstalled ()` restores `Console.Out`/`Console.Error`/`Console.In` whenever a script replaced them.
  - `captureGlobalState ()` / `restoreGlobalState ()` snapshot and detach script-added delegates on `AppContext.FirstChanceException` / `UnhandledException` (the backing static fields of `AppDomain.CurrentDomain.*` in .NET 10/11), `AppDomain.ProcessExit`, `AssemblyLoadContext.AssemblyResolve` / `Default.Resolving` / `Default.ResolvingUnmanagedDll`, and `TaskScheduler.UnobservedTaskException`; restore `CultureInfo` defaults/current values; and remove added `Trace.Listeners`.
