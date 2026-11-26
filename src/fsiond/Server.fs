module Fsiond.Server

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Tasks
open Fsion.Ipc
open Fsion.Protocol
open Fsiond.Prelude
open Fsiond.SessionHost

[<NoComparison>]
type Options =
    { Endpoint: Endpoint
      IdleTtlMinutes: float
      SessionTtlMinutes: float
      TrimIdleMinutes: float
      Oneshot: bool
      PoolSize: int
      LogPath: string option
      Prelude: Prelude.Spec }
type [<NoEquality; NoComparison>] private Hooks =
    { EnsureWarm: Prelude.Spec -> unit
      Execute: ExecuteRequest -> CancellationToken -> ExecutionReport
      Status: unit -> StatusInfo
      Metrics: unit -> MetricsInfo
      Retire: unit -> unit
      Respawn: unit -> unit
      Stop: unit -> unit }

let private handleConnection (options: Options) (hooks: Hooks) (stream: Stream) =
    use connection = stream
    let mutable requestVersion = ProtocolVersion
    try
        match tryReceiveFrame connection with
        | None -> ()
        | Some payload ->
            if payload.Length >= 4 then
                requestVersion <- BitConverter.ToInt32(payload, 0)
            let reqVer, req = parseRequestWithVersion payload
            requestVersion <- reqVer
            match req with
            | Execute request ->
                use monitorCancellation = new CancellationTokenSource()
                use disconnects = new CancellationTokenSource()
                let monitorBuffer: byte[] = Array.zeroCreate 1
                let monitorRead = connection.ReadAsync(monitorBuffer, 0, 1, monitorCancellation.Token)

                monitorRead.ContinueWith(
                    (fun (completed: Task<int>) ->
                        if completed.IsCompletedSuccessfully && completed.Result = 0 then
                            try
                                disconnects.Cancel()
                            with _ ->
                                ()),
                    TaskScheduler.Default
                )
                |> ignore

                let started = Stopwatch.GetTimestamp()
                let report = hooks.Execute request disconnects.Token
                let daemonMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds

                let logExecuted () =
                    Log.info
                        options.LogPath
                        ("executed exit="
                         + string report.Outcome.ExitCode
                         + " timedOut="
                         + string report.Outcome.TimedOut
                         + " wedged="
                         + string report.Wedged
                         + (if options.Oneshot then " totalExecuted=" + string (hooks.Status().Executed) else "")
                         + " daemonMs="
                         + daemonMs.ToString("F3", CultureInfo.InvariantCulture)
                         + " elapsedMs="
                         + string (int daemonMs))

                if report.Wedged then
                    hooks.Retire ()

                try
                    sendResponse
                        connection
                        (Executed
                            { ExitCode = report.Outcome.ExitCode
                              StandardOut = report.Outcome.StandardOut
                              StandardError = report.Outcome.StandardError
                              Diagnostics = report.Outcome.Diagnostics
                              TimedOut = report.Outcome.TimedOut })
                finally
                    monitorCancellation.Cancel ()

                    if report.Wedged then
                        hooks.Respawn ()

                logExecuted ()

                if options.Oneshot && not report.Wedged then
                    hooks.Stop ()
            | Ping (preludePath, noPrelude) ->
                hooks.EnsureWarm (Prelude.specOfOptions preludePath noPrelude)
                sendResponse connection Pong
            | GetStatus -> sendResponseWithVersion requestVersion connection (Status(hooks.Status ()))
            | GetMetrics -> sendResponse connection (Metrics(hooks.Metrics ()))
            | Shutdown ->
                sendResponseWithVersion requestVersion connection ShuttingDown
                hooks.Stop ()
    with ex ->
        Log.error options.LogPath ("connection error: " + ex.ToString())
        try
            sendResponseWithVersion requestVersion connection (Failure(ex.ToString()))
        with _ ->
            ()
let run (options: Options) (listener: IListener) (releaseOwnership: unit -> unit) : int =
    let generationId = Guid.NewGuid().ToString("N")
    let session =
        new SessionHost(
            { EndpointName = options.Endpoint.Name
              GenerationId = generationId
              PoolSize = options.PoolSize
              SessionTtlMinutes = options.SessionTtlMinutes
              TrimIdleMinutes = options.TrimIdleMinutes
              Oneshot = options.Oneshot
              LogPath = options.LogPath
              Prelude = options.Prelude }
        )

    let startedAt = DateTime.UtcNow
    let mutable lastActivityTicks = DateTime.UtcNow.Ticks
    let mutable activeRequests = 0
    let mutable retired = 0
    let mutable stopped = 0
    let idleTtlMs = int64 (TimeSpan.FromMinutes(options.IdleTtlMinutes).TotalMilliseconds)

    let stopToken = new CancellationTokenSource()

    let touchActivity () =
        Volatile.Write(&lastActivityTicks, DateTime.UtcNow.Ticks)

    let stop () =
        if Interlocked.Exchange(&stopped, 1) = 0 then
            stopToken.Cancel()

            try
                listener.Stop ()
            with _ ->
                ()

            releaseOwnership ()

    // Non-collateral wedged drain: stop accepting new connections and release endpoint ownership
    // immediately so the replacement generation serves new callers at once, but let all other
    // healthy in-flight requests finish before terminating the retiring process (bounded by a
    // generous 60s drain ceiling so a wedged evaluation still cannot leak forever).
    let scheduleSelfTermination () =
        if Interlocked.Exchange(&retired, 1) = 0 then
            Task.Factory.StartNew(
                (fun () ->
                    let drainDeadline = Environment.TickCount64 + 60000L
                    while Volatile.Read(&activeRequests) > 0
                          && Environment.TickCount64 < drainDeadline do
                        Thread.Sleep 50
                    Thread.Sleep 250
                    try
                        Process.GetCurrentProcess().Kill()
                    with _ ->
                        ()),
                TaskCreationOptions.LongRunning
            )
            |> ignore

    let retire () =
        stop ()
        scheduleSelfTermination ()

    let respawn () =
        if not options.Oneshot then
            match Environment.ProcessPath with
            | null -> Log.error options.LogPath "cannot spawn a replacement: Environment.ProcessPath is null"
            | daemonPath ->
                let arguments =
                    [ "--endpoint"; options.Endpoint.Name
                      "--idle-ttl"; options.IdleTtlMinutes.ToString(CultureInfo.InvariantCulture)
                      "--session-ttl"; options.SessionTtlMinutes.ToString(CultureInfo.InvariantCulture)
                      "--pool-size"; string options.PoolSize ]
                    @ (match session.DesiredSpec with
                       | Prelude.Spec.Bare -> [ "--no-prelude" ]
                       | Prelude.Spec.Builtin -> [ "--builtin-prelude" ]
                       | Prelude.Spec.File path -> [ "--prelude"; path ])
                    @ (match options.LogPath with
                       | Some path -> [ "--log"; path ]
                       | None -> [])

                try
                    spawnDetachedWithEnvironment daemonPath arguments (session.RespawnEnvironment())
                    Log.info options.LogPath "spawned a replacement fsiond generation"
                with ex ->
                    Log.error options.LogPath ("replacement spawn failed: " + ex.Message)

    let hooks =
        { EnsureWarm =
            fun spec ->
                session.EnsureWarm spec
                touchActivity ()
          Execute =
            fun request disconnectToken ->
                let report = session.Execute(request, disconnectToken)
                touchActivity ()
                report
          Status =
            fun () ->
                let lastTicks = Volatile.Read(&lastActivityTicks)
                { Pid = Environment.ProcessId
                  Endpoint = options.Endpoint.Name
                  UptimeMs = int64 (DateTime.UtcNow - startedAt).TotalMilliseconds
                  IdleMs = int64 (DateTime.UtcNow - DateTime(lastTicks, DateTimeKind.Utc)).TotalMilliseconds
                  Executed = session.ExecutedCount
                  Warm = session.IsWarm
                  ProtocolVersion = ProtocolVersion
                  TargetFramework = targetFrameworkMoniker
                  BuildFingerprint = buildFingerprint
                  PoolSize = options.PoolSize
                  IdleTtlMinutes = options.IdleTtlMinutes
                  SessionTtlMinutes = options.SessionTtlMinutes
                  GenerationId = generationId }
          Metrics = fun () -> session.Metrics ()
          Retire = retire
          Respawn = respawn
          Stop = stop }

    let acceptLoop =
        Task.Factory.StartNew(
            (fun () ->
                while Volatile.Read(&stopped) = 0 do
                    try
                        let stream = listener.Accept().GetAwaiter().GetResult()
                        Interlocked.Increment(&activeRequests) |> ignore
                        Task.Run(fun () ->
                            try
                                handleConnection options hooks stream
                            finally
                                Interlocked.Decrement(&activeRequests) |> ignore)
                        |> ignore
                    with
                    | :? OperationCanceledException -> ()
                    | :? ObjectDisposedException -> ()
                    | ex ->
                        Log.error options.LogPath ("accept loop error: " + ex.Message)
                        Thread.Sleep 50),
            TaskCreationOptions.LongRunning
        )

    let watchdog =
        Task.Run(fun () ->
            while Volatile.Read(&stopped) = 0 do
                stopToken.Token.WaitHandle.WaitOne 250 |> ignore
                let lastTicks = Volatile.Read(&lastActivityTicks)
                let idleMs = (DateTime.UtcNow - DateTime(lastTicks, DateTimeKind.Utc)).TotalMilliseconds
                let uptimeMs = (DateTime.UtcNow - startedAt).TotalMilliseconds
                if options.Oneshot && Volatile.Read(&activeRequests) = 0 && uptimeMs > 60000.0 then
                    stop ()
                elif
                    not options.Oneshot
                    && Volatile.Read(&activeRequests) = 0
                    && idleMs >= float idleTtlMs
                then
                    Log.info options.LogPath ("idle ttl reached after " + string (int idleMs) + " ms")
                    stop ())

    Log.info options.LogPath ("listening on " + endpointDisplay options.Endpoint)
    acceptLoop.Wait ()
    let drainSeconds = if Volatile.Read(&retired) = 1 then 60.0 else 3.0
    let drainDeadline = DateTime.UtcNow.AddSeconds drainSeconds
    while Volatile.Read(&activeRequests) > 0 && DateTime.UtcNow < drainDeadline do
        Thread.Sleep 50
    watchdog.Wait 1000 |> ignore
    0
