module Fsiond.Program

open System
open System.Globalization
open System.IO
open System.Threading
open Fsion.Ipc
open Fsion.Protocol
open Fsion.PreludeConfig
open Fsiond.Ambient
open Fsiond.Prelude
open Fsiond.Server

[<NoComparison>]
type private Options =
    { Endpoint: Endpoint
      IdleTtlMinutes: float
      SessionTtlMinutes: float
      Oneshot: bool
      PoolSize: int
      LogPath: string option
      Prelude: Spec }

let private defaultPoolSize = 3

let private parse (argv: string[]) =
    let mutable endpointName = defaultEndpointName ()
    let mutable endpointSet = false
    let mutable idleTtlMinutes = DefaultIdleTtlMinutes
    let mutable idleTtlSet = false
    let mutable sessionTtlMinutes = DefaultSessionTtlMinutes
    let mutable sessionTtlSet = false
    let mutable oneshot = false
    let mutable poolSize = defaultPoolSize
    let mutable poolSizeSet = false
    let mutable logPath = None
    let mutable preludePath = None
    let mutable noPrelude = false
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
        | "--pool-size" when index + 1 < argv.Length ->
            match Int32.TryParse(argv.[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, value when value >= 0 ->
                poolSize <- value
                poolSizeSet <- true
                index <- index + 2
            | _ -> valid <- false
        | "--session-ttl" when index + 1 < argv.Length ->
            match Double.TryParse(argv.[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value when value > 0.0 ->
                sessionTtlMinutes <- value
                sessionTtlSet <- true
                index <- index + 2
            | _ -> valid <- false
        | "--oneshot" ->
            oneshot <- true
            index <- index + 1
        | "--prelude" when index + 1 < argv.Length ->
            preludePath <- Some argv.[index + 1]
            index <- index + 2
        | "--no-prelude" ->
            noPrelude <- true
            index <- index + 1
        | "--log" when index + 1 < argv.Length ->
            logPath <- Some argv.[index + 1]
            index <- index + 2
        | _ -> valid <- false
    if (not valid || (noPrelude && preludePath.IsSome)) && argv.Length > 0 then
        Log.error logPath ("invalid arguments: " + String.Join(" ", argv))
        None
    else
        let envEndpoint =
            if endpointSet then
                endpointName
            else
                match Environment.GetEnvironmentVariable PipeEnvironmentVariable with
                | null | "" -> endpointName
                | value -> value
        let envTtl =
            if idleTtlSet then
                idleTtlMinutes
            else
                match Environment.GetEnvironmentVariable IdleTtlEnvironmentVariable with
                | null | "" -> idleTtlMinutes
                | value ->
                    match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
                    | true, parsed -> parsed
                    | _ -> idleTtlMinutes
        let envPoolSize =
            if poolSizeSet then
                poolSize
            else
                match Environment.GetEnvironmentVariable PoolSizeEnvironmentVariable with
                | null | "" -> poolSize
                | value ->
                    match Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                    | true, parsed when parsed >= 0 -> parsed
                    | _ -> poolSize
        let envSessionTtl =
            if sessionTtlSet then
                sessionTtlMinutes
            else
                match Environment.GetEnvironmentVariable SessionTtlEnvironmentVariable with
                | null | "" -> sessionTtlMinutes
                | value ->
                    match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
                    | true, parsed when parsed > 0.0 -> parsed
                    | _ -> sessionTtlMinutes
        let configuredPrelude =
            if preludePath.IsSome then
                preludePath
            else
                configuredPreludePath ()
        let prelude =
            if noPrelude then
                Spec.Bare
            else
                match configuredPrelude |> Option.orElseWith userPreludePath with
                | Some path -> Spec.File(Path.GetFullPath path)
                | None -> Spec.Builtin

        Some
            { Endpoint = { Name = envEndpoint }
              IdleTtlMinutes = envTtl
              SessionTtlMinutes = envSessionTtl
              Oneshot = oneshot
              PoolSize = envPoolSize
              LogPath = logPath
              Prelude = prelude }

let private runDaemon (options: Options) (releaseOwnership: unit -> unit) =
    let serverOptions: Server.Options =
        { Endpoint = options.Endpoint
          IdleTtlMinutes = options.IdleTtlMinutes
          SessionTtlMinutes = options.SessionTtlMinutes
          Oneshot = options.Oneshot
          PoolSize = options.PoolSize
          LogPath = options.LogPath
          Prelude = options.Prelude }
    let listener = createListener options.Endpoint
    Server.run serverOptions listener releaseOwnership

[<EntryPoint>]
let main argv =
    Ambient.install ()

    match parse argv with
    | None -> 1
    | Some options when options.Oneshot -> runDaemon options ignore
    | Some options ->
        if isWindows then
            let ownershipName = "Local\\fsion-endpoint-" + sanitizeName options.Endpoint.Name
            let ownership = new Semaphore(1, 1, ownershipName)
            let acquired = ownership.WaitOne(TimeSpan.FromSeconds 2.0)

            if not acquired then
                ownership.Dispose()
                Log.info options.LogPath "another fsiond owns this endpoint; exiting"
                0
            else
                let mutable released = 0

                let release () =
                    if Interlocked.Exchange(&released, 1) = 0 then
                        try
                            ownership.Release() |> ignore
                        with _ ->
                            ()

                        try
                            ownership.Dispose()
                        with _ ->
                            ()

                try
                    runDaemon options release
                finally
                    release ()
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
                runDaemon options ignore
