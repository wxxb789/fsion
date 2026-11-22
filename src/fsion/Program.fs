module Fsion.Program

open System
open System.ComponentModel
open System.Diagnostics
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open Fsion.Ipc
open Fsion.Protocol

let private version = "0.1.0"

[<Struct; StructLayout(LayoutKind.Sequential)>]
type StartupInfo =
    val mutable Cb: uint32
    val mutable Reserved: nativeint
    val mutable Desktop: nativeint
    val mutable Title: nativeint
    val mutable X: uint32
    val mutable Y: uint32
    val mutable XSize: uint32
    val mutable YSize: uint32
    val mutable XCountChars: uint32
    val mutable YCountChars: uint32
    val mutable FillAttribute: uint32
    val mutable Flags: uint32
    val mutable ShowWindow: uint16
    val mutable Reserved2: uint16
    val mutable Reserved2Ptr: nativeint
    val mutable StdInput: nativeint
    val mutable StdOutput: nativeint
    val mutable StdError: nativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type ProcessInformation =
    val mutable Process: nativeint
    val mutable Thread: nativeint
    val mutable ProcessId: uint32
    val mutable ThreadId: uint32

[<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)>]
extern bool CreateProcessW(
    string applicationName,
    string commandLine,
    nativeint processAttributes,
    nativeint threadAttributes,
    bool inheritHandles,
    uint32 creationFlags,
    nativeint environment,
    string currentDirectory,
    StartupInfo& startupInfo,
    ProcessInformation& processInformation
)

[<DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)>]
extern bool CloseHandle(nativeint handle)

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
      IdleTtlMinutes: float option }

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
    + "  --pipe <name>         daemon endpoint name (env: FSION_PIPE)\n"
    + "  --idle-ttl <minutes>  auto-started daemon idle TTL (env: FSION_IDLE_TTL_MINUTES, default 30)\n"
    + "  -h, --help            show this help\n"
    + "  --version             show version\n"

let private parse (argv: string[]) : Result<Options * Input, string> =
    let mutable options =
        { NoDaemon = false
          PipeName = None
          IdleTtlMinutes = None }
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
        | "--pipe" when index + 1 < argv.Length ->
            options <- { options with PipeName = Some argv.[index + 1] }
            index <- index + 2
        | "--idle-ttl" when index + 1 < argv.Length ->
            match Double.TryParse(argv.[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value when value > 0.0 ->
                options <- { options with IdleTtlMinutes = Some value }
                index <- index + 2
            | _ -> outcome <- Some(Error("invalid --idle-ttl value: " + argv.[index + 1]))
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
    match outcome with
    | Some result -> result
    | None ->
        if Console.IsInputRedirected then
            Ok(options, RunStdin [||])
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

let private quoteWindowsArgument (value: string) =
    if value.Length > 0 && not (value.Contains ' ' || value.Contains '\t' || value.Contains '"') then
        value
    else
        let builder = StringBuilder()
        builder.Append '"' |> ignore
        let mutable backslashes = 0
        for character in value do
            match character with
            | '\\' -> backslashes <- backslashes + 1
            | '"' ->
                builder.Append('\\', backslashes * 2 + 1).Append '"' |> ignore
                backslashes <- 0
            | _ ->
                builder.Append('\\', backslashes).Append character |> ignore
                backslashes <- 0
        builder.Append('\\', backslashes * 2).Append '"' |> ignore
        builder.ToString()

let private daemonArguments (idleTtlMinutes: float option) (endpoint: Endpoint) =
    let modeArguments =
        match idleTtlMinutes with
        | Some minutes -> [ "--idle-ttl"; minutes.ToString(CultureInfo.InvariantCulture) ]
        | None -> [ "--oneshot" ]
    let logArguments =
        match Environment.GetEnvironmentVariable "FSIOND_LOG" with
        | null | "" -> []
        | logPath -> [ "--log"; logPath ]
    [ "--endpoint"; endpoint.Name ] @ modeArguments @ logArguments

// Process.Start sets bInheritHandles=TRUE on Windows, so a long-lived fsiond would
// inherit the caller's stdout/stderr pipe write ends and keep a redirected
// ReadToEnd blocked after fsion itself exits. Spawning with bInheritHandles=false
// leaves the daemon without any inherited handle.
let private spawnDaemonWindows (daemonPath: string) (arguments: string list) =
    let commandLine =
        daemonPath :: arguments |> List.map quoteWindowsArgument |> String.concat " "
    let mutable startupInfo = Unchecked.defaultof<StartupInfo>
    startupInfo.Cb <- uint32 (Marshal.SizeOf<StartupInfo>())
    let mutable processInformation = Unchecked.defaultof<ProcessInformation>
    let creationFlags = 0x08000000u ||| 0x00000008u
    let created =
        CreateProcessW(
            daemonPath,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            creationFlags,
            IntPtr.Zero,
            Path.GetDirectoryName daemonPath,
            &startupInfo,
            &processInformation
        )
    if not created then
        raise (Win32Exception(Marshal.GetLastWin32Error(), "failed to start fsiond"))
    if processInformation.Process <> IntPtr.Zero then
        CloseHandle processInformation.Process |> ignore
    if processInformation.Thread <> IntPtr.Zero then
        CloseHandle processInformation.Thread |> ignore

let private spawnDaemonManaged (daemonPath: string) (arguments: string list) =
    let startInfo = ProcessStartInfo(daemonPath)
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.RedirectStandardInput <- true
    startInfo.WorkingDirectory <- (Path.GetDirectoryName daemonPath)
    for argument in arguments do
        startInfo.ArgumentList.Add argument
    let daemonProcess = Process.Start startInfo
    if isNull daemonProcess then
        raise (InvalidOperationException "failed to start fsiond")
    Task.Run(fun () ->
        try
            daemonProcess.StandardOutput.ReadToEnd() |> ignore
        with _ ->
            ())
    |> ignore
    Task.Run(fun () ->
        try
            daemonProcess.StandardError.ReadToEnd() |> ignore
        with _ ->
            ())
    |> ignore

let private spawnDaemon (idleTtlMinutes: float option) (endpoint: Endpoint) =
    let daemonPath = findDaemonBinary ()
    let arguments = daemonArguments idleTtlMinutes endpoint
    if isWindows then
        spawnDaemonWindows daemonPath arguments
    else
        spawnDaemonManaged daemonPath arguments

let private ensureDaemon (options: Options) (endpoint: Endpoint) : Stream =
    match tryConnect endpoint (TimeSpan.FromMilliseconds 250.0) with
    | Some stream -> stream
    | None ->
        spawnDaemon (Some(resolveIdleTtl options)) endpoint
        connect endpoint (TimeSpan.FromSeconds 30.0)

type private Delivery =
    | NotDelivered
    | NoResponse
    | Delivered of Response

let private sendAndReceive (stream: Stream) (request: Request) : Delivery =
    let delivered =
        try
            sendRequest stream request
            true
        with _ ->
            false
    if not delivered then
        NotDelivered
    else
        match tryReceiveFrame stream with
        | Some payload -> Delivered(parseResponse payload)
        | None -> NoResponse

let private responseOf (delivery: Delivery) : Response =
    match delivery with
    | Delivered response -> response
    | NoResponse -> Failure "fsiond closed the connection without a response"
    | NotDelivered -> Failure "fsiond did not accept the request"

let private executeOverDaemon (options: Options) (endpoint: Endpoint) (request: ExecuteRequest) : Response =
    let attempt () =
        let stream = ensureDaemon options endpoint
        try
            sendAndReceive stream (Execute request)
        finally
            stream.Dispose()
    let first = attempt ()
    match first with
    | Delivered response -> response
    | NoResponse -> responseOf first
    | NotDelivered -> attempt () |> responseOf

let private executeOneShot (request: ExecuteRequest) : Response =
    let endpoint =
        { Name = "fsion-oneshot-" + string Environment.ProcessId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) }
    spawnDaemon None endpoint
    let stream = connect endpoint (TimeSpan.FromSeconds 60.0)
    try
        responseOf (sendAndReceive stream (Execute request))
    finally
        stream.Dispose()

let private emit (response: Response) : int =
    match response with
    | Executed (exitCode, standardOut, standardError) ->
        if standardOut.Length > 0 then
            Console.Out.Write standardOut
        if standardError.Length > 0 then
            Console.Error.Write standardError
        Console.Out.Flush()
        Console.Error.Flush()
        exitCode
    | Failure message ->
        Console.Error.Write("fsion: " + message + Environment.NewLine)
        Console.Error.Flush()
        1
    | _ ->
        Console.Error.Write("fsion: unexpected daemon response" + Environment.NewLine)
        Console.Error.Flush()
        1

let private fetchStatus (endpoint: Endpoint) : StatusInfo option =
    match tryConnect endpoint (TimeSpan.FromMilliseconds 250.0) with
    | None -> None
    | Some stream ->
        use connection = stream
        match sendAndReceive connection GetStatus with
        | Delivered (Status status) -> Some status
        | _ -> None

let private stopDaemon (endpoint: Endpoint) : bool =
    match tryConnect endpoint (TimeSpan.FromMilliseconds 250.0) with
    | None -> true
    | Some stream ->
        let replied =
            try
                match sendAndReceive stream Shutdown with
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
        match sendAndReceive stream Ping with
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

let private executeInput (options: Options) (endpoint: Endpoint) (request: ExecuteRequest) : int =
    if options.NoDaemon then
        emit (executeOneShot request)
    else
        emit (executeOverDaemon options endpoint request)

[<EntryPoint>]
let main argv =
    try
        Console.OutputEncoding <- UTF8Encoding(false)
    with _ ->
        ()
    match parse argv with
    | Error message ->
        Console.Error.Write("fsion: " + message + Environment.NewLine + usage)
        Console.Error.Flush()
        1
    | Ok (options, input) ->
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
                    { Mode = "file"
                      Source = path
                      WorkingDirectory = Directory.GetCurrentDirectory()
                      Args = Array.append [| path |] args }
            | RunEval (code, args) ->
                executeInput
                    options
                    endpoint
                    { Mode = "eval"
                      Source = code
                      WorkingDirectory = Directory.GetCurrentDirectory()
                      Args = Array.append [| "eval.fsx" |] args }
            | RunStdin args ->
                executeInput
                    options
                    endpoint
                    { Mode = "stdin"
                      Source = readAllStdin ()
                      WorkingDirectory = Directory.GetCurrentDirectory()
                      Args = Array.append [| "stdin.fsx" |] args }
            | Warmup -> runWarmup options endpoint
            | DaemonStart
            | DaemonStop
            | DaemonRestart
            | DaemonStatus -> runDaemonCommand options endpoint input
        with ex ->
            Console.Error.Write("fsion: " + ex.Message + Environment.NewLine)
            Console.Error.Flush()
            1
