module Fsiond.Program

open System
open System.Globalization
open System.IO
open System.Threading
open Fsion.Ipc
open Fsiond.Server

type private Options =
    { Endpoint: Endpoint
      IdleTtlMinutes: float
      Oneshot: bool
      LogPath: string option }

let private parse (argv: string[]) =
    let mutable endpointName = defaultEndpointName ()
    let mutable endpointSet = false
    let mutable idleTtlMinutes = 30.0
    let mutable idleTtlSet = false
    let mutable oneshot = false
    let mutable logPath = None
    let mutable index = 0
    let mutable valid = true
    while valid && index < argv.Length do
        match argv.[index] with
        | "--endpoint" when index + 1 < argv.Length ->
            endpointName <- argv.[index + 1]
            endpointSet <- true
            index <- index + 2
        | "--idle-ttl" when index + 1 < argv.Length ->
            match Double.TryParse(argv.[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value ->
                idleTtlMinutes <- value
                idleTtlSet <- true
                index <- index + 2
            | _ -> valid <- false
        | "--oneshot" ->
            oneshot <- true
            index <- index + 1
        | "--log" when index + 1 < argv.Length ->
            logPath <- Some argv.[index + 1]
            index <- index + 2
        | _ -> valid <- false
    if not valid && argv.Length > 0 then
        Log.error logPath ("invalid arguments: " + String.Join(" ", argv))
        None
    else
        let envEndpoint =
            if endpointSet then
                endpointName
            else
                match Environment.GetEnvironmentVariable "FSION_PIPE" with
                | null | "" -> endpointName
                | value -> value
        let envTtl =
            if idleTtlSet then
                idleTtlMinutes
            else
                match Environment.GetEnvironmentVariable "FSION_IDLE_TTL_MINUTES" with
                | null | "" -> idleTtlMinutes
                | value ->
                    match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
                    | true, parsed -> parsed
                    | _ -> idleTtlMinutes
        Some
            { Endpoint = { Name = envEndpoint }
              IdleTtlMinutes = envTtl
              Oneshot = oneshot
              LogPath = logPath }

let private runDaemon (options: Options) =
    let serverOptions =
        { Endpoint = options.Endpoint
          IdleTtlMs = int64 (TimeSpan.FromMinutes(options.IdleTtlMinutes).TotalMilliseconds)
          Oneshot = options.Oneshot
          LogPath = options.LogPath }
    let listener = createListener options.Endpoint
    Server.run serverOptions listener

[<EntryPoint>]
let main argv =
    Console.SetIn TextReader.Null
    match parse argv with
    | None -> 1
    | Some options when options.Oneshot -> runDaemon options
    | Some options ->
        if isWindows then
            let mutexName = "Local\\fsion-mutex-" + sanitizeName options.Endpoint.Name
            let mutable createdNew = false
            use mutex = new Mutex(true, mutexName, &createdNew)
            if not createdNew then
                Log.info options.LogPath "another fsiond owns this endpoint; exiting"
                0
            else
                runDaemon options
        else
            let path = socketPath options.Endpoint
            let ownedByOther =
                if File.Exists path then
                    match tryConnect options.Endpoint (TimeSpan.FromMilliseconds 400.0) with
                    | Some stream ->
                        stream.Dispose()
                        true
                    | None ->
                        try
                            File.Delete path
                        with _ ->
                            ()
                        false
                else
                    false
            if ownedByOther then
                Log.info options.LogPath "another fsiond owns this endpoint; exiting"
                0
            else
                runDaemon options
