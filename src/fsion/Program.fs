module Fsion.Program

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Fsion.Ipc
open Fsion.Protocol

let private version = "0.1.0"

type private Input =
    | RunScript of path: string * args: string[]
    | RunEval of code: string * args: string[]
    | RunStdin of args: string[]
    | Warmup
    | DaemonStart
    | DaemonStop
    | DaemonRestart
    | DaemonStatus
    | ShowHelp
    | ShowVersion

type private Options =
    { NoDaemon: bool
      PipeName: string option
      IdleTtlMinutes: float option
      SessionTtlMinutes: float option
      SessionId: string option
      Reset: bool
      TimeoutMs: int option
      PreludePath: string option
      NoPrelude: bool
      Json: bool }

let private usage =
    "fsion " + version + " - fast F# script runner\n"
    + "\n"
    + "Usage:\n"
    + "  fsion [options] <script.fsx> [args...]\n"
    + "  fsion [options] -e <code> [args...]\n"
    + "  fsion [options] - [args...]\n"
    + "  fsion [options] warmup\n"
    + "  fsion [options] daemon <start|stop|restart|status>\n"
    + "\n"
    + "Options:\n"
    + "  --no-daemon           run a single-shot daemon for this invocation only\n"
    + "  --json                emit a structured JSON result instead of raw streams\n"
    + "  --pipe <name>         daemon endpoint name (env: FSION_PIPE)\n"
    + "  --idle-ttl <minutes>  auto-started daemon idle TTL (env: FSION_IDLE_TTL_MINUTES, default 30)\n"
    + "  --session <id>        reuse a named stateful session inside fsiond\n"
    + "  --reset               discard the named session before this run (requires --session)\n"
    + "  --session-ttl <min>   auto-started daemon named-session idle TTL (env: FSION_SESSION_TTL_MINUTES, default 15)\n"
    + "  --timeout <ms>        cancel after this deadline; scripts observe fsionFsiState.CancellationToken; exit 124\n"
    + "  --prelude <path>      evaluate this prelude.fsx in warm sessions (env: FSION_PRELUDE)\n"
    + "  --no-prelude          do not load the Fsion module or the user prelude\n"
    + "  -h, --help            show this help\n"
    + "  --version             show version\n"

let private parse (argv: string[]) : Result<Options * Input, string> =
    let mutable options =
        { NoDaemon = false
          PipeName = None
          IdleTtlMinutes = None
          SessionTtlMinutes = None
          SessionId = None
          Reset = false
          TimeoutMs = None
          PreludePath = None
          NoPrelude = false
          Json = false }
    let mutable outcome: Result<Options * Input, string> option = None
    let mutable index = 0
    while outcome.IsNone && index < argv.Length do
        let argument = argv.[index]
        match argument with
        | "-h"
        | "--help" -> outcome <- Some(Ok(options, ShowHelp))
        | "--version" -> outcome <- Some(Ok(options, ShowVersion))
        | "--no-daemon" ->
            options <- { options with NoDaemon = true }
            index <- index + 1
        | "--json" ->
            options <- { options with Json = true }
            index <- index + 1
        | "--pipe" when index + 1 < argv.Length ->
            let name = argv.[index + 1]
            if name.Trim().Length = 0 then
                outcome <- Some(Error "--pipe requires a non-empty endpoint name")
            else
                options <- { options with PipeName = Some name }
                index <- index + 2
        | "--idle-ttl" when index + 1 < argv.Length ->
            match Double.TryParse(argv.[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value when value > 0.0 ->
                options <- { options with IdleTtlMinutes = Some value }
                index <- index + 2
            | _ -> outcome <- Some(Error("invalid --idle-ttl value: " + argv.[index + 1]))
        | "--session" when index + 1 < argv.Length ->
            let sessionId = argv.[index + 1]

            if sessionId.Trim().Length = 0 then
                outcome <- Some(Error "--session requires a non-empty session id")
            else
                options <- { options with SessionId = Some sessionId }
                index <- index + 2
        | "--reset" ->
            options <- { options with Reset = true }
            index <- index + 1
        | "--session-ttl" when index + 1 < argv.Length ->
            match Double.TryParse(argv.[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value when value > 0.0 ->
                options <- { options with SessionTtlMinutes = Some value }
                index <- index + 2
            | _ -> outcome <- Some(Error("invalid --session-ttl value: " + argv.[index + 1]))
        | "--timeout" when index + 1 < argv.Length ->
            match Int32.TryParse(argv.[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, value when value > 0 ->
                options <- { options with TimeoutMs = Some value }
                index <- index + 2
            | _ -> outcome <- Some(Error("invalid --timeout value: " + argv.[index + 1]))
        | "--prelude" when index + 1 < argv.Length ->
            let prelude = argv.[index + 1]

            if prelude.Trim().Length = 0 then
                outcome <- Some(Error "--prelude requires a non-empty path")
            else
                options <- { options with PreludePath = Some prelude }
                index <- index + 2
        | "--no-prelude" ->
            options <- { options with NoPrelude = true }
            index <- index + 1
        | "warmup" -> outcome <- Some(Ok(options, Warmup))
        | "daemon" ->
            if index + 1 >= argv.Length then
                outcome <- Some(Error "daemon requires one of: start, stop, restart, status")
            else
                match argv.[index + 1] with
                | "start" -> outcome <- Some(Ok(options, DaemonStart))
                | "stop" -> outcome <- Some(Ok(options, DaemonStop))
                | "restart" -> outcome <- Some(Ok(options, DaemonRestart))
                | "status" -> outcome <- Some(Ok(options, DaemonStatus))
                | other -> outcome <- Some(Error("unknown daemon subcommand: " + other))
        | "-e" ->
            if index + 1 >= argv.Length then
                outcome <- Some(Error "-e requires a code argument")
            else
                outcome <- Some(Ok(options, RunEval(argv.[index + 1], argv.[index + 2 ..])))
        | "-" -> outcome <- Some(Ok(options, RunStdin(argv.[index + 1 ..])))
        | _ when argument.StartsWith("-", StringComparison.Ordinal) ->
            outcome <- Some(Error("unknown option: " + argument))
        | script -> outcome <- Some(Ok(options, RunScript(script, argv.[index + 1 ..])))
    let validate (parsed: Result<Options * Input, string>) =
        match parsed with
        | Ok (parsedOptions, _) when parsedOptions.Reset && parsedOptions.SessionId.IsNone ->
            Error "--reset requires --session <id>"
        | Ok (parsedOptions, _) when parsedOptions.SessionId.IsSome && parsedOptions.NoDaemon ->
            Error "--no-daemon cannot be combined with --session <id>"
        | Ok (parsedOptions, _) when parsedOptions.NoPrelude && parsedOptions.PreludePath.IsSome ->
            Error "--no-prelude cannot be combined with --prelude <path>"
        | _ -> parsed

    match outcome with
    | Some result -> validate result
    | None ->
        if Console.IsInputRedirected then
            validate (Ok(options, RunStdin [||]))
        else
            Error "no script file, -e code, or redirected stdin input"

let private resolveEndpoint (options: Options) =
    let name =
        match options.PipeName with
        | Some value -> value
        | None ->
            match Environment.GetEnvironmentVariable PipeEnvironmentVariable with
            | null | "" -> defaultEndpointName ()
            | value -> value
    validateEndpointName name
    { Name = name }

let private resolveIdleTtl (options: Options) =
    match options.IdleTtlMinutes with
    | Some value -> value
    | None ->
        match Environment.GetEnvironmentVariable IdleTtlEnvironmentVariable with
        | null | "" -> DefaultIdleTtlMinutes
        | value ->
            match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, parsed -> parsed
            | _ -> DefaultIdleTtlMinutes

let private resolveSessionTtl (options: Options) =
    match options.SessionTtlMinutes with
    | Some value -> value
    | None ->
        match Environment.GetEnvironmentVariable SessionTtlEnvironmentVariable with
        | null | "" -> DefaultSessionTtlMinutes
        | value ->
            match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, parsed -> parsed
            | _ -> DefaultSessionTtlMinutes

let private resolvePrelude (options: Options) : Result<string option, string> =
    if options.NoPrelude then
        Ok None
    else
        match options.PreludePath |> Option.orElseWith PreludeConfig.configuredPreludePath with
        | Some path ->
            let fullPath = Path.GetFullPath path

            if File.Exists fullPath then
                Ok(Some fullPath)
            else
                Error("prelude file not found: " + path)
        | None -> Ok(PreludeConfig.userPreludePath ())

let private findDaemonBinary () =
    match Environment.GetEnvironmentVariable "FSIOND_PATH" with
    | null | "" ->
        let fileName = if isWindows then "fsiond.exe" else "fsiond"
        let candidate = Path.Combine(AppContext.BaseDirectory, fileName)
        if File.Exists candidate then
            candidate
        else
            raise (FileNotFoundException("fsiond was not found next to fsion; set FSIOND_PATH", candidate))
    | path -> path

let private daemonArguments
    (idleTtlMinutes: float option)
    (sessionTtlMinutes: float option)
    (options: Options)
    (endpoint: Endpoint)
    =
    let modeArguments =
        match idleTtlMinutes with
        | Some minutes -> [ "--idle-ttl"; minutes.ToString(CultureInfo.InvariantCulture) ]
        | None -> [ "--oneshot" ]
    let sessionArguments =
        match sessionTtlMinutes with
        | Some minutes -> [ "--session-ttl"; minutes.ToString(CultureInfo.InvariantCulture) ]
        | None -> []
    let preludeArguments =
        if options.NoPrelude then
            [ "--no-prelude" ]
        else
            match options.PreludePath with
            | Some path -> [ "--prelude"; path ]
            | None -> []
    let logArguments =
        match Environment.GetEnvironmentVariable "FSIOND_LOG" with
        | null | "" -> []
        | logPath -> [ "--log"; logPath ]
    [ "--endpoint"; endpoint.Name ] @ modeArguments @ sessionArguments @ preludeArguments @ logArguments

let private spawnDaemon
    (idleTtlMinutes: float option)
    (sessionTtlMinutes: float option)
    (options: Options)
    (endpoint: Endpoint)
    =
    let daemonPath = findDaemonBinary ()
    spawnDetached daemonPath (daemonArguments idleTtlMinutes sessionTtlMinutes options endpoint)

let private ensureDaemon (options: Options) (endpoint: Endpoint) : Stream =
    match tryConnect endpoint (TimeSpan.FromMilliseconds 250.0) with
    | Some stream -> stream
    | None ->
        spawnDaemon (Some(resolveIdleTtl options)) (Some(resolveSessionTtl options)) options endpoint
        connect endpoint (TimeSpan.FromSeconds 30.0)

type private Delivery =
    | NotDelivered
    | NoResponse
    | DeadlineExpired of timeoutMs: int
    | Delivered of Response

let private sendAndReceive (stream: Stream) (timeoutMs: int option) (request: Request) : Delivery =
    let delivered =
        try
            sendRequest stream request
            true
        with _ ->
            false
    if not delivered then
        NotDelivered
    else
        match timeoutMs with
        | Some timeoutMs when timeoutMs > 0 ->
            let receive = Task.Run(fun () -> tryReceiveFrame stream)
            // Last-resort net only: fsiond enforces --timeout after the session is acquired, so
            // cold daemon/session creation must not be charged against this client deadline.
            if receive.Wait(timeoutMs + 30000) then
                match receive.Result with
                | Some payload -> Delivered(parseResponse payload)
                | None -> NoResponse
            else
                DeadlineExpired timeoutMs
        | _ ->
            match tryReceiveFrame stream with
            | Some payload -> Delivered(parseResponse payload)
            | None -> NoResponse

let private timeoutResult (timeoutMs: int) : ExecutionResult =
    { ExitCode = TimeoutExitCode
      StandardOut = ""
      StandardError = timeoutMessage timeoutMs
      Diagnostics = [||]
      TimedOut = true }

let private responseOf (delivery: Delivery) : Response =
    match delivery with
    | Delivered response -> response
    | NoResponse -> Failure "fsiond closed the connection without a response"
    | DeadlineExpired timeoutMs -> Executed(timeoutResult timeoutMs)
    | NotDelivered -> Failure "fsiond did not accept the request"

let private executeOverDaemon
    (options: Options)
    (endpoint: Endpoint)
    (request: ExecuteRequest)
    : Response =
    let attempt () =
        let stream = ensureDaemon options endpoint
        try
            sendAndReceive stream options.TimeoutMs (Execute request)
        finally
            stream.Dispose()
    let first = attempt ()
    match first with
    | Delivered response -> response
    | NotDelivered -> attempt () |> responseOf
    | NoResponse
    | DeadlineExpired _ -> responseOf first

let private executeOneShot
    (options: Options)
    (endpoint: Endpoint)
    (request: ExecuteRequest)
    : Response =
    let oneshotEndpoint =
        match options.PipeName with
        | Some _ -> endpoint
        | None ->
            match Environment.GetEnvironmentVariable OneShotPipeEnvironmentVariable with
            | null | "" ->
                { Name =
                    "fsion-oneshot-"
                    + string Environment.ProcessId
                    + "-"
                    + Guid.NewGuid().ToString("N").Substring(0, 8) }
            | name -> { Name = name }
    validateEndpointName oneshotEndpoint.Name
    spawnDaemon None None options oneshotEndpoint
    let stream = connect oneshotEndpoint (TimeSpan.FromSeconds 60.0)

    try
        sendAndReceive stream options.TimeoutMs (Execute request) |> responseOf
    finally
        stream.Dispose()

let private writeJsonResult (elapsedMs: float) (result: ExecutionResult) =
    use stream = Console.OpenStandardOutput()
    use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
    writer.WriteStartObject()
    writer.WriteNumber("exitCode", result.ExitCode)
    writer.WriteString("stdout", result.StandardOut)
    writer.WriteString("stderr", result.StandardError)
    writer.WriteBoolean("timedOut", result.TimedOut)
    writer.WriteNumber("elapsedMs", elapsedMs)
    writer.WriteStartArray("diagnostics")

    for diagnostic in result.Diagnostics do
        writer.WriteStartObject()
        writer.WriteString("code", diagnostic.Code)
        writer.WriteNumber("number", diagnostic.Number)
        writer.WriteString("severity", diagnostic.Severity)
        writer.WriteString("message", diagnostic.Message)
        writer.WriteString("file", diagnostic.File)
        writer.WriteNumber("startLine", diagnostic.StartLine)
        writer.WriteNumber("startColumn", diagnostic.StartColumn)
        writer.WriteNumber("endLine", diagnostic.EndLine)
        writer.WriteNumber("endColumn", diagnostic.EndColumn)
        writer.WriteEndObject()

    writer.WriteEndArray()
    writer.WriteEndObject()
    writer.Flush()

let private emitFailure (options: Options) (elapsedMs: float) (message: string) : int =
    if options.Json then
        writeJsonResult
            elapsedMs
            { ExitCode = 1
              StandardOut = ""
              StandardError = "fsion: " + message + Environment.NewLine
              Diagnostics = [||]
              TimedOut = false }
    else
        Console.Error.Write("fsion: " + message + Environment.NewLine)
        Console.Error.Flush()

    1

let private emit (options: Options) (elapsedMs: float) (response: Response) : int =
    match response with
    | Executed result ->
        if options.Json then
            writeJsonResult elapsedMs result
        else
            if result.StandardOut.Length > 0 then
                Console.Out.Write result.StandardOut

            if result.StandardError.Length > 0 then
                Console.Error.Write result.StandardError

            Console.Out.Flush()
            Console.Error.Flush()

        result.ExitCode
    | Failure message -> emitFailure options elapsedMs message
    | _ -> emitFailure options elapsedMs "unexpected daemon response"

let private fetchStatus (endpoint: Endpoint) : StatusInfo option =
    match tryConnect endpoint (TimeSpan.FromMilliseconds 250.0) with
    | None -> None
    | Some stream ->
        use connection = stream
        match sendAndReceive connection None GetStatus with
        | Delivered (Status status) -> Some status
        | _ -> None

let private stopDaemon (endpoint: Endpoint) : bool =
    match tryConnect endpoint (TimeSpan.FromMilliseconds 250.0) with
    | None -> true
    | Some stream ->
        let replied =
            try
                match sendAndReceive stream None Shutdown with
                | Delivered ShuttingDown -> true
                | _ -> false
            finally
                stream.Dispose()
        if not replied then
            false
        else
            let deadline = DateTime.UtcNow.AddSeconds 8.0
            let mutable reachable = true
            while reachable && DateTime.UtcNow < deadline do
                match tryConnect endpoint (TimeSpan.FromMilliseconds 150.0) with
                | None -> reachable <- false
                | Some probe ->
                    probe.Dispose()
                    Thread.Sleep 100
            not reachable

let private readAllStdin () =
    use standardInput = Console.OpenStandardInput()
    use buffer = new MemoryStream()
    standardInput.CopyTo buffer
    let text = Encoding.UTF8.GetString(buffer.ToArray())
    if text.Length > 0 && text.[0] = '\uFEFF' then text.Substring 1 else text

let private statusLine (status: StatusInfo) =
    $"fsiond running (pid={status.Pid}, endpoint={status.Endpoint}, uptimeMs={status.UptimeMs}, idleMs={status.IdleMs}, executed={status.Executed}, warm={status.Warm})"

let private runWarmup (options: Options) (endpoint: Endpoint) : int =
    let stream = ensureDaemon options endpoint
    try
        match sendAndReceive stream None (Ping(options.PreludePath, options.NoPrelude)) with
        | Delivered Pong ->
            match fetchStatus endpoint with
            | Some status ->
                Console.Out.Write($"fsiond ready (pid={status.Pid}, endpoint={status.Endpoint}, warm={status.Warm})" + Environment.NewLine)
                0
            | None ->
                Console.Out.Write("fsiond ready" + Environment.NewLine)
                0
        | _ ->
            Console.Error.Write("fsion: fsiond did not answer the warmup ping" + Environment.NewLine)
            1
    finally
        stream.Dispose()

let rec private runDaemonCommand (options: Options) (endpoint: Endpoint) (input: Input) : int =
    match input with
    | DaemonStatus ->
        match fetchStatus endpoint with
        | Some status ->
            Console.Out.Write(statusLine status + Environment.NewLine)
            0
        | None ->
            Console.Out.Write("fsiond not running (endpoint " + endpoint.Name + ")" + Environment.NewLine)
            0
    | DaemonStart ->
        match fetchStatus endpoint with
        | Some status ->
            Console.Out.Write($"fsiond already running (pid={status.Pid}, endpoint={status.Endpoint})" + Environment.NewLine)
            0
        | None ->
            let stream = ensureDaemon options endpoint
            stream.Dispose()
            match fetchStatus endpoint with
            | Some status ->
                Console.Out.Write($"fsiond started (pid={status.Pid}, endpoint={status.Endpoint})" + Environment.NewLine)
                0
            | None ->
                Console.Out.Write("fsiond started (endpoint " + endpoint.Name + ")" + Environment.NewLine)
                0
    | DaemonStop ->
        if stopDaemon endpoint then
            Console.Out.Write($"fsiond stopped (endpoint {endpoint.Name})" + Environment.NewLine)
            0
        else
            Console.Error.Write($"fsion: fsiond did not stop within the timeout (endpoint {endpoint.Name})" + Environment.NewLine)
            1
    | DaemonRestart ->
        if not (stopDaemon endpoint) then
            Console.Error.Write($"fsion: fsiond did not stop within the timeout (endpoint {endpoint.Name})" + Environment.NewLine)
            1
        else
            runDaemonCommand options endpoint DaemonStart
    | _ -> 1

let private describe
    (options: Options)
    (mode: string)
    (source: string)
    (args: string[])
    : ExecuteRequest =
    { Mode = mode
      Source = source
      WorkingDirectory = Directory.GetCurrentDirectory()
      Args = args
      Environment = environmentSnapshot ()
      IncludeDiagnostics = options.Json
      SessionId = options.SessionId
      Reset = options.Reset
      TimeoutMs = defaultArg options.TimeoutMs 0
      PreludePath = options.PreludePath
      NoPrelude = options.NoPrelude }

let private executeInput
    (options: Options)
    (endpoint: Endpoint)
    (request: ExecuteRequest)
    (elapsedMs: unit -> float)
    : int =
    let response =
        if options.NoDaemon then
            executeOneShot options endpoint request
        else
            executeOverDaemon options endpoint request

    emit options (elapsedMs ()) response

[<EntryPoint>]
let main argv =
    let started = Stopwatch.StartNew()

    try
        Console.OutputEncoding <- UTF8Encoding(false)
    with _ ->
        ()

    let elapsedMs () = started.Elapsed.TotalMilliseconds

    match parse argv with
    | Error message ->
        Console.Error.Write("fsion: " + message + Environment.NewLine + usage)
        Console.Error.Flush()
        1
    | Ok (options, input) ->
        let resolvedOptions =
            match input with
            | ShowHelp
            | ShowVersion
            | DaemonStop
            | DaemonStatus -> Ok options
            | Warmup
            | RunScript _
            | RunEval _
            | RunStdin _
            | DaemonStart
            | DaemonRestart ->
                resolvePrelude options
                |> Result.map (fun preludePath -> { options with PreludePath = preludePath })

        match resolvedOptions with
        | Error message ->
            emitFailure options (elapsedMs ()) message
        | Ok options ->
            try
                let endpoint = resolveEndpoint options

                match input with
                | ShowHelp ->
                    Console.Out.Write usage
                    Console.Out.Flush()
                    0
                | ShowVersion ->
                    Console.Out.Write("fsion " + version + Environment.NewLine)
                    Console.Out.Flush()
                    0
                | RunScript (path, args) ->
                    executeInput
                        options
                        endpoint
                        (describe options "file" path (Array.append [| path |] args))
                        elapsedMs
                | RunEval (code, args) ->
                    executeInput
                        options
                        endpoint
                        (describe options "eval" code (Array.append [| "eval.fsx" |] args))
                        elapsedMs
                | RunStdin args ->
                    executeInput
                        options
                        endpoint
                        (describe options "stdin" (readAllStdin ()) (Array.append [| "stdin.fsx" |] args))
                        elapsedMs
                | Warmup -> runWarmup options endpoint
                | DaemonStart
                | DaemonStop
                | DaemonRestart
                | DaemonStatus -> runDaemonCommand options endpoint input
            with ex ->
                emitFailure options (elapsedMs ()) ex.Message
