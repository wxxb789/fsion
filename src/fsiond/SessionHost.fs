module Fsiond.SessionHost

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open Fsion.Protocol
open Fsiond.Ambient

type RunOutcome =
    { ExitCode: int
      StandardOut: string
      StandardError: string
      Diagnostics: ScriptDiagnostic[]
      TimedOut: bool }

type ExecutionReport =
    { Outcome: RunOutcome
      Wedged: bool }

type SessionHostOptions =
    { PoolSize: int
      SessionTtlMinutes: float }

type private RunTextWriter() =
    inherit TextWriter()

    let inner = new StringWriter()
    let gate = obj()

    member _.SafeText() = lock gate (fun () -> inner.ToString())

    member _.TruncateAt(index: int) = lock gate (fun () -> inner.GetStringBuilder().Length <- index)

    member _.ReplaceInBuilder(oldValue: string, newValue: string) =
        lock gate (fun () -> inner.GetStringBuilder().Replace(oldValue, newValue) |> ignore)

    override _.Encoding = inner.Encoding

    override _.Write(value: char) = lock gate (fun () -> inner.Write value)

    override _.Write(value: string) = lock gate (fun () -> inner.Write value)

    override _.Write(buffer: char[], index: int, count: int) =
        lock gate (fun () -> inner.Write(buffer, index, count))

    override _.WriteLine(value: string) = lock gate (fun () -> inner.WriteLine value)

    override _.WriteLine() = lock gate (fun () -> inner.WriteLine())

    override _.Flush() = lock gate (fun () -> inner.Flush())

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

type private WarmSession() =
    member val Session: FsiEvaluationSession option = None with get, set
    member val State: obj option = None with get, set
    member val CommandLineArgsProperty: PropertyInfo option = None with get, set
    member val ExitedProperty: PropertyInfo option = None with get, set
    member val ExitCodeProperty: PropertyInfo option = None with get, set
    member val CancellationTokenProperty: PropertyInfo option = None with get, set
    member val ExitMarker: string = "" with get, set
    member val ProcessExitHandlers: Delegate[] = [||] with get, set
    member val UnmanagedDllHandlers: Delegate[] = [||] with get, set
    member val AssemblyResolveHandlers: Delegate[] = [||] with get, set

[<NoComparison; NoEquality>]
type private CleanupWork =
    { Session: FsiEvaluationSession option
      ProcessExitHandlers: Delegate[]
      UnmanagedDllHandlers: Delegate[]
      AssemblyResolveHandlers: Delegate[] }

type private NamedSessionEntry() =
    let gate = new SemaphoreSlim(1, 1)
    member val Session: WarmSession option = None with get, set
    member val LastUsedMs = Environment.TickCount64 with get, set
    member val Evicted = false with get, set
    member _.Gate = gate

// FSI never removes its process-wide subscriptions when a collectible session is disposed:
// FsiDynamicCompiler adds an AppDomain.ProcessExit handler and the session's DependencyProvider
// adds an AssemblyLoadContext.Default.ResolvingUnmanagedDll handler, both capturing the session.
// The diagnostics AsyncLocal values are also left set on the creating/evaluating thread.
// All three roots must be undone or each one-shot session leaks ~27 MB and its file handles.
let private processExitField =
    typeof<AppDomain>.GetField("ProcessExit", BindingFlags.Instance ||| BindingFlags.NonPublic)

let private defaultLoadContext = AssemblyLoadContext.Default

let private unmanagedDllField =
    typeof<AssemblyLoadContext>.GetField("_resolvingUnmanagedDll", BindingFlags.Instance ||| BindingFlags.NonPublic)

let private unmanagedDllEvent = typeof<AssemblyLoadContext>.GetEvent "ResolvingUnmanagedDll"

let private invocationList (handler: Delegate) =
    if isNull handler then [||] else handler.GetInvocationList()

let private processExitSnapshot () : Delegate[] =
    invocationList (processExitField.GetValue AppDomain.CurrentDomain :?> EventHandler)

let private unmanagedDllSnapshot () : Delegate[] =
    invocationList (unmanagedDllField.GetValue defaultLoadContext :?> Func<Assembly, string, nativeint>)

let private assemblyResolveField =
    typeof<AssemblyLoadContext>.GetField("AssemblyResolve", BindingFlags.Static ||| BindingFlags.NonPublic)

let private assemblyResolveSnapshot () : Delegate[] =
    match assemblyResolveField.GetValue null with
    | null -> [||]
    | handler -> invocationList (handler :?> Delegate)

let private removeAssemblyResolve (handlers: Delegate[]) =
    if handlers.Length > 0 then
        let mutable current = assemblyResolveField.GetValue null :?> Delegate

        for handler in handlers do
            current <- Delegate.Remove(current, handler)

        assemblyResolveField.SetValue(null, current)

let private addedHandlers (before: Delegate[]) (after: Delegate[]) =
    after
    |> Array.filter (fun candidate ->
        not (before |> Array.exists (fun existing -> Object.ReferenceEquals(existing, candidate))))

let private removeProcessExit (handlers: Delegate[]) =
    if handlers.Length > 0 then
        let mutable current = processExitField.GetValue AppDomain.CurrentDomain :?> EventHandler

        for handler in handlers do
            current <- Delegate.Remove(current, handler) :?> EventHandler

        processExitField.SetValue(AppDomain.CurrentDomain, current)

let private removeUnmanagedDll (handlers: Delegate[]) =
    for handler in handlers do
        unmanagedDllEvent.RemoveEventHandler(defaultLoadContext, handler)

let private checkerField =
    typeof<FsiEvaluationSession>.GetField("checker", BindingFlags.Instance ||| BindingFlags.NonPublic)

let private tcConfigBField =
    typeof<FsiEvaluationSession>.GetField("tcConfigB", BindingFlags.Instance ||| BindingFlags.NonPublic)

// FCS stores a TcConfigBuilder callback closure that captures the session, and FSI's own
// process-global registrations capture an ExecutionContext whose diagnostics-logger AsyncLocal
// value references that TcConfigBuilder. The chain keeps every session (and its collectible
// assembly) alive after handlers are removed, so sever the closure edge before disposal.
let private detachSessionCallback (session: FsiEvaluationSession) =
    try
        match tcConfigBField.GetValue session with
        | null -> ()
        | tcConfigB ->
            for field in tcConfigB.GetType().GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) do
                if field.Name.StartsWith("tryGetMetadataSnapshot", StringComparison.Ordinal) then
                    try
                        field.SetValue(tcConfigB, null)
                    with _ ->
                        ()
    with _ ->
        ()

let private clearCheckerCaches (session: FsiEvaluationSession) =
    try
        match checkerField.GetValue session with
        | null -> ()
        | checker ->
            match checker.GetType().GetMethod("ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients") with
            | null -> ()
            | method -> method.Invoke(checker, [||]) |> ignore
    with _ ->
        ()

let private scriptAssemblies () =
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.filter (fun assembly ->
        let name = assembly.FullName
        not (isNull name) && name.StartsWith("FSI-", StringComparison.Ordinal))

// FSI emits one collectible FSI-ASSEMBLY dynamic assembly per session. Static fields on its
// FSI_000n types hold top-level bindings and leaked@ closures; a top-level
// `let reader = new StreamReader(...)` keeps its Windows file handle locked until the binding is
// disposed or the field is nulled (initonly fields cannot be nulled, so they must be disposed).
// Nulling the rest lets GC unload the assembly and finalize any remaining streams.
let private clearScriptStatics (assemblies: Assembly[]) =
    for assembly in assemblies do
        try
            for scriptType in assembly.GetTypes() do
                for field in scriptType.GetFields(BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic) do
                    if
                        not field.IsLiteral
                        && not field.FieldType.IsValueType
                        && not field.FieldType.IsPointer
                    then
                        try
                            match field.GetValue null with
                            | :? IDisposable as disposable ->
                                try
                                    disposable.Dispose()
                                with _ ->
                                    ()
                            | _ -> ()
                        with _ ->
                            ()

                        if not field.IsInitOnly then
                            try
                                field.SetValue(null, null)
                            with _ ->
                                ()
        with _ ->
            ()

let private compilerAsyncLocals =
    match typeof<FsiEvaluationSession>.Assembly.GetType "FSharp.Compiler.DiagnosticsLogger" with
    | null -> [||]
    | moduleType ->
        [| "diagnosticsLogger"; "buildPhase" |]
        |> Array.choose (fun name ->
            match moduleType.GetProperty(name, BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic) with
            | null -> None
            | property -> Some(property.GetValue null))

let private clearCompilerAsyncLocals () =
    for local in compilerAsyncLocals do
        let valueProperty = local.GetType().GetProperty "Value"
        let valueType = local.GetType().GetGenericArguments().[0]
        valueProperty.SetValue(local, Activator.CreateInstance valueType)

let private sweepFinalizers () =
    GC.Collect(2, GCCollectionMode.Forced, true, true)
    GC.WaitForPendingFinalizers()
    GC.Collect(2, GCCollectionMode.Forced, true, true)

// Top-level IDisposable bindings are disposed by clearScriptStatics; this Gen-0 sweep finalizes
// throwaway streams created inside functions so their Windows handles close before the response
// while the collectible unload (checker caches, session dispose, full sweep) stays off the
// request's critical path.
let private sweepTransientFinalizers () =
    GC.Collect(0, GCCollectionMode.Forced, true, false)
    GC.WaitForPendingFinalizers()

let private severityText (severity: FSharpDiagnosticSeverity) =
    match severity with
    | FSharpDiagnosticSeverity.Error -> "error"
    | FSharpDiagnosticSeverity.Warning -> "warning"
    | FSharpDiagnosticSeverity.Info -> "info"
    | FSharpDiagnosticSeverity.Hidden -> "hidden"

let private timeoutMessage (timeoutMs: int) =
    "fsion: script timed out after " + string timeoutMs + " ms" + Environment.NewLine

let private appendMessage (text: string) (message: string) =
    if text.Length = 0 || text.EndsWith("\n", StringComparison.Ordinal) then
        text + message
    else
        text + Environment.NewLine + message

let private scriptDiagnostic (diagnostic: FSharpDiagnostic) =
    { Code = diagnostic.ErrorNumberText
      Number = diagnostic.ErrorNumber
      Severity = severityText diagnostic.Severity
      Message = diagnostic.Message
      File = diagnostic.FileName
      StartLine = diagnostic.StartLine
      StartColumn = diagnostic.StartColumn
      EndLine = diagnostic.EndLine
      EndColumn = diagnostic.EndColumn }

// The throwing Eval* APIs print diagnostics to the session's error writer but construct
// FsiCompilationException with ErrorInfos = None; only the non-throwing APIs return the
// diagnostics. Clients that did not ask for structured diagnostics skip the re-run entirely.
let private sessionArguments =
    [| "fsion"
       "--nologo"
       "--quiet"
       "--noninteractive"
       "--optimize-"
       "--multiemit-"
       "--targetprofile:netcore" |]

let private prelude (exitMarker: string) =
    String.Concat(
        [ "type FsionFsiState() =\n"
          "    member val CommandLineArgs : string[] = [||] with get, set\n"
          "    member val Exited : bool = false with get, set\n"
          "    member val ExitCode : int = 0 with get, set\n"
          "    member val CancellationToken : System.Threading.CancellationToken = System.Threading.CancellationToken.None with get, set\n"
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

let private environmentComparer =
    if OperatingSystem.IsWindows() then
        StringComparer.OrdinalIgnoreCase
    else
        StringComparer.Ordinal

let private applyEnvironment (snapshot: (string * string)[]) =
    let desired = Dictionary<string, string>(environmentComparer)

    for key, value in snapshot do
        desired.[key] <- value

    let current = Environment.GetEnvironmentVariables()

    for entry in current.Keys do
        let key = entry :?> string

        if key.Length > 0 && key.IndexOf '=' < 0 && not (desired.ContainsKey key) then
            Environment.SetEnvironmentVariable(key, null, EnvironmentVariableTarget.Process)

    for key, value in snapshot do
        Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process)

let private environmentEquals (left: (string * string)[]) (right: (string * string)[]) =
    if left.Length <> right.Length then
        false
    else
        let map = Dictionary<string, string>(environmentComparer)

        for key, value in left do
            map.[key] <- value

        right
        |> Array.forall (fun (key, value) ->
            match map.TryGetValue key with
            | true, existing -> String.Equals(existing, value, StringComparison.Ordinal)
            | _ -> false)

// Process working directory and environment are OS-global. Runs that need the same
// (directory, environment) share one epoch and execute concurrently; a run that needs a
// different epoch waits for all active runs before transitioning. FSI's #cd only updates
// per-session compiler state, so script directories do not need to be part of the key.
type private ProcessStateGate() =
    let sync = obj ()
    let daemonDirectory = Directory.GetCurrentDirectory()
    let daemonEnvironment = environmentSnapshot ()
    let mutable activeRuns = 0
    let mutable currentDirectory = daemonDirectory
    let mutable currentEnvironment = daemonEnvironment

    member _.Enter(directory: string, environment: (string * string)[]) =
        lock sync (fun () ->
            let matches () =
                currentDirectory = directory && environmentEquals currentEnvironment environment

            while activeRuns > 0 && not (matches ()) do
                Monitor.Wait sync |> ignore

            if activeRuns = 0 then
                Directory.SetCurrentDirectory directory
                applyEnvironment environment
                currentDirectory <- directory
                currentEnvironment <- environment

            activeRuns <- activeRuns + 1)

    member _.Exit() =
        lock sync (fun () ->
            activeRuns <- activeRuns - 1

            if activeRuns = 0 then
                applyEnvironment daemonEnvironment
                Directory.SetCurrentDirectory daemonDirectory
                currentDirectory <- daemonDirectory
                currentEnvironment <- daemonEnvironment
                Monitor.PulseAll sync)

type SessionHost(options: SessionHostOptions) =
    let targetPoolSize = max 0 options.PoolSize
    let pool = ConcurrentQueue<WarmSession>()
    let creationGate = new SemaphoreSlim(1, 1)
    let replenishSignal = new SemaphoreSlim(0)
    let pendingCleanups = ConcurrentQueue<CleanupWork>()
    let releasedSessions = ConcurrentQueue<WeakReference>()
    let processState = ProcessStateGate()
    let coldClaims = ref 0
    let executed = ref 0
    let mutable pendingCreations = 0
    let mutable stopped = false

    let createWarmSession () : WarmSession =
        let processExitBefore = processExitSnapshot ()
        let unmanagedDllBefore = unmanagedDllSnapshot ()
        let assemblyResolveBefore = assemblyResolveSnapshot ()
        let settings = Settings()
        let config = FsiEvaluationSession.GetDefaultConfiguration(settings, false)

        let session =
            FsiEvaluationSession.Create(
                config,
                sessionArguments,
                new StringReader(""),
                outWriter,
                errorWriter,
                collectible = true
            )

        let processExitHandlers = addedHandlers processExitBefore (processExitSnapshot ())
        let unmanagedDllHandlers = addedHandlers unmanagedDllBefore (unmanagedDllSnapshot ())
        let assemblyResolveHandlers = addedHandlers assemblyResolveBefore (assemblyResolveSnapshot ())
        let exitMarker = "@@FSION-EXIT-" + Guid.NewGuid().ToString("N") + "@@"
        session.EvalInteraction(prelude exitMarker)
        let state =
            match session.TryFindBoundValue "fsionFsiState" with
            | Some bound -> bound.Value.ReflectionValue
            | None -> failwith "fsion prelude did not expose fsionFsiState"

        let stateType = state.GetType()
        clearCompilerAsyncLocals ()
        let warm = WarmSession()
        warm.Session <- Some session
        warm.State <- Some state
        warm.CommandLineArgsProperty <- Some(stateType.GetProperty "CommandLineArgs")
        warm.ExitedProperty <- Some(stateType.GetProperty "Exited")
        warm.ExitCodeProperty <- Some(stateType.GetProperty "ExitCode")
        warm.CancellationTokenProperty <- Some(stateType.GetProperty "CancellationToken")
        warm.ExitMarker <- exitMarker
        warm.ProcessExitHandlers <- processExitHandlers
        warm.UnmanagedDllHandlers <- unmanagedDllHandlers
        warm.AssemblyResolveHandlers <- assemblyResolveHandlers
        warm

    let claimSession () : WarmSession =
        match pool.TryDequeue() with
        | true, warm -> warm
        | _ ->
            Interlocked.Increment(&coldClaims.contents) |> ignore
            creationGate.Wait()

            try
                createWarmSession ()
            finally
                creationGate.Release() |> ignore

    let removeHandlers (work: CleanupWork) =
        removeProcessExit work.ProcessExitHandlers
        removeUnmanagedDll work.UnmanagedDllHandlers
        removeAssemblyResolve work.AssemblyResolveHandlers

    let drainReleasedSessions () =
        let survivors = ResizeArray<WeakReference>()
        let mutable weak = Unchecked.defaultof<WeakReference>

        while releasedSessions.TryDequeue(&weak) do
            if weak.IsAlive then
                survivors.Add weak

        for survivor in survivors do
            releasedSessions.Enqueue survivor

        survivors.Count

    let drainPendingCleanups () =
        let mutable work = Unchecked.defaultof<CleanupWork>
        let mutable drained = 0

        while pendingCleanups.TryDequeue(&work) do
            drained <- drained + 1

            match work.Session with
            | Some session ->
                detachSessionCallback session
                clearCheckerCaches session
                releasedSessions.Enqueue(WeakReference(session))

                try
                    (session :> IDisposable).Dispose()
                with _ ->
                    ()
            | None -> ()

            removeHandlers work

        if drained > 0 then
            sweepFinalizers ()
            drainReleasedSessions () |> ignore

    // Handler removal and collectible-session disposal need creationGate so delegate adds and
    // removes never interleave, but a run must not stall behind a slow background session
    // creation. The replenisher drains this queue right after it wakes (it runs with
    // --pool-size 0 too), and Metrics drains it before sampling.
    let drainCleanups () =
        if creationGate.Wait 0 then
            try
                drainPendingCleanups ()
            finally
                creationGate.Release() |> ignore

    let drainCleanupsBlocking () =
        creationGate.Wait()

        try
            drainPendingCleanups ()
        finally
            creationGate.Release() |> ignore

    let releaseSession (claimed: WarmSession) =
        match claimed.Session with
        | Some session -> clearScriptStatics session.DynamicAssemblies
        | None -> ()

        clearCompilerAsyncLocals ()
        sweepTransientFinalizers ()

        pendingCleanups.Enqueue
            { Session = claimed.Session
              ProcessExitHandlers = claimed.ProcessExitHandlers
              UnmanagedDllHandlers = claimed.UnmanagedDllHandlers
              AssemblyResolveHandlers = claimed.AssemblyResolveHandlers }

        claimed.Session <- None
        claimed.State <- None
        claimed.CommandLineArgsProperty <- None
        claimed.ExitedProperty <- None
        claimed.ExitCodeProperty <- None
        claimed.CancellationTokenProperty <- None
        claimed.ProcessExitHandlers <- [||]
        claimed.UnmanagedDllHandlers <- [||]
        claimed.AssemblyResolveHandlers <- [||]

    let evaluate
        (claimed: WarmSession)
        (request: ExecuteRequest)
        (outWriter: RunTextWriter)
        (errWriter: RunTextWriter)
        (cancellationToken: CancellationToken)
        : int * ScriptDiagnostic[] =
        let session = claimed.Session.Value
        let state = claimed.State.Value
        claimed.CommandLineArgsProperty.Value.SetValue(state, box request.Args)
        claimed.ExitedProperty.Value.SetValue(state, box false)
        claimed.ExitCodeProperty.Value.SetValue(state, box 0)

        let changeDirectory (directory: string) =
            session.EvalInteraction("#cd @\"" + directory.Replace("\"", "\"\"") + "\"")

        let sourceName = if request.Mode = "stdin" then "stdin.fsx" else "eval.fsx"

        let sourcePath =
            if Path.IsPathRooted request.Source then
                request.Source
            else
                Path.Combine(request.WorkingDirectory, request.Source)

        let sourceExists = request.Mode = "file" && File.Exists sourcePath
        let escapedSource = request.Source.Replace("\"", "\"\"")
        let mutable checkedSource = false

        let emittedTypeCount () =
            session.DynamicAssemblies
            |> Array.sumBy (fun assembly ->
                try
                    assembly.GetTypes().Length
                with _ ->
                    0)

        let run () =
            if request.Mode = "file" then
                if sourceExists then
                    let directory = Path.GetDirectoryName sourcePath

                    if not (String.IsNullOrEmpty directory) then
                        changeDirectory directory

                    checkedSource <- true
                    session.EvalScript(Path.GetFileName sourcePath)
                else
                    changeDirectory request.WorkingDirectory
                    checkedSource <- true

                    try
                        session.EvalInteraction("#load @\"" + escapedSource + "\"", "stdin")
                    with :? FsiCompilationException ->
                        // dotnet fsi reports the synthetic #load location as stdin(0,1); FCS uses stdin(1,1).
                        errWriter.ReplaceInBuilder("stdin(1,1): error FS0078", "stdin(0,1): error FS0078")
                        reraise ()
            else
                changeDirectory request.WorkingDirectory
                checkedSource <- true
                session.EvalInteraction(request.Source + "\n()", sourceName, cancellationToken = cancellationToken)

        let compileDiagnostics () =
            if not checkedSource || (request.Mode = "file" && not sourceExists) then
                [||]
            else
                // The non-throwing APIs suppress the formatted diagnostics but still write
                // "Stopped due to error"; route that to a discard writer so the text printed by the
                // throwing pass stays byte-identical to 0001.
                let saved = { Out = outWriter; Error = errWriter; In = TextReader.Null }
                Ambient.set { Out = TextWriter.Null; Error = TextWriter.Null; In = TextReader.Null }

                let diagnostics =
                    try
                        if request.Mode = "file" then
                            snd (session.EvalScriptNonThrowing(Path.GetFileName sourcePath))
                        else
                            snd (session.EvalInteractionNonThrowing(request.Source + "\n()", sourceName))
                    finally
                        Ambient.set saved

                diagnostics |> Array.map scriptDiagnostic

        let typesBeforeRun = emittedTypeCount ()

        try
            run ()
            0, [||]
        with :? FsiCompilationException ->
            if claimed.ExitedProperty.Value.GetValue state :?> bool then
                let text = errWriter.SafeText()
                let markerIndex = text.IndexOf(claimed.ExitMarker, StringComparison.Ordinal)

                if markerIndex >= 0 then
                    errWriter.TruncateAt markerIndex

                claimed.ExitCodeProperty.Value.GetValue state :?> int, [||]
            else
                // A failed compilation emits no CLR types; every compiled interaction or script
                // (even a comment-only one) emits its types before executing, so the emitted-type
                // delta proves user IL never ran without sniffing the diagnostic text.
                let compiledAndEnteredRuntime = emittedTypeCount () > typesBeforeRun

                if
                    request.IncludeDiagnostics
                    && not compiledAndEnteredRuntime
                    && checkedSource
                    && not (request.Mode = "file" && not sourceExists)
                then
                    1, compileDiagnostics ()
                else
                    1, [||]

    let sessionTtlMs = int64 (max 0.0 options.SessionTtlMinutes * 60000.0)
    let namedGate = obj ()
    let namedSessions = Dictionary<string, NamedSessionEntry>()

    let getOrCreateNamed (sessionId: string) =
        lock namedGate (fun () ->
            match namedSessions.TryGetValue sessionId with
            | true, entry -> entry
            | _ ->
                let entry = NamedSessionEntry()
                namedSessions.[sessionId] <- entry
                entry)

    let rec acquireNamed (sessionId: string) =
        let entry = getOrCreateNamed sessionId
        entry.Gate.Wait()

        if entry.Evicted then
            entry.Gate.Release() |> ignore
            acquireNamed sessionId
        else
            entry

    let acquireNamedReset (sessionId: string) =
        let entry = acquireNamed sessionId

        match entry.Session with
        | Some warm -> releaseSession warm
        | None -> ()

        entry.Session <- None
        entry

    let sweepNamedSessions () =
        if sessionTtlMs > 0L then
            let snapshot =
                lock namedGate (fun () ->
                    [ for KeyValue(sessionId, entry) in namedSessions -> sessionId, entry ])

            for sessionId, entry in snapshot do
                let expired () =
                    not entry.Evicted
                    && Environment.TickCount64 - entry.LastUsedMs >= sessionTtlMs

                if expired () && entry.Gate.Wait 0 then
                    try
                        if expired () then
                            entry.Evicted <- true

                            lock namedGate (fun () ->
                                match namedSessions.TryGetValue sessionId with
                                | true, existing when Object.ReferenceEquals(existing, entry) ->
                                    namedSessions.Remove sessionId |> ignore
                                | _ -> ())

                            match entry.Session with
                            | Some warm -> releaseSession warm
                            | None -> ()

                            entry.Session <- None
                    finally
                        entry.Gate.Release() |> ignore

    let replenish () =
        while not stopped do
            replenishSignal.Wait 250 |> ignore
            drainCleanups ()

            let mutable working = true

            while working && not stopped do
                if pool.Count + pendingCreations < targetPoolSize then
                    pendingCreations <- pendingCreations + 1

                    let created =
                        try
                            try
                                creationGate.Wait()

                                try
                                    Some(createWarmSession ())
                                finally
                                    creationGate.Release() |> ignore
                            with ex ->
                                Log.error None ("replenisher could not create a warm session: " + ex.Message)
                                Thread.Sleep 200
                                None
                        finally
                            pendingCreations <- pendingCreations - 1

                    match created with
                    | Some warm -> pool.Enqueue warm
                    | None -> ()
                else
                    working <- false

    do
        Task.Factory.StartNew(replenish, TaskCreationOptions.LongRunning) |> ignore
        replenishSignal.Release() |> ignore

        if sessionTtlMs > 0L then
            let sweepIntervalMs = max 250 (min 5000 (int (sessionTtlMs / 4L)))

            Task.Factory.StartNew(
                (fun () ->
                    while true do
                        Thread.Sleep sweepIntervalMs
                        sweepNamedSessions ()),
                TaskCreationOptions.LongRunning
            )
            |> ignore

    member _.EnsureWarm() =
        if targetPoolSize > 0 then
            drainCleanups ()
            replenishSignal.Release() |> ignore
            let deadline = Environment.TickCount64 + 60000L
            let mutable filling = true

            while filling do
                filling <-
                    not stopped
                    && pool.Count + pendingCreations < targetPoolSize
                    && Environment.TickCount64 < deadline

                if filling then
                    Thread.Sleep 5

    member _.IsWarm = pool.Count > 0

    member _.ExecutedCount = executed.Value

    member _.Execute(request: ExecuteRequest, disconnectToken: CancellationToken) : ExecutionReport =
        let timeoutMs = request.TimeoutMs
        use runCts = new CancellationTokenSource()
        let timeoutFired = ref 0

        use timeoutTimer =
            if timeoutMs > 0 then
                new Timer(
                    (fun _ ->
                        Volatile.Write(&timeoutFired.contents, 1)

                        try
                            runCts.Cancel()
                        with _ ->
                            ()),
                    null,
                    timeoutMs,
                    Timeout.Infinite
                )
            else
                Unchecked.defaultof<Timer>

        use disconnectRegistration =
            disconnectToken.Register(fun () ->
                try
                    runCts.Cancel()
                with _ ->
                    ())

        let canceledSignal = new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        use canceledRegistration = runCts.Token.Register(fun () -> canceledSignal.TrySetResult() |> ignore)

        let outWriter = new RunTextWriter()
        let errWriter = new RunTextWriter()

        let runInSession (claimed: WarmSession) (stateless: bool) =
            try
                processState.Enter(request.WorkingDirectory, request.Environment)

                try
                    Ambient.set { Out = outWriter; Error = errWriter; In = TextReader.Null }

                    try
                        match claimed.CancellationTokenProperty, claimed.State with
                        | Some property, Some state -> property.SetValue(state, box runCts.Token)
                        | _ -> ()

                        try
                            let exitCode, diagnostics = evaluate claimed request outWriter errWriter runCts.Token
                            Interlocked.Increment(&executed.contents) |> ignore

                            { ExitCode = exitCode
                              StandardOut = outWriter.SafeText()
                              StandardError = errWriter.SafeText()
                              Diagnostics = diagnostics
                              TimedOut = false }
                        finally
                            match claimed.CancellationTokenProperty, claimed.State with
                            | Some property, Some state -> property.SetValue(state, box CancellationToken.None)
                            | _ -> ()
                    finally
                        Ambient.clear ()
                finally
                    processState.Exit ()
            finally
                replenishSignal.Release() |> ignore

                if stateless then
                    releaseSession claimed

        let canceledOutcome () =
            Interlocked.Increment(&executed.contents) |> ignore

            { ExitCode = 1
              StandardOut = outWriter.SafeText()
              StandardError = errWriter.SafeText()
              Diagnostics = [||]
              TimedOut = false }

        let work () =
            let outcome =
                match request.SessionId with
                | None ->
                    let claimed = claimSession ()

                    try
                        runInSession claimed true
                    with :? OperationCanceledException ->
                        canceledOutcome ()
                | Some sessionId ->
                    let entry =
                        if request.Reset then
                            acquireNamedReset sessionId
                        else
                            acquireNamed sessionId

                    try
                        if entry.Session.IsNone then
                            entry.Session <- Some(claimSession ())

                        let outcome =
                            try
                                runInSession entry.Session.Value false
                            with :? OperationCanceledException ->
                                canceledOutcome ()

                        entry.LastUsedMs <- Environment.TickCount64
                        outcome
                    finally
                        entry.Gate.Release() |> ignore

            // Classify at the worker's completion point: reading the flag here, before the
            // caller observes the task, makes the timeout outcome deterministic even if the
            // deadline and the completion race.
            if Volatile.Read(&timeoutFired.contents) = 1 then
                { outcome with
                    ExitCode = 124
                    TimedOut = true
                    StandardError = appendMessage outcome.StandardError (timeoutMessage timeoutMs) }
            else
                outcome

        let worker = Task.Factory.StartNew(work, TaskCreationOptions.LongRunning)
        let canceledFirst = Task.WaitAny(worker, canceledSignal.Task) = 1

        if not canceledFirst then
            { Outcome = worker.GetAwaiter().GetResult(); Wedged = false }
        else
            let completedInGrace =
                try
                    worker.Wait 150
                with :? AggregateException ->
                    true

            if completedInGrace then
                { Outcome = worker.GetAwaiter().GetResult(); Wedged = false }
            else
                let timedOut = Volatile.Read(&timeoutFired.contents) = 1

                let message =
                    if timedOut then
                        timeoutMessage timeoutMs
                    else
                        "fsion: run cancelled after the client disconnected" + Environment.NewLine

                { Outcome =
                    { ExitCode = 124
                      StandardOut = outWriter.SafeText()
                      StandardError = appendMessage (errWriter.SafeText()) message
                      Diagnostics = [||]
                      TimedOut = timedOut }
                  Wedged = true }

    member _.Metrics() : MetricsInfo =
        drainCleanupsBlocking ()
        sweepFinalizers ()

        let scriptAssemblyCount = scriptAssemblies () |> Array.length
        let aliveDisposedSessions = drainReleasedSessions ()

        { PoolTarget = targetPoolSize
          PoolWarm = pool.Count
          ColdClaims = coldClaims.Value
          Executed = executed.Value
          ManagedHeapBytes = GC.GetTotalMemory true
          ScriptAssemblies = scriptAssemblyCount
          AliveDisposedSessions = aliveDisposedSessions
          NamedSessions = lock namedGate (fun () -> namedSessions.Count) }
