module Fsion.Ipc

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

type Endpoint = { Name: string }

let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

let sanitizeName (name: string) =
    let mapped =
        name.ToCharArray()
        |> Array.map (fun character ->
            if Char.IsLetterOrDigit character || character = '-' || character = '_' || character = '.' then character
            else '_')
    new String(mapped)

let defaultEndpointName () = "fsion-" + sanitizeName Environment.UserName

let validateEndpointName (name: string) =
    if name.Contains '\\' || name.Contains '/' then
        raise (ArgumentException("pipe name must not contain path separators"))
let socketPath (endpoint: Endpoint) =
    if Path.IsPathRooted endpoint.Name then
        endpoint.Name
    else
        let baseDirectory =
            match Environment.GetEnvironmentVariable "XDG_RUNTIME_DIR" with
            | null | "" -> if OperatingSystem.IsMacOS() && Directory.Exists "/tmp" then "/tmp" else Path.GetTempPath()
            | directory -> directory

        let sanitized = sanitizeName endpoint.Name
        let candidate = Path.Combine(baseDirectory, sanitized + ".sock")

        if Encoding.UTF8.GetByteCount candidate <= 100 then
            candidate
        else
            let hash =
                SHA256.HashData(Encoding.UTF8.GetBytes sanitized)
                |> Convert.ToHexString
                |> fun value -> value.Substring(0, 16).ToLowerInvariant()

            Path.Combine(baseDirectory, "fsion-" + hash + ".sock")
let endpointDisplay (endpoint: Endpoint) =
    if isWindows then endpoint.Name else socketPath endpoint

let connect (endpoint: Endpoint) (timeout: TimeSpan) : Stream =
    validateEndpointName endpoint.Name
    let deadline = DateTime.UtcNow + timeout
    let mutable stream: Stream = null
    while isNull stream do
        try
            let candidate =
                if isWindows then
                    let pipe =
                        new NamedPipeClientStream(".", endpoint.Name, PipeDirection.InOut, PipeOptions.Asynchronous)
                    try
                        pipe.Connect 250
                        pipe :> Stream
                    with _ ->
                        pipe.Dispose()
                        reraise ()
                else
                    let socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                    try
                        socket.Connect(UnixDomainSocketEndPoint(socketPath endpoint))
                        new NetworkStream(socket, true) :> Stream
                    with _ ->
                        socket.Dispose()
                        reraise ()
            stream <- candidate
        with ex ->
            if DateTime.UtcNow >= deadline then
                raise (TimeoutException("fsiond endpoint '" + endpointDisplay endpoint + "' is not reachable", ex))
            Thread.Sleep 20
    stream

let tryConnect (endpoint: Endpoint) (timeout: TimeSpan) : Stream option =
    try
        Some(connect endpoint timeout)
    with _ ->
        None

type IListener =
    inherit IDisposable
    abstract Accept: unit -> Task<Stream>
    abstract Stop: unit -> unit

type private WindowsListener(name: string) =
    let cancellation = new CancellationTokenSource()
    let mutable pending: NamedPipeServerStream option = None

    member _.Accept() : Task<Stream> =
        task {
            let server =
                new NamedPipeServerStream(
                    name,
                    PipeDirection.InOut,
                    64,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                )
            pending <- Some server
            try
                do! server.WaitForConnectionAsync cancellation.Token
                pending <- None
                return server :> Stream
            with ex ->
                pending <- None
                server.Dispose()
                return raise ex
        }

    member _.Stop() =
        cancellation.Cancel()
        pending
        |> Option.iter (fun server ->
            try
                server.Dispose()
            with _ ->
                ())

    interface IListener with
        member this.Accept() = this.Accept()
        member this.Stop() = this.Stop()
        member this.Dispose() = this.Stop()

type private UnixListener(path: string) =
    let cancellation = new CancellationTokenSource()
    let socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

    do
        socket.Bind(UnixDomainSocketEndPoint path)
        socket.Listen 64

    member _.Accept() : Task<Stream> =
        task {
            let! client = socket.AcceptAsync cancellation.Token
            return new NetworkStream(client, true) :> Stream
        }

    member _.Stop() =
        cancellation.Cancel()
        try
            socket.Dispose()
        with _ ->
            ()
        try
            File.Delete path
        with _ ->
            ()

    interface IListener with
        member this.Accept() = this.Accept()
        member this.Stop() = this.Stop()
        member this.Dispose() = this.Stop()

let createListener (endpoint: Endpoint) : IListener =
    validateEndpointName endpoint.Name

    if isWindows then
        new WindowsListener(endpoint.Name) :> IListener
    else
        new UnixListener(socketPath endpoint) :> IListener

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

let private quoteWindowsArgument (value: string) =
    if value.Length = 0 then
        "\"\""
    elif not (value.Contains ' ' || value.Contains '\t' || value.Contains '"') then
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

// Process.Start sets bInheritHandles=true on Windows, so a long-lived child would inherit
// the caller's stdout/stderr pipe write ends and keep a redirected ReadToEnd blocked after
// the caller exits. CreateProcessW with bInheritHandles=false leaves the child detached.
let private spawnDetachedWindows (executable: string) (arguments: string list) =
    let commandLine = executable :: arguments |> List.map quoteWindowsArgument |> String.concat " "
    let mutable startupInfo = Unchecked.defaultof<StartupInfo>
    startupInfo.Cb <- uint32 (Marshal.SizeOf<StartupInfo>())
    let mutable processInformation = Unchecked.defaultof<ProcessInformation>
    let creationFlags = 0x08000000u ||| 0x00000008u

    let created =
        CreateProcessW(
            executable,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            creationFlags,
            IntPtr.Zero,
            Path.GetDirectoryName executable,
            &startupInfo,
            &processInformation
        )

    if not created then
        raise (Win32Exception(Marshal.GetLastWin32Error(), "failed to start " + executable))

    if processInformation.Process <> IntPtr.Zero then
        CloseHandle processInformation.Process |> ignore

    if processInformation.Thread <> IntPtr.Zero then
        CloseHandle processInformation.Thread |> ignore

let private spawnDetachedManaged (executable: string) (arguments: string list) =
    let startInfo = ProcessStartInfo(executable)
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.RedirectStandardInput <- true
    startInfo.WorkingDirectory <- Path.GetDirectoryName executable

    for argument in arguments do
        startInfo.ArgumentList.Add argument

    let child = Process.Start startInfo

    if isNull child then
        raise (InvalidOperationException("failed to start " + executable))

    Task.Run(fun () ->
        try
            child.StandardOutput.ReadToEnd() |> ignore
        with _ ->
            ())
    |> ignore

    Task.Run(fun () ->
        try
            child.StandardError.ReadToEnd() |> ignore
        with _ ->
            ())
    |> ignore

let spawnDetached (executable: string) (arguments: string list) =
    if isWindows then
        spawnDetachedWindows executable arguments
    else
        spawnDetachedManaged executable arguments
