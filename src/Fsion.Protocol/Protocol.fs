module Fsion.Protocol

open System
open System.IO
open System.Text

let [<Literal>] ProtocolVersion = 5

let [<Literal>] PipeEnvironmentVariable = "FSION_PIPE"

let [<Literal>] IdleTtlEnvironmentVariable = "FSION_IDLE_TTL_MINUTES"

let [<Literal>] PoolSizeEnvironmentVariable = "FSION_POOL_SIZE"

let [<Literal>] DefaultPoolSize = 4

let [<Literal>] SessionTtlEnvironmentVariable = "FSION_SESSION_TTL_MINUTES"

let [<Literal>] TrimIdleEnvironmentVariable = "FSION_TRIM_IDLE_MINUTES"

let [<Literal>] DefaultTrimIdleMinutes = 5.0

let [<Literal>] OneShotPipeEnvironmentVariable = "FSION_ONESHOT_PIPE"

let [<Literal>] ParentEndpointEnvironmentVariable = "FSION_ACTIVE_ENDPOINT"

let [<Literal>] ParentRunIdEnvironmentVariable = "FSION_ACTIVE_RUN_ID"

let [<Literal>] DefaultIdleTtlMinutes = 30.0

let [<Literal>] DefaultSessionTtlMinutes = 15.0

let [<Literal>] TimeoutExitCode = 124

let targetFrameworkMoniker =
    #if NET11_0_OR_GREATER
    "net11.0"
    #elif NET10_0_OR_GREATER
    "net10.0"
    #else
    "net"
    #endif

let buildFingerprint =
    let version =
        match typeof<obj>.Assembly.GetName().Version with
        | null -> "0.0.0.0"
        | v -> $"{v.Major}.{v.Minor}"
    $"p{ProtocolVersion}-{targetFrameworkMoniker}-{version}"
let timeoutMessage (timeoutMs: int) =
    "fsion: script timed out after " + string timeoutMs + " ms" + Environment.NewLine

let environmentSnapshot () : (string * string)[] =
    let variables = Environment.GetEnvironmentVariables()

    [| for entry in variables.Keys do
         let key = entry :?> string

         if key.Length > 0 && key.IndexOf '=' < 0 then
             key, string variables.[entry] |]

type ExecuteRequest =
    { Mode: string
      Source: string
      WorkingDirectory: string
      Args: string[]
      Environment: (string * string)[]
      IncludeDiagnostics: bool
      SessionId: string option
      Reset: bool
      TimeoutMs: int
      PreludePath: string option
      NoPrelude: bool }

type ScriptDiagnostic =
    { Code: string
      Number: int
      Severity: string
      Message: string
      File: string
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int }

type ExecutionResult =
    { ExitCode: int
      StandardOut: string
      StandardError: string
      Diagnostics: ScriptDiagnostic[]
      TimedOut: bool }

type StatusInfo =
    { Pid: int
      Endpoint: string
      UptimeMs: int64
      IdleMs: int64
      Executed: int
      Warm: bool
      ProtocolVersion: int
      TargetFramework: string
      BuildFingerprint: string
      PoolSize: int
      IdleTtlMinutes: float
      SessionTtlMinutes: float
      GenerationId: string }
type MetricsInfo =
    { PoolTarget: int
      PoolWarm: int
      ColdClaims: int
      Executed: int
      ManagedHeapBytes: int64
      ScriptAssemblies: int
      AliveDisposedSessions: int
      NamedSessions: int }

type Request =
    | Execute of ExecuteRequest
    | GetStatus
    | GetMetrics
    | Shutdown
    | Ping of preludePath: string option * noPrelude: bool

type Response =
    | Executed of ExecutionResult
    | Status of StatusInfo
    | Metrics of MetricsInfo
    | ShuttingDown
    | Pong
    | Failure of message: string

let private utf8 = UTF8Encoding(false)

let private writeRequestPayload (writer: BinaryWriter) (request: Request) =
    writer.Write ProtocolVersion
    match request with
    | Execute payload ->
        writer.Write 1uy
        writer.Write payload.Mode
        writer.Write payload.Source
        writer.Write payload.WorkingDirectory
        writer.Write payload.Args.Length
        for argument in payload.Args do
            writer.Write argument
        writer.Write payload.Environment.Length
        for key, value in payload.Environment do
            writer.Write key
            writer.Write value
        writer.Write payload.IncludeDiagnostics
        writer.Write payload.SessionId.IsSome

        match payload.SessionId with
        | Some sessionId -> writer.Write sessionId
        | None -> ()

        writer.Write payload.Reset
        writer.Write payload.TimeoutMs
        writer.Write payload.NoPrelude
        writer.Write payload.PreludePath.IsSome

        match payload.PreludePath with
        | Some preludePath -> writer.Write preludePath
        | None -> ()
    | GetStatus -> writer.Write 2uy
    | GetMetrics -> writer.Write 5uy
    | Shutdown -> writer.Write 3uy
    | Ping (preludePath, noPrelude) ->
        writer.Write 4uy
        writer.Write noPrelude
        writer.Write preludePath.IsSome

        match preludePath with
        | Some path -> writer.Write path
        | None -> ()

let private writeResponsePayload (writer: BinaryWriter) (response: Response) =
    writer.Write ProtocolVersion
    match response with
    | Executed result ->
        writer.Write 11uy
        writer.Write result.ExitCode
        writer.Write result.StandardOut
        writer.Write result.StandardError
        writer.Write result.TimedOut
        writer.Write result.Diagnostics.Length
        for diagnostic in result.Diagnostics do
            writer.Write diagnostic.Code
            writer.Write diagnostic.Number
            writer.Write diagnostic.Severity
            writer.Write diagnostic.Message
            writer.Write diagnostic.File
            writer.Write diagnostic.StartLine
            writer.Write diagnostic.StartColumn
            writer.Write diagnostic.EndLine
            writer.Write diagnostic.EndColumn
    | Status status ->
        writer.Write 12uy
        writer.Write status.Pid
        writer.Write status.Endpoint
        writer.Write status.UptimeMs
        writer.Write status.IdleMs
        writer.Write status.Executed
        writer.Write status.Warm
        writer.Write status.ProtocolVersion
        writer.Write status.TargetFramework
        writer.Write status.BuildFingerprint
        writer.Write status.PoolSize
        writer.Write status.IdleTtlMinutes
        writer.Write status.SessionTtlMinutes
        writer.Write status.GenerationId
    | Metrics metrics ->
        writer.Write 16uy
        writer.Write metrics.PoolTarget
        writer.Write metrics.PoolWarm
        writer.Write metrics.ColdClaims
        writer.Write metrics.Executed
        writer.Write metrics.ManagedHeapBytes
        writer.Write metrics.ScriptAssemblies
        writer.Write metrics.AliveDisposedSessions
        writer.Write metrics.NamedSessions
    | ShuttingDown -> writer.Write 13uy
    | Pong -> writer.Write 14uy
    | Failure message ->
        writer.Write 15uy
        writer.Write message

let private payloadOf (write: BinaryWriter -> unit) =
    use stream = new MemoryStream()
    do
        use writer = new BinaryWriter(stream, utf8, true)
        write writer
        writer.Flush()
    stream.ToArray()

let private sendFramed (stream: Stream) (payload: byte[]) =
    use frameStream = new MemoryStream()
    do
        use writer = new BinaryWriter(frameStream, utf8, true)
        writer.Write payload.Length
        writer.Write payload
        writer.Flush()
    let frame = frameStream.ToArray()
    stream.Write(frame, 0, frame.Length)
    stream.Flush()

let sendRequest (stream: Stream) (request: Request) =
    sendFramed stream (payloadOf (fun writer -> writeRequestPayload writer request))

let sendResponse (stream: Stream) (response: Response) =
    sendFramed stream (payloadOf (fun writer -> writeResponsePayload writer response))

let private readExactly (stream: Stream) (count: int) =
    let buffer = Array.zeroCreate count
    let mutable offset = 0
    let mutable complete = true
    while complete && offset < count do
        let read = stream.Read(buffer, offset, count - offset)
        if read <= 0 then
            complete <- false
        else
            offset <- offset + read
    if complete then Some buffer else None

let tryReceiveFrame (stream: Stream) : byte[] option =
    match readExactly stream 4 with
    | None -> None
    | Some header ->
        let length = BitConverter.ToInt32(header, 0)
        if length <= 0 || length > 64 * 1024 * 1024 then
            failwith ("invalid frame length " + string length)
        readExactly stream length

// Control requests (`GetStatus` = 2uy, `Shutdown` = 3uy) and control/error responses (`Status` = 12uy,
// `ShuttingDown` = 13uy, `Failure` = 15uy) are decoded regardless of wire `ProtocolVersion` so a newer
// or older `fsion` CLI can always inspect and stop/restart a mismatched `fsiond` on an explicit `--pipe`.
let parseRequest (payload: byte[]) : Request =
    use stream = new MemoryStream(payload)
    use reader = new BinaryReader(stream, utf8)
    let version = reader.ReadInt32()
    let kind = reader.ReadByte()
    match kind with
    | 2uy -> GetStatus
    | 3uy -> Shutdown
    | _ ->
        if version <> ProtocolVersion then
            failwith ("unsupported protocol version " + string version + " (expected " + string ProtocolVersion + ")")
        match kind with
        | 1uy ->
            let mode = reader.ReadString()
            let source = reader.ReadString()
            let workingDirectory = reader.ReadString()
            let count = reader.ReadInt32()
            let args = Array.init count (fun _ -> reader.ReadString())
            let environmentCount = reader.ReadInt32()
            let environment = Array.init environmentCount (fun _ -> reader.ReadString(), reader.ReadString())
            let includeDiagnostics = reader.ReadBoolean()
            let hasSession = reader.ReadBoolean()
            let sessionId = if hasSession then Some(reader.ReadString()) else None
            let reset = reader.ReadBoolean()
            let timeoutMs = reader.ReadInt32()
            let noPrelude = reader.ReadBoolean()
            let hasPrelude = reader.ReadBoolean()
            let preludePath = if hasPrelude then Some(reader.ReadString()) else None
            Execute
                { Mode = mode
                  Source = source
                  WorkingDirectory = workingDirectory
                  Args = args
                  Environment = environment
                  IncludeDiagnostics = includeDiagnostics
                  SessionId = sessionId
                  Reset = reset
                  TimeoutMs = timeoutMs
                  PreludePath = preludePath
                  NoPrelude = noPrelude }
        | 5uy -> GetMetrics
        | 4uy ->
            let noPrelude = reader.ReadBoolean()
            let hasPrelude = reader.ReadBoolean()
            let preludePath = if hasPrelude then Some(reader.ReadString()) else None
            Ping(preludePath, noPrelude)
        | _ -> failwith ("unknown request kind " + string kind)

let parseResponse (payload: byte[]) : Response =
    use stream = new MemoryStream(payload)
    use reader = new BinaryReader(stream, utf8)
    let version = reader.ReadInt32()
    let kind = reader.ReadByte()
    match kind with
    | 12uy ->
        let pid = reader.ReadInt32()
        let endpoint = reader.ReadString()
        let uptimeMs = reader.ReadInt64()
        let idleMs = reader.ReadInt64()
        let executed = reader.ReadInt32()
        let warm = reader.ReadBoolean()
        let protoVer, tfm, fingerprint, poolSize, idleTtl, sessionTtl, genId =
            if stream.Position < stream.Length then
                let pv = reader.ReadInt32()
                let t = reader.ReadString()
                let fp = reader.ReadString()
                let ps = reader.ReadInt32()
                let it = reader.ReadDouble()
                let st = reader.ReadDouble()
                let gid = if stream.Position < stream.Length then reader.ReadString() else ""
                pv, t, fp, ps, it, st, gid
            else
                version, "", "", 0, 0.0, 0.0, ""
        Status
            { Pid = pid
              Endpoint = endpoint
              UptimeMs = uptimeMs
              IdleMs = idleMs
              Executed = executed
              Warm = warm
              ProtocolVersion = protoVer
              TargetFramework = tfm
              BuildFingerprint = fingerprint
              PoolSize = poolSize
              IdleTtlMinutes = idleTtl
              SessionTtlMinutes = sessionTtl
              GenerationId = genId }
    | 13uy -> ShuttingDown
    | 15uy -> Failure(reader.ReadString())
    | _ ->
        if version <> ProtocolVersion then
            failwith ("unsupported protocol version " + string version + " (expected " + string ProtocolVersion + ")")
        match kind with
        | 11uy ->
            let exitCode = reader.ReadInt32()
            let standardOut = reader.ReadString()
            let standardError = reader.ReadString()
            let timedOut = reader.ReadBoolean()
            let diagnosticsCount = reader.ReadInt32()
            let diagnostics =
                Array.init diagnosticsCount (fun _ ->
                    { Code = reader.ReadString()
                      Number = reader.ReadInt32()
                      Severity = reader.ReadString()
                      Message = reader.ReadString()
                      File = reader.ReadString()
                      StartLine = reader.ReadInt32()
                      StartColumn = reader.ReadInt32()
                      EndLine = reader.ReadInt32()
                      EndColumn = reader.ReadInt32() })
            Executed
                { ExitCode = exitCode
                  StandardOut = standardOut
                  StandardError = standardError
                  Diagnostics = diagnostics
                  TimedOut = timedOut }
        | 16uy ->
            let poolTarget = reader.ReadInt32()
            let poolWarm = reader.ReadInt32()
            let coldClaims = reader.ReadInt32()
            let executed = reader.ReadInt32()
            let managedHeapBytes = reader.ReadInt64()
            let scriptAssemblies = reader.ReadInt32()
            let aliveDisposedSessions = reader.ReadInt32()
            let namedSessions = reader.ReadInt32()
            Metrics
                { PoolTarget = poolTarget
                  PoolWarm = poolWarm
                  ColdClaims = coldClaims
                  Executed = executed
                  ManagedHeapBytes = managedHeapBytes
                  ScriptAssemblies = scriptAssemblies
                  AliveDisposedSessions = aliveDisposedSessions
                  NamedSessions = namedSessions }
        | 14uy -> Pong
        | _ -> failwith ("unknown response kind " + string kind)
