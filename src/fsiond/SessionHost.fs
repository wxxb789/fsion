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

[<NoComparison>]
type SessionHostOptions =
    { PoolSize: int
      SessionTtlMinutes: float
      Prelude: Prelude.Spec }

type private RunTextWriter() =
    inherit TextWriter()

    let inner = new StringWriter()
    let gate = obj()

    member _.SafeText() = lock gate (fun () -> inner.ToString())

    member _.TruncateAt(index: int) = lock gate (fun () -> inner.GetStringBuilder().Length <- index)

    member _.ReplaceInBuilder(oldValue: string, newValue: string) =
        lock gate (fun () -> inner.GetStringBuilder().Replace(oldValue, newValue) |> ignore)

    member _.ReplaceDiagnosticSource(sourcePath: string, fileName: string) =
        lock gate (fun () ->
            let builder = inner.GetStringBuilder()
            let oldPrefix = sourcePath + "("
            let newPrefix = fileName + "("

            let isLocationHeader (startAfterParen: int) =
                let mutable pos = startAfterParen
                let len = builder.Length

                while pos < len && Char.IsDigit builder.[pos] do
                    pos <- pos + 1

                if pos > startAfterParen && pos < len && builder.[pos] = ',' then
                    let colStart = pos + 1
                    pos <- colStart

                    while pos < len && Char.IsDigit builder.[pos] do
                        pos <- pos + 1

                    pos > colStart
                    && pos + 3 <= len
                    && builder.[pos] = ')'
                    && builder.[pos + 1] = ':'
                    && builder.[pos + 2] = ' '
                else
                    false

            let mutable index = 0

            while index >= 0 && index <= builder.Length - oldPrefix.Length do
                let found = builder.ToString().IndexOf(oldPrefix, index, StringComparison.Ordinal)

                if found < 0 then
                    index <- -1
                elif
                    (found = 0 || builder.[found - 1] = '\n' || builder.[found - 1] = '\r')
                    && isLocationHeader (found + oldPrefix.Length)
                then
                    builder.Remove(found, sourcePath.Length).Insert(found, fileName) |> ignore
                    index <- found + newPrefix.Length
                else
                    index <- found + oldPrefix.Length)

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

    member val ShowDeclarationValues = false
    member val ShowIEnumerable = false
    member val ShowProperties = false
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
    member val Prelude: Prelude.Tag = Prelude.Tag.Builtin with get, set
    member val CompilerDirectory: string = "" with get, set
    member val ProcessExitHandlers: Delegate[] = [||] with get, set
    member val UnmanagedDllHandlers: Delegate[] = [||] with get, set
    member val AssemblyResolveHandlers: Delegate[] = [||] with get, set

type private PreparedSource =
    { SourceName: string
      SourcePath: string
      SourceExists: bool
      ScriptDirectory: string
      LoadedText: string option
      Rewritten: (string * string) option }

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

let private mayMutateCompilerDirectory (text: string) =
    text.IndexOf("#cd", StringComparison.Ordinal) >= 0
    || text.IndexOf("#silentCd", StringComparison.Ordinal) >= 0
    || text.IndexOf("#load", StringComparison.Ordinal) >= 0

type private InteractionScan =
    | InteractionCode
    | InteractionLineComment
    | InteractionBlockComment of int
    | InteractionNormalString
    | InteractionVerbatimString
    | InteractionTripleString

// EvalInteraction stops at the first top-level `;;` terminator, whereas `dotnet fsi` executes every
// `;;`-delimited chunk in a script file. Split on top-level `;;` outside strings/comments and pad
// subsequent chunks with leading `\n` equal to their starting line offset so diagnostic line
// numbers and column offsets remain exact.
let private firstLineMinIndent (text: string) =
    let mutable idx = 0
    let mutable col = 0
    let mutable inIndent = true
    let mutable minCol = Int32.MaxValue

    while idx < text.Length do
        let ch = text.[idx]

        if ch = '\n' then
            col <- 0
            inIndent <- true
            idx <- idx + 1
        elif ch = '\r' then
            idx <- idx + 1
        elif inIndent && ch = ' ' then
            col <- col + 1
            idx <- idx + 1
        elif inIndent && ch = '\t' then
            col <- col + 4
            idx <- idx + 1
        elif inIndent then
            if col < minCol then
                minCol <- col

            inIndent <- false
            idx <- idx + 1
        else
            idx <- idx + 1

    if minCol = Int32.MaxValue then 0 else minCol

let private appendTrailingUnit (padded: string) =
    if padded.TrimEnd().EndsWith("=", StringComparison.Ordinal) then
        padded
    else
        let unitIndent = firstLineMinIndent padded
        padded + "\n" + (if unitIndent > 0 then String(' ', unitIndent) else "") + "()"

let private splitInteractions (source: string) : string[] =
    if source.IndexOf(";;", StringComparison.Ordinal) < 0 then
        [| appendTrailingUnit source |]
    else
        let chunks = ResizeArray<string>()
        let length = source.Length
        let mutable state = InteractionCode
        let mutable i = 0
        let mutable lineCount = 0
        let mutable lineStart = 0
        let mutable chunkStart = 0
        let mutable chunkStartLines = 0
        let mutable chunkStartCols = 0

        let emitChunk endExclusive nextStart nextLines nextCols =
            let body = source.Substring(chunkStart, endExclusive - chunkStart)

            if chunks.Count = 0 || body.Trim().Length > 0 then
                let linePrefix = if chunkStartLines > 0 then String('\n', chunkStartLines) else ""

                let hasCodeOnFirstLine =
                    let nl = body.IndexOf '\n'
                    let firstLine = if nl >= 0 then body.Substring(0, nl) else body
                    firstLine.Trim().Length > 0

                let colPrefix =
                    if chunkStartCols > 0 && hasCodeOnFirstLine then
                        String(' ', chunkStartCols)
                    else
                        ""

                chunks.Add(appendTrailingUnit (linePrefix + colPrefix + body))

            chunkStart <- nextStart
            chunkStartLines <- nextLines
            chunkStartCols <- nextCols

        let advanceNewline nextIndex =
            lineCount <- lineCount + 1
            lineStart <- nextIndex
            i <- nextIndex

        while i < length do
            let c = source.[i]

            match state with
            | InteractionCode ->
                if c = '\n' then
                    advanceNewline (i + 1)
                elif c = '/' && i + 1 < length && source.[i + 1] = '/' then
                    state <- InteractionLineComment
                    i <- i + 2
                elif c = '(' && i + 1 < length && source.[i + 1] = '*' then
                    state <- InteractionBlockComment 1
                    i <- i + 2
                elif c = '"' then
                    if i + 2 < length && source.[i + 1] = '"' && source.[i + 2] = '"' then
                        state <- InteractionTripleString
                        i <- i + 3
                    else
                        state <- InteractionNormalString
                        i <- i + 1
                elif c = '@' && i + 1 < length && source.[i + 1] = '"' then
                    state <- InteractionVerbatimString
                    i <- i + 2
                elif c = '\'' && i + 2 < length && source.[i + 1] <> '\\' && source.[i + 2] = '\'' then
                    i <- i + 3
                elif c = '\'' && i + 3 < length && source.[i + 1] = '\\' && source.[i + 3] = '\'' then
                    i <- i + 4
                elif c = ';' && i + 1 < length && source.[i + 1] = ';' then
                    let nextIndex = i + 2
                    emitChunk i nextIndex lineCount (nextIndex - lineStart)
                    i <- nextIndex
                else
                    i <- i + 1
            | InteractionLineComment ->
                if c = '\n' then
                    state <- InteractionCode
                    advanceNewline (i + 1)
                else
                    i <- i + 1
            | InteractionBlockComment depth ->
                if c = '\n' then
                    advanceNewline (i + 1)
                elif c = '(' && i + 1 < length && source.[i + 1] = '*' then
                    state <- InteractionBlockComment(depth + 1)
                    i <- i + 2
                elif c = '*' && i + 1 < length && source.[i + 1] = ')' then
                    state <- if depth = 1 then InteractionCode else InteractionBlockComment(depth - 1)
                    i <- i + 2
                else
                    i <- i + 1
            | InteractionNormalString ->
                if c = '\\' && i + 1 < length then
                    if source.[i + 1] = '\n' then
                        advanceNewline (i + 2)
                    else
                        i <- i + 2
                elif c = '"' then
                    state <- InteractionCode
                    i <- i + 1
                elif c = '\n' then
                    state <- InteractionCode
                    advanceNewline (i + 1)
                else
                    i <- i + 1
            | InteractionVerbatimString ->
                if c = '"' then
                    if i + 1 < length && source.[i + 1] = '"' then
                        i <- i + 2
                    else
                        state <- InteractionCode
                        i <- i + 1
                elif c = '\n' then
                    advanceNewline (i + 1)
                else
                    i <- i + 1
            | InteractionTripleString ->
                if c = '"' && i + 2 < length && source.[i + 1] = '"' && source.[i + 2] = '"' then
                    state <- InteractionCode
                    i <- i + 3
                elif c = '\n' then
                    advanceNewline (i + 1)
                else
                    i <- i + 1

        if chunkStart < length || chunks.Count = 0 then
            emitChunk length length lineCount (chunkStart - lineStart)

        chunks.ToArray()

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

let private builtinPrelude =
    """
module Fsion =
    let private warmAssemblies =
        [ typeof<System.IO.File>
          typeof<System.Text.StringBuilder>
          typeof<System.Text.Json.JsonDocument>
          typeof<System.Text.RegularExpressions.Regex>
          typeof<System.Net.Http.HttpClient>
          typeof<System.Linq.Enumerable> ]
        |> List.map (fun warmType -> warmType.Assembly.GetName().Name)

    let private warmTypicalJsonPipeline =
        let root = (System.Text.Json.JsonDocument.Parse "[{\"a\":1,\"b\":\"x\",\"ok\":true}]").RootElement
        root.EnumerateArray()
        |> Seq.filter (fun item -> item.GetProperty("ok").GetBoolean())
        |> Seq.map (fun item -> item.GetProperty("b").GetString(), item.GetProperty("a").GetInt32())
        |> Seq.sortBy (fun (label, score) -> -score, label)
        |> Seq.toArray
        |> Array.length

    let cwd () : string = System.IO.Directory.GetCurrentDirectory()
    let path (rel: string) : string = System.IO.Path.GetFullPath rel
    let env (name: string) : string option =
        match System.Environment.GetEnvironmentVariable name with
        | null -> None
        | value -> Some value
    let envOr (name: string) (fallback: string) : string =
        match System.Environment.GetEnvironmentVariable name with
        | null -> fallback
        | value -> value
    let readText (path: string) : string = System.IO.File.ReadAllText path
    let writeText (path: string) (content: string) : unit = System.IO.File.WriteAllText(path, content)
    let json (text: string) : System.Text.Json.JsonDocument = System.Text.Json.JsonDocument.Parse text
"""

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

    let evaluateUserPrelude (session: FsiEvaluationSession) (path: string) : Prelude.Tag * string =
        match Prelude.readAndHashFile path with
        | None -> failwith ("configured prelude file could not be read: " + path)
        | Some (text, contentHash) ->
            let directory =
                match Path.GetDirectoryName path with
                | null | "" -> AppContext.BaseDirectory
                | directory -> directory

            session.EvalInteraction("#cd @\"" + Prelude.escapeVerbatim directory + "\"")
            match NugetCache.prepare text directory path with
            | Some plan ->
                session.EvalInteraction(NugetCache.referenceBlock plan, "fsion-prelude-refs.fsx")
                session.EvalInteraction(plan.RewrittenSource + "\n()", path)
            | None -> session.EvalInteraction(text + "\n()", path)

            let trackedDirectory =
                if mayMutateCompilerDirectory text then "" else directory

            Prelude.Tag.File(path, contentHash), trackedDirectory
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

    let createWarmSession (spec: Prelude.Spec) : WarmSession =
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
        let warm = WarmSession()
        warm.Session <- Some session
        warm.ProcessExitHandlers <- processExitHandlers
        warm.UnmanagedDllHandlers <- unmanagedDllHandlers
        warm.AssemblyResolveHandlers <- assemblyResolveHandlers
        let mutable succeeded = false

        try
            let exitMarker = "@@FSION-EXIT-" + Guid.NewGuid().ToString("N") + "@@"
            let creationOut = new StringWriter()
            let creationErr = new StringWriter()
            Ambient.set { Out = creationOut; Error = creationErr; In = TextReader.Null }
            let mutable preludeTag = Prelude.Tag.Builtin
            let mutable compilerDirectory = ""

            try
                try
                    match spec with
                    | Prelude.Spec.Bare ->
                        session.EvalInteraction(prelude exitMarker)
                        preludeTag <- Prelude.Tag.Bare
                    | Prelude.Spec.Builtin ->
                        session.EvalInteraction(prelude exitMarker + builtinPrelude)
                        preludeTag <- Prelude.Tag.Builtin
                    | Prelude.Spec.File path ->
                        session.EvalInteraction(prelude exitMarker + builtinPrelude)
                        let fileTag, fileDirectory = evaluateUserPrelude session path
                        preludeTag <- fileTag
                        compilerDirectory <- fileDirectory
                with :? FsiCompilationException ->
                    let diagnostics = creationErr.ToString()

                    if diagnostics.Trim().Length = 0 then
                        reraise ()
                    else
                        failwith ("configured prelude did not compile:" + Environment.NewLine + diagnostics)
            finally
                Ambient.clear ()

            let state =
                match session.TryFindBoundValue "fsionFsiState" with
                | Some bound -> bound.Value.ReflectionValue
                | None -> failwith "fsion prelude did not expose fsionFsiState"

            let stateType = state.GetType()
            clearCompilerAsyncLocals ()
            warm.State <- Some state
            warm.CommandLineArgsProperty <- Some(stateType.GetProperty "CommandLineArgs")
            warm.ExitedProperty <- Some(stateType.GetProperty "Exited")
            warm.ExitCodeProperty <- Some(stateType.GetProperty "ExitCode")
            warm.CancellationTokenProperty <- Some(stateType.GetProperty "CancellationToken")
            warm.ExitMarker <- exitMarker
            warm.Prelude <- preludeTag
            warm.CompilerDirectory <- compilerDirectory
            succeeded <- true
            warm
        finally
            if not succeeded then
                releaseSession warm
                replenishSignal.Release() |> ignore
    let desiredGate = obj ()
    let mutable desiredSpec = options.Prelude
    let mutable failedTag: Prelude.Tag option = None
    let mutable failedError: string option = None

    let rec claimSession (tag: Prelude.Tag) (cancelToken: CancellationToken) : WarmSession =
        match pool.TryDequeue() with
        | true, warm when warm.Prelude = tag -> warm
        | true, warm ->
            releaseSession warm
            claimSession tag cancelToken
        | _ ->
            Interlocked.Increment(&coldClaims.contents) |> ignore
            creationGate.Wait(cancelToken)

            let created =
                try
                    createWarmSession (Prelude.specOfTag tag)
                finally
                    creationGate.Release() |> ignore

            let cleared =
                lock desiredGate (fun () ->
                    if failedTag.IsSome then
                        failedTag <- None
                        failedError <- None
                        true
                    else
                        false)

            if cleared then
                replenishSignal.Release() |> ignore

            created

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
    // creation. The replenisher drains this queue after refilling the warm pool (it runs with
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

    let stripLeadingBom (text: string) =
        if text.Length > 0 && text.[0] = '\uFEFF' then text.Substring 1 else text

    let readUtf8File (path: string) =
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
        use reader = new StreamReader(stream, Encoding.UTF8, true)
        reader.ReadToEnd()

    let prepareExecution (request: ExecuteRequest) : PreparedSource =
        let sourceName = if request.Mode = "stdin" then "stdin.fsx" else "eval.fsx"

        let sourcePath =
            if Path.IsPathRooted request.Source then
                request.Source
            else
                Path.Combine(request.WorkingDirectory, request.Source)

        let sourceExists = request.Mode = "file" && File.Exists sourcePath

        let scriptDirectory =
            if request.Mode = "file" && sourceExists then
                match Path.GetDirectoryName sourcePath with
                | null | "" -> request.WorkingDirectory
                | directory -> directory
            else
                request.WorkingDirectory

        let loadedText =
            if request.Mode = "file" then
                if sourceExists then Some(readUtf8File sourcePath) else None
            else
                Some(stripLeadingBom request.Source)

        let containsNuget (text: string) = text.IndexOf("nuget:", StringComparison.OrdinalIgnoreCase) >= 0

        let rewritten =
            match loadedText with
            | Some text when containsNuget text ->
                let directiveSource =
                    if request.Mode = "file" && sourceExists then sourcePath else sourceName

                NugetCache.prepare text scriptDirectory directiveSource
                |> Option.map (fun plan -> NugetCache.referenceBlock plan, plan.RewrittenSource)
            | _ -> None

        { SourceName = sourceName
          SourcePath = sourcePath
          SourceExists = sourceExists
          ScriptDirectory = scriptDirectory
          LoadedText = loadedText
          Rewritten = rewritten }
    let evaluate
        (claimed: WarmSession)
        (request: ExecuteRequest)
        (prepared: PreparedSource)
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
            if not (String.Equals(claimed.CompilerDirectory, directory, StringComparison.Ordinal)) then
                session.EvalInteraction("#cd @\"" + Prelude.escapeVerbatim directory + "\"")
                claimed.CompilerDirectory <- directory

        let escapedSource = request.Source.Replace("\"", "\"\"")
        let mutable checkedSource = false

        let emittedTypeCount () =
            session.DynamicAssemblies
            |> Array.sumBy (fun assembly ->
                try
                    assembly.GetTypes().Length
                with _ ->
                    0)

        let checkedSourceName =
            if request.Mode = "file" && prepared.SourceExists then
                prepared.SourcePath
            else
                prepared.SourceName

        let mutable attemptedCode = ""
        let mutable attemptedScriptName = prepared.SourceName
        let mutable typesBeforeCurrentEval = 0
        let mutable precheckedDiagnostics: ScriptDiagnostic[] option = None

        let evalCode (code: string) (scriptName: string) =
            let chunks = splitInteractions code

            try
                for chunk in chunks do
                    checkedSource <- true
                    attemptedCode <- chunk
                    attemptedScriptName <- scriptName
                    typesBeforeCurrentEval <- emittedTypeCount ()
                    precheckedDiagnostics <- None

                    if request.IncludeDiagnostics && chunk.IndexOf("#load", StringComparison.Ordinal) >= 0 then
                        let checkResults =
                            let saved = { Out = outWriter; Error = errWriter; In = TextReader.Null }
                            Ambient.set { Out = TextWriter.Null; Error = TextWriter.Null; In = TextReader.Null }

                            try
                                let _, _, check = session.ParseAndCheckInteraction chunk
                                check
                            finally
                                Ambient.set saved

                        let hasCompileErrors =
                            checkResults.Diagnostics
                            |> Array.exists (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)

                        if hasCompileErrors then
                            precheckedDiagnostics <-
                                Some(
                                    checkResults.Diagnostics
                                    |> Array.map (fun diagnostic ->
                                        let mapped = scriptDiagnostic diagnostic

                                        if mapped.File.EndsWith("stdin.fsx", StringComparison.OrdinalIgnoreCase) then
                                            { mapped with File = scriptName }
                                        else
                                            mapped)
                                )

                            session.EvalInteraction(chunk, scriptName, cancellationToken = cancellationToken)
                        else
                            typesBeforeCurrentEval <- Int32.MinValue
                            session.EvalInteraction(chunk, scriptName, cancellationToken = cancellationToken)
                    else
                        session.EvalInteraction(chunk, scriptName, cancellationToken = cancellationToken)
            finally
                if mayMutateCompilerDirectory code then
                    claimed.CompilerDirectory <- ""

                if OperatingSystem.IsWindows() && request.Mode = "file" && prepared.SourceExists then
                    let fileName = Path.GetFileName prepared.SourcePath

                    if not (String.Equals(prepared.SourcePath, fileName, StringComparison.Ordinal)) then
                        errWriter.ReplaceDiagnosticSource(prepared.SourcePath, fileName)
        let run () =
            match prepared.Rewritten with
            | Some (referenceBlock, rewrittenSource) ->
                changeDirectory prepared.ScriptDirectory
                session.EvalInteraction(referenceBlock, "fsion-nuget-refs.fsx")
                evalCode rewrittenSource checkedSourceName
            | None ->
                if request.Mode = "file" then
                    if prepared.SourceExists then
                        changeDirectory prepared.ScriptDirectory
                        evalCode (defaultArg prepared.LoadedText "") checkedSourceName
                    else
                        changeDirectory request.WorkingDirectory
                        checkedSource <- true

                        try
                            try
                                session.EvalInteraction("#load @\"" + escapedSource + "\"", "stdin")
                            with :? FsiCompilationException ->
                                // dotnet fsi reports the synthetic #load location as stdin(0,1); FCS uses stdin(1,1).
                                errWriter.ReplaceInBuilder("stdin(1,1): error FS0078", "stdin(0,1): error FS0078")
                                reraise ()
                        finally
                            // The loaded file is not read here, so a `#cd` inside it cannot be
                            // detected; invalidate the tracked directory so the next turn re-applies
                            // `#cd` if the load changed FCS's compiler directory.
                            claimed.CompilerDirectory <- ""
                else
                    changeDirectory request.WorkingDirectory
                    evalCode (defaultArg prepared.LoadedText "") prepared.SourceName
        let compileDiagnostics () =
            if not checkedSource || (request.Mode = "file" && not prepared.SourceExists) then
                [||]
            else
                match precheckedDiagnostics with
                | Some diagnostics -> diagnostics
                | None ->
                    changeDirectory (
                        if request.Mode = "file" && prepared.SourceExists then
                            prepared.ScriptDirectory
                        else
                            request.WorkingDirectory
                    )

                    // The non-throwing APIs suppress the formatted diagnostics but still write
                    // "Stopped due to error"; route that to a discard writer so the text printed by the
                    // throwing pass stays byte-identical to 0001.
                    let saved = { Out = outWriter; Error = errWriter; In = TextReader.Null }
                    Ambient.set { Out = TextWriter.Null; Error = TextWriter.Null; In = TextReader.Null }

                    let diagnostics =
                        try
                            snd (session.EvalInteractionNonThrowing(attemptedCode, attemptedScriptName))
                        finally
                            Ambient.set saved

                    diagnostics |> Array.map scriptDiagnostic

        try
            run ()

            if checkedSource && emittedTypeCount () = typesBeforeCurrentEval && errWriter.SafeText().Length > 0 then
                let diagnostics = if request.IncludeDiagnostics then compileDiagnostics () else [||]
                1, diagnostics
            else
                0, [||]
        with :? FsiCompilationException ->
            if claimed.ExitedProperty.Value.GetValue state :?> bool then
                let text = errWriter.SafeText()
                let markerIndex = text.IndexOf(claimed.ExitMarker, StringComparison.Ordinal)

                if markerIndex >= 0 then
                    errWriter.TruncateAt markerIndex

                claimed.ExitCodeProperty.Value.GetValue state :?> int, [||]
            else
                // A failed compilation emits no CLR types for the failing interaction (and `#load`
                // interactions pre-checked clean set `typesBeforeCurrentEval = Int32.MinValue`), so
                // `emittedTypeCount () > typesBeforeCurrentEval` proves user IL ran without sniffing text.
                let compiledAndEnteredRuntime =
                    match precheckedDiagnostics with
                    | Some _ -> false
                    | None -> typesBeforeCurrentEval = Int32.MinValue || emittedTypeCount () > typesBeforeCurrentEval
                if
                    request.IncludeDiagnostics
                    && not compiledAndEnteredRuntime
                    && checkedSource
                    && not (request.Mode = "file" && not prepared.SourceExists)
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

    let rec sweepNamedSessions () =
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

    let setDesiredSpec (spec: Prelude.Spec) =
        let changed =
            lock desiredGate (fun () ->
                if desiredSpec = spec then
                    false
                else
                    desiredSpec <- spec
                    failedTag <- None
                    failedError <- None
                    true)

        if changed then
            replenishSignal.Release() |> ignore

    let recycleStalePoolSessions () =
        let current = lock desiredGate (fun () -> Prelude.tagOfSpec desiredSpec)
        let retained = ResizeArray<WarmSession>()
        let mutable warm = Unchecked.defaultof<WarmSession>

        while pool.TryDequeue(&warm) do
            if warm.Prelude = current then
                retained.Add warm
            else
                try
                    releaseSession warm
                with ex ->
                    Log.error None ("replenisher failed to release a stale warm session: " + ex.Message)

        for warm in retained do
            pool.Enqueue warm

    let replenish () =
        while not stopped do
            replenishSignal.Wait 250 |> ignore
            recycleStalePoolSessions ()

            let mutable working = true

            while working && not stopped do
                let spec, currentTag, skipBroken =
                    lock desiredGate (fun () ->
                        let tag = Prelude.tagOfSpec desiredSpec
                        desiredSpec, tag, failedTag = Some tag)

                if not skipBroken && pool.Count + pendingCreations < targetPoolSize then
                    pendingCreations <- pendingCreations + 1

                    let created =
                        try
                            try
                                creationGate.Wait()

                                try
                                    let warm = createWarmSession spec

                                    lock desiredGate (fun () ->
                                        if failedTag = Some currentTag then
                                            failedTag <- None
                                            failedError <- None)

                                    Some warm
                                finally
                                    creationGate.Release() |> ignore
                            with ex ->
                                Log.error None ("replenisher could not create a warm session: " + ex.Message)

                                lock desiredGate (fun () ->
                                    failedTag <- Some currentTag
                                    failedError <- Some ex.Message)

                                None
                        finally
                            pendingCreations <- pendingCreations - 1

                    match created with
                    | Some warm -> pool.Enqueue warm
                    | None -> working <- false
                else
                    working <- false

            if not pendingCleanups.IsEmpty && (pendingCleanups.Count >= 4 || pool.Count >= targetPoolSize || (lock desiredGate (fun () -> failedTag.IsSome))) then
                try
                    drainCleanups ()
                with ex ->
                    Log.error None ("replenisher failed to drain cleanups: " + ex.Message)
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

    member _.DesiredSpec = lock desiredGate (fun () -> desiredSpec)

    member _.EnsureWarm(spec: Prelude.Spec) =
        lock desiredGate (fun () ->
            desiredSpec <- spec
            failedTag <- None
            failedError <- None)

        if targetPoolSize > 0 then
            recycleStalePoolSessions ()
            drainCleanups ()
            replenishSignal.Release() |> ignore
            let deadline = Environment.TickCount64 + 60000L
            let mutable filling = true

            while filling do
                let broken =
                    lock desiredGate (fun () -> failedTag = Some(Prelude.tagOfSpec desiredSpec))

                filling <-
                    not stopped
                    && not broken
                    && pool.Count < targetPoolSize
                    && Environment.TickCount64 < deadline
                if filling then
                    Thread.Sleep 5

            let failure =
                lock desiredGate (fun () ->
                    if failedTag = Some(Prelude.tagOfSpec desiredSpec) then
                        Some(defaultArg failedError "configured prelude failed during warmup")
                    else
                        None)

            match failure with
            | Some message -> failwith message
            | None -> ()
        else
            match spec with
            | Prelude.Spec.File _ ->
                creationGate.Wait()

                let probe =
                    try
                        createWarmSession spec
                    finally
                        creationGate.Release() |> ignore

                releaseSession probe
                replenishSignal.Release() |> ignore
            | _ -> ()
    member _.IsWarm = pool.Count > 0

    member _.ExecutedCount = executed.Value

    member _.Execute(request: ExecuteRequest, disconnectToken: CancellationToken) : ExecutionReport =
        let timeoutMs = request.TimeoutMs
        use runCts = new CancellationTokenSource()
        let timeoutFired = ref 0
        let evaluationStarted = ref 0

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

        // --timeout is a script execution deadline only. Session acquisition (cold creation, a busy
        // named session's gate, the process-state transition) is not charged against it, and a
        // queued turn whose deadline expires before evaluation aborts in place without draining.
        let gateDeadline =
            if timeoutMs > 0 then Environment.TickCount64 + int64 timeoutMs else Int64.MaxValue

        let remainingGateMs () =
            if timeoutMs > 0 then
                max 0 (int (min (int64 Int32.MaxValue) (gateDeadline - Environment.TickCount64)))
            else
                Timeout.Infinite

        let rec acquireNamed (sessionId: string) =
            let entry = getOrCreateNamed sessionId

            if not (entry.Gate.Wait(remainingGateMs (), disconnectToken)) then
                None
            elif entry.Evicted then
                entry.Gate.Release() |> ignore
                acquireNamed sessionId
            else
                Some entry

        let acquireNamedReset (sessionId: string) =
            match acquireNamed sessionId with
            | None -> None
            | Some entry ->
                match entry.Session with
                | Some warm -> releaseSession warm
                | None -> ()

                entry.Session <- None
                Some entry

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
                            let prepared = prepareExecution request
                            disconnectToken.ThrowIfCancellationRequested()
                            Volatile.Write(&evaluationStarted.contents, 1)

                            try
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

                                let exitCode, diagnostics =
                                    evaluate claimed request prepared outWriter errWriter runCts.Token

                                Interlocked.Increment(&executed.contents) |> ignore

                                { ExitCode = exitCode
                                  StandardOut = outWriter.SafeText()
                                  StandardError = errWriter.SafeText()
                                  Diagnostics = diagnostics
                                  TimedOut = false }
                            finally
                                Volatile.Write(&evaluationStarted.contents, 0)
                        finally
                            match claimed.CancellationTokenProperty, claimed.State with
                            | Some property, Some state -> property.SetValue(state, box CancellationToken.None)
                            | _ -> ()
                    finally
                        Ambient.clear ()
                finally
                    processState.Exit ()
            finally
                if stateless then
                    releaseSession claimed

                replenishSignal.Release() |> ignore

        let canceledOutcome () =
            Interlocked.Increment(&executed.contents) |> ignore

            { ExitCode = 1
              StandardOut = outWriter.SafeText()
              StandardError = errWriter.SafeText()
              Diagnostics = [||]
              TimedOut = false }

        let work () =
            let targetSpec = Prelude.specOfRequest request
            setDesiredSpec targetSpec
            let expectedTag = Prelude.tagOfSpec targetSpec

            let outcome =
                try
                    match request.SessionId with
                    | None ->
                        let claimed = claimSession expectedTag disconnectToken

                        try
                            runInSession claimed true
                        with :? OperationCanceledException ->
                            canceledOutcome ()
                    | Some sessionId ->
                        let acquired =
                            if request.Reset then
                                acquireNamedReset sessionId
                            else
                                acquireNamed sessionId

                        match acquired with
                        | None ->
                            // The queued turn spent its whole --timeout budget on the gate: report
                            // the timeout in place while the daemon stays healthy.
                            { ExitCode = TimeoutExitCode
                              StandardOut = ""
                              StandardError = timeoutMessage timeoutMs
                              Diagnostics = [||]
                              TimedOut = true }
                        | Some entry ->
                            try
                                match entry.Session with
                                | Some warm when warm.Prelude <> expectedTag ->
                                    releaseSession warm
                                    entry.Session <- None
                                | _ -> ()

                                if entry.Session.IsNone then
                                    entry.Session <- Some(claimSession expectedTag disconnectToken)

                                let outcome =
                                    try
                                        runInSession entry.Session.Value false
                                    with :? OperationCanceledException ->
                                        canceledOutcome ()

                                entry.LastUsedMs <- Environment.TickCount64
                                outcome
                            finally
                                entry.Gate.Release() |> ignore
                with :? OperationCanceledException ->
                    canceledOutcome ()

            // Classify at the worker's completion point: reading the flag here, before the
            // caller observes the task, makes the timeout outcome deterministic even if the
            // deadline and the completion race.
            if Volatile.Read(&timeoutFired.contents) = 1 then
                { outcome with
                    ExitCode = TimeoutExitCode
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
                    worker.Wait 1500
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

                if Volatile.Read(&evaluationStarted.contents) = 1 then
                    // Only a run that entered evaluation can wedge a session. Acquisition waits
                    // always return here, so they never drain a healthy daemon.
                    { Outcome =
                        { ExitCode = TimeoutExitCode
                          StandardOut = outWriter.SafeText()
                          StandardError = appendMessage (errWriter.SafeText()) message
                          Diagnostics = [||]
                          TimedOut = timedOut }
                      Wedged = true }
                else
                    { Outcome =
                        { ExitCode = (if timedOut then TimeoutExitCode else 1)
                          StandardOut = outWriter.SafeText()
                          StandardError = appendMessage (errWriter.SafeText()) message
                          Diagnostics = [||]
                          TimedOut = timedOut }
                      Wedged = false }

    member _.Metrics() : MetricsInfo =
        if targetPoolSize > 0 then
            let shouldWait () =
                let broken =
                    lock desiredGate (fun () -> failedTag = Some(Prelude.tagOfSpec desiredSpec))

                not stopped && not broken && pool.Count < targetPoolSize

            if shouldWait () then
                replenishSignal.Release() |> ignore
                let deadline = Environment.TickCount64 + 30000L

                while shouldWait () && Environment.TickCount64 < deadline do
                    Thread.Sleep 10

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
