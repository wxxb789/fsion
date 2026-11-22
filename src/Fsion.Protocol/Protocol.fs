module Fsion.Protocol

open System
open System.IO
open System.Text

let [<Literal>] ProtocolVersion = 1

let [<Literal>] PipeEnvironmentVariable = "FSION_PIPE"

let [<Literal>] IdleTtlEnvironmentVariable = "FSION_IDLE_TTL_MINUTES"

let [<Literal>] DefaultIdleTtlMinutes = 30.0

type ExecuteRequest =
    { Mode: string
      Source: string
      WorkingDirectory: string
      Args: string[] }

type StatusInfo =
    { Pid: int
      Endpoint: string
      UptimeMs: int64
      IdleMs: int64
      Executed: int
      Warm: bool }

type Request =
    | Execute of ExecuteRequest
    | GetStatus
    | Shutdown
    | Ping

type Response =
    | Executed of exitCode: int * standardOut: string * standardError: string
    | Status of StatusInfo
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
    | GetStatus -> writer.Write 2uy
    | Shutdown -> writer.Write 3uy
    | Ping -> writer.Write 4uy

let private writeResponsePayload (writer: BinaryWriter) (response: Response) =
    writer.Write ProtocolVersion
    match response with
    | Executed (exitCode, standardOut, standardError) ->
        writer.Write 11uy
        writer.Write exitCode
        writer.Write standardOut
        writer.Write standardError
    | Status status ->
        writer.Write 12uy
        writer.Write status.Pid
        writer.Write status.Endpoint
        writer.Write status.UptimeMs
        writer.Write status.IdleMs
        writer.Write status.Executed
        writer.Write status.Warm
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

let private readVersion (reader: BinaryReader) =
    let version = reader.ReadInt32()
    if version <> ProtocolVersion then
        failwith ("unsupported protocol version " + string version)

let parseRequest (payload: byte[]) : Request =
    use stream = new MemoryStream(payload)
    use reader = new BinaryReader(stream, utf8)
    readVersion reader
    match reader.ReadByte() with
    | 1uy ->
        let mode = reader.ReadString()
        let source = reader.ReadString()
        let workingDirectory = reader.ReadString()
        let count = reader.ReadInt32()
        let args = Array.init count (fun _ -> reader.ReadString())
        Execute
            { Mode = mode
              Source = source
              WorkingDirectory = workingDirectory
              Args = args }
    | 2uy -> GetStatus
    | 3uy -> Shutdown
    | 4uy -> Ping
    | kind -> failwith ("unknown request kind " + string kind)

let parseResponse (payload: byte[]) : Response =
    use stream = new MemoryStream(payload)
    use reader = new BinaryReader(stream, utf8)
    readVersion reader
    match reader.ReadByte() with
    | 11uy ->
        let exitCode = reader.ReadInt32()
        let standardOut = reader.ReadString()
        let standardError = reader.ReadString()
        Executed(exitCode, standardOut, standardError)
    | 12uy ->
        let pid = reader.ReadInt32()
        let endpoint = reader.ReadString()
        let uptimeMs = reader.ReadInt64()
        let idleMs = reader.ReadInt64()
        let executed = reader.ReadInt32()
        let warm = reader.ReadBoolean()
        Status
            { Pid = pid
              Endpoint = endpoint
              UptimeMs = uptimeMs
              IdleMs = idleMs
              Executed = executed
              Warm = warm }
    | 13uy -> ShuttingDown
    | 14uy -> Pong
    | 15uy -> Failure(reader.ReadString())
    | kind -> failwith ("unknown response kind " + string kind)
