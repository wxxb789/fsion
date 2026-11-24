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
    try
        match tryReceiveFrame connection with
        | None -> ()
        | Some payload ->
            match parseRequest payload with
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

                let elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds

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
                     + string (int elapsedMs))

                if options.Oneshot && not report.Wedged then
                    hooks.Stop ()
            | Ping (preludePath, noPrelude) ->
                hooks.EnsureWarm (Prelude.specOfOptions preludePath noPrelude)
                sendResponse connection Pong
            | GetStatus -> sendResponse connection (Status(hooks.Status ()))
            | GetMetrics -> sendResponse connection (Metrics(hooks.Metrics ()))
            | Shutdown ->
                sendResponse connection ShuttingDown
                hooks.Stop ()
    with ex ->
        Log.error options.LogPath ("connection error: " + ex.ToString())
        try
            sendResponse connection (Failure(ex.ToString()))
        with _ ->
            ()

let run (options: Options) (listener: IListener) (releaseOwnership: unit -> unit) : int =
    let session =
        new SessionHost(
            { PoolSize = options.PoolSize
              SessionTtlMinutes = options.SessionTtlMinutes
              Oneshot = options.Oneshot
              LogPath = options.LogPath
              Prelude = options.Prelude }
        )

    let startedAt = DateTime.UtcNow
    let lastActivity = ref DateTime.UtcNow
    let activeRequests = ref 0
    let stopped = ref false
    let idleTtlMs = int64 (TimeSpan.FromMinutes(options.IdleTtlMinutes).TotalMilliseconds)

    let stopToken = new CancellationTokenSource()

    let stop () =
        if not stopped.Value then
            stopped.Value <- true
            stopToken.Cancel()

            try
                listener.Stop ()
            with _ ->
                ()

            releaseOwnership ()

    // The drained generation keeps no endpoint and no in-flight script can return. A hard
    // self-kill bounds the leak even if managed shutdown handlers would block on the wedged
    // session's process-wide FSI cleanup.
    let scheduleSelfTermination () =
        Task.Factory.StartNew(
            (fun () ->
                Thread.Sleep 1000

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
                       | Prelude.Spec.Builtin -> []
                       | Prelude.Spec.File path -> [ "--prelude"; path ])
                    @ (match options.LogPath with
                       | Some path -> [ "--log"; path ]
                       | None -> [])

                try
                    spawnDetached daemonPath arguments
                    Log.info options.LogPath "spawned a replacement fsiond generation"
                with ex ->
                    Log.error options.LogPath ("replacement spawn failed: " + ex.Message)

    let hooks =
        { EnsureWarm =
            fun spec ->
                session.EnsureWarm spec
                lastActivity.Value <- DateTime.UtcNow
          Execute =
            fun request disconnectToken ->
                let report = session.Execute(request, disconnectToken)
                lastActivity.Value <- DateTime.UtcNow
                report
          Status =
            fun () ->
                { Pid = Environment.ProcessId
                  Endpoint = options.Endpoint.Name
                  UptimeMs = int64 (DateTime.UtcNow - startedAt).TotalMilliseconds
                  IdleMs = int64 (DateTime.UtcNow - lastActivity.Value).TotalMilliseconds
                  Executed = session.ExecutedCount
                  Warm = session.IsWarm }
          Metrics = fun () -> session.Metrics ()
          Retire = retire
          Respawn = respawn
          Stop = stop }

    let acceptLoop =
        Task.Factory.StartNew(
            (fun () ->
                while not stopped.Value do
                    try
                        let stream = listener.Accept().GetAwaiter().GetResult()
                        activeRequests.Value <- activeRequests.Value + 1
                        Task.Run(fun () ->
                            try
                                handleConnection options hooks stream
                            finally
                                activeRequests.Value <- activeRequests.Value - 1)
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
            while not stopped.Value do
                stopToken.Token.WaitHandle.WaitOne 250 |> ignore
                let idleMs = (DateTime.UtcNow - lastActivity.Value).TotalMilliseconds
                let uptimeMs = (DateTime.UtcNow - startedAt).TotalMilliseconds
                if options.Oneshot && activeRequests.Value = 0 && uptimeMs > 60000.0 then
                    stop ()
                elif
                    not options.Oneshot
                    && activeRequests.Value = 0
                    && idleMs >= float idleTtlMs
                then
                    Log.info options.LogPath ("idle ttl reached after " + string (int idleMs) + " ms")
                    stop ())

    Log.info options.LogPath ("listening on " + endpointDisplay options.Endpoint)
    acceptLoop.Wait ()
    let drainDeadline = DateTime.UtcNow.AddSeconds 3.0
    while activeRequests.Value > 0 && DateTime.UtcNow < drainDeadline do
        Thread.Sleep 50
    watchdog.Wait 1000 |> ignore
    0
