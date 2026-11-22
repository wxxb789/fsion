module Fsiond.Server

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Fsion.Ipc
open Fsion.Protocol
open Fsiond.SessionHost

type Options =
    { Endpoint: Endpoint
      IdleTtlMs: int64
      Oneshot: bool
      LogPath: string option }

type [<NoEquality; NoComparison>] private Hooks =
    { EnsureWarm: unit -> unit
      Execute: ExecuteRequest -> RunOutcome
      Status: unit -> StatusInfo
      Stop: unit -> unit
      MarkExecuted: unit -> unit }

let private handleConnection (options: Options) (hooks: Hooks) (stream: Stream) =
    use connection = stream
    try
        match tryReceiveFrame connection with
        | None -> ()
        | Some payload ->
            match parseRequest payload with
            | Execute request ->
                let started = Stopwatch.GetTimestamp()
                let outcome = hooks.Execute request
                sendResponse connection (Executed(outcome.ExitCode, outcome.StandardOut, outcome.StandardError))
                hooks.MarkExecuted ()
                let elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                Log.info options.LogPath ("executed exit=" + string outcome.ExitCode + " elapsedMs=" + string (int elapsedMs))
                if options.Oneshot then
                    hooks.Stop ()
            | Ping ->
                hooks.EnsureWarm ()
                sendResponse connection Pong
            | GetStatus -> sendResponse connection (Status(hooks.Status ()))
            | Shutdown ->
                sendResponse connection ShuttingDown
                hooks.Stop ()
    with ex ->
        Log.error options.LogPath ("connection error: " + ex.ToString())
        try
            sendResponse connection (Failure(ex.ToString()))
        with _ ->
            ()

let run (options: Options) (listener: IListener) : int =
    let session = new SessionHost(options.Endpoint.Name)
    let startedAt = DateTime.UtcNow
    let lastActivity = ref DateTime.UtcNow
    let activeRequests = ref 0
    let stopped = ref false

    let stop () =
        if not stopped.Value then
            stopped.Value <- true
            try
                listener.Stop ()
            with _ ->
                ()

    let hooks =
        { EnsureWarm =
            fun () ->
                session.EnsureWarm ()
                lastActivity.Value <- DateTime.UtcNow
          Execute =
            fun request ->
                let outcome = session.Execute request
                lastActivity.Value <- DateTime.UtcNow
                outcome
          Status =
            fun () ->
                { Pid = Environment.ProcessId
                  Endpoint = options.Endpoint.Name
                  UptimeMs = int64 (DateTime.UtcNow - startedAt).TotalMilliseconds
                  IdleMs = int64 (DateTime.UtcNow - lastActivity.Value).TotalMilliseconds
                  Executed = session.ExecutedCount
                  Warm = session.IsWarm }
          Stop = stop
          MarkExecuted = fun () -> () }

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
                Thread.Sleep 250
                let idleMs = (DateTime.UtcNow - lastActivity.Value).TotalMilliseconds
                let uptimeMs = (DateTime.UtcNow - startedAt).TotalMilliseconds
                if options.Oneshot && activeRequests.Value = 0 && uptimeMs > 60000.0 then
                    stop ()
                elif
                    not options.Oneshot
                    && activeRequests.Value = 0
                    && idleMs >= float options.IdleTtlMs
                then
                    Log.info options.LogPath ("idle ttl reached after " + string (int idleMs) + " ms")
                    stop ())

    Log.info options.LogPath ("listening on " + endpointDisplay options.Endpoint)
    acceptLoop.Wait ()
    let drainDeadline = DateTime.UtcNow.AddSeconds 3.0
    while activeRequests.Value > 0 && DateTime.UtcNow < drainDeadline do
        Thread.Sleep 50
    watchdog.Dispose ()
    0
