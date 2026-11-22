module Fsiond.SessionHost

open System
open System.Globalization
open System.IO
open System.Reflection
open System.Text
open System.Threading
open FSharp.Compiler.Interactive.Shell
open Fsion.Protocol

type RunOutcome =
    { ExitCode: int
      StandardOut: string
      StandardError: string }

type private NoopEventLoop() =
    member _.Run() = true
    member _.Invoke(code: unit -> 'T) : 'T = code ()
    member _.ScheduleRestart() = ()

type private Settings() =
    member val FormatProvider : IFormatProvider = CultureInfo.InvariantCulture :> IFormatProvider
    member val FloatingPointFormat : string = "g10"

    member val AddedPrinters : Choice<Type * (obj -> string), Type * (obj -> obj)> list = []

    member val ShowDeclarationValues = true
    member val ShowIEnumerable = true
    member val ShowProperties = true
    member val PrintSize = 10000
    member val PrintDepth = 100
    member val PrintWidth = 78
    member val PrintLength = 100
    member val CommandLineArgs : string[] = [||] with get, set
    member val EventLoop : obj = box (NoopEventLoop()) with get, set

type private ForwardingWriter(target: unit -> TextWriter) =
    inherit TextWriter()

    override _.Encoding = UTF8Encoding(false)
    override _.Write(value: char) = target().Write value
    override _.Write(value: string) = target().Write value
    override _.Flush() = target().Flush()

type SessionHost() =
    let gate = new SemaphoreSlim(1, 1)
    let mutable session: FsiEvaluationSession option = None
    let mutable stateObject: obj = null
    let mutable commandLineArgsProperty: PropertyInfo = null
    let mutable exitedProperty: PropertyInfo = null
    let mutable exitCodeProperty: PropertyInfo = null
    let warm = ref false
    let executed = ref 0
    let mutable currentOut: TextWriter = TextWriter.Null
    let mutable currentErr: TextWriter = TextWriter.Null
    let mutable includeDirectory = ""
    let exitMarker = "@@FSION-EXIT-" + Guid.NewGuid().ToString("N") + "@@"
    let forwardingOut = new ForwardingWriter(fun () -> currentOut)
    let forwardingErr = new ForwardingWriter(fun () -> currentErr)

    let sessionArgs =
        [| "fsion"
           "--nologo"
           "--quiet"
           "--noninteractive"
           "--optimize-"
           "--multiemit-"
           "--targetprofile:netcore" |]

    let prelude =
        String.Concat(
            [ "type FsionFsiState() =\n"
              "    member val CommandLineArgs : string[] = [||] with get, set\n"
              "    member val Exited : bool = false with get, set\n"
              "    member val ExitCode : int = 0 with get, set\n"
              "let fsionFsiState = FsionFsiState()\n"
              "let fsi = fsionFsiState\n"
              "exception FsionExit of int\n"
              "let exit (code: int) : 'T =\n"
              "    fsionFsiState.Exited <- true\n"
              "    fsionFsiState.ExitCode <- code\n"
              "    System.Console.Error.Write(\""
              exitMarker
              "\")\n"
              "    raise (FsionExit code)\n" ]
        )

    let createSession () =
        let settings = Settings()
        let config = FsiEvaluationSession.GetDefaultConfiguration(settings, false)
        let created =
            FsiEvaluationSession.Create(
                config,
                sessionArgs,
                new StringReader(""),
                forwardingOut,
                forwardingErr,
                collectible = true
            )
        created.EvalInteraction prelude
        let state =
            match created.TryFindBoundValue "fsionFsiState" with
            | Some bound -> bound.Value.ReflectionValue
            | None -> failwith "fsion prelude did not expose fsionFsiState"
        let stateType = state.GetType()
        stateObject <- state
        commandLineArgsProperty <- stateType.GetProperty "CommandLineArgs"
        exitedProperty <- stateType.GetProperty "Exited"
        exitCodeProperty <- stateType.GetProperty "ExitCode"
        created

    let ensureSession () =
        match session with
        | Some _ -> ()
        | None ->
            session <- Some(createSession ())
            warm.Value <- true

    let ensureIncludeDirectory (active: FsiEvaluationSession) (directory: string) =
        if includeDirectory <> directory then
            let escaped = directory.Replace("\"", "\"\"")
            active.EvalInteraction("#cd @\"" + escaped + "\"")
            includeDirectory <- directory

    member _.EnsureWarm() =
        gate.Wait()
        try
            ensureSession ()
        finally
            gate.Release() |> ignore

    member _.IsWarm = warm.Value

    member _.ExecutedCount = executed.Value

    member _.Execute(request: ExecuteRequest) : RunOutcome =
        gate.Wait()
        try
            ensureSession ()
            let outWriter = new StringWriter()
            let errWriter = new StringWriter()
            currentOut <- outWriter
            currentErr <- errWriter
            exitedProperty.SetValue(stateObject, box false)
            exitCodeProperty.SetValue(stateObject, box 0)
            commandLineArgsProperty.SetValue(stateObject, request.Args)
            let previousDirectory = Directory.GetCurrentDirectory()
            let previousOut = Console.Out
            let previousError = Console.Error
            try
                Directory.SetCurrentDirectory request.WorkingDirectory
                Console.SetOut outWriter
                Console.SetError errWriter
                let evaluate () =
                    let active =
                        match session with
                        | Some value -> value
                        | None -> failwith "fsion session was not initialised"

                    if request.Mode = "file" then
                        let sourcePath =
                            if Path.IsPathRooted request.Source then
                                request.Source
                            else
                                Path.Combine(request.WorkingDirectory, request.Source)

                        if File.Exists sourcePath then
                            let directory = Path.GetDirectoryName sourcePath
                            ensureIncludeDirectory active directory
                            active.EvalScript(Path.GetFileName sourcePath)
                        else
                            ensureIncludeDirectory active request.WorkingDirectory
                            let escaped = request.Source.Replace("\"", "\"\"")
                            try
                                active.EvalInteraction("#load @\"" + escaped + "\"", "stdin")
                            with :? FsiCompilationException ->
                                // dotnet fsi reports the synthetic #load location as stdin(0,1); FCS uses stdin(1,1).
                                errWriter.GetStringBuilder().Replace("stdin(1,1): error FS0078", "stdin(0,1): error FS0078")
                                |> ignore
                                reraise ()
                    else
                        ensureIncludeDirectory active request.WorkingDirectory
                        let sourceName = if request.Mode = "stdin" then "stdin.fsx" else "eval.fsx"
                        active.EvalInteraction(request.Source + "\n()", sourceName)
                let exitCode =
                    try
                        evaluate ()
                        0
                    with :? FsiCompilationException ->
                        if exitedProperty.GetValue stateObject :?> bool then
                            let text = errWriter.ToString()
                            let markerIndex = text.IndexOf(exitMarker, StringComparison.Ordinal)
                            if markerIndex >= 0 then
                                errWriter.GetStringBuilder().Length <- markerIndex
                            exitCodeProperty.GetValue stateObject :?> int
                        else
                            1
                executed.Value <- executed.Value + 1
                { ExitCode = exitCode
                  StandardOut = outWriter.ToString()
                  StandardError = errWriter.ToString() }
            finally
                Console.SetOut previousOut
                Console.SetError previousError
                Directory.SetCurrentDirectory previousDirectory
                currentOut <- TextWriter.Null
                currentErr <- TextWriter.Null
        finally
            gate.Release() |> ignore
