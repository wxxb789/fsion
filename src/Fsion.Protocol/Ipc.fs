module Fsion.Ipc

open System
open System.IO
open System.IO.Pipes
open System.Net.Sockets
open System.Runtime.InteropServices
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

let socketPath (endpoint: Endpoint) =
    if Path.IsPathRooted endpoint.Name then
        endpoint.Name
    else
        let baseDirectory =
            match Environment.GetEnvironmentVariable "XDG_RUNTIME_DIR" with
            | null | "" -> Path.GetTempPath()
            | directory -> directory
        Path.Combine(baseDirectory, sanitizeName endpoint.Name + ".sock")

let endpointDisplay (endpoint: Endpoint) =
    if isWindows then endpoint.Name else socketPath endpoint

let connect (endpoint: Endpoint) (timeout: TimeSpan) : Stream =
    let deadline = DateTime.UtcNow + timeout
    let rec attempt () =
        try
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
        with ex ->
            if DateTime.UtcNow >= deadline then
                raise (TimeoutException("fsiond endpoint '" + endpointDisplay endpoint + "' is not reachable", ex))
            Thread.Sleep 20
            attempt ()
    attempt ()

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
    if isWindows then
        new WindowsListener(endpoint.Name) :> IListener
    else
        new UnixListener(socketPath endpoint) :> IListener
