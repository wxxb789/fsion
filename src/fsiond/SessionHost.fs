module Fsiond.SessionHost

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open FSharp.Compiler.Tokenization
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
      Oneshot: bool
      LogPath: string option
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
    member val SetCompilerDirectory: (string -> unit) option = None with get, set
    member val PreludeTypes = HashSet<Type>() with get, set
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

let private fxClearStaticCaches =
    match typeof<FsiEvaluationSession>.Assembly.GetType("FSharp.Compiler.FxResolver") with
    | null -> null
    | resolverType ->
        resolverType.GetMethod(
            "ClearStaticCaches",
            BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic,
            Type.EmptyTypes
        )

let private clearCheckerCaches (session: FsiEvaluationSession) =
    try
        match checkerField.GetValue session with
        | null -> ()
        | checker ->
            match checker.GetType().GetMethod("InvalidateAll", Type.EmptyTypes) with
            | null -> ()
            | method -> method.Invoke(checker, [||]) |> ignore
    with _ ->
        ()

    if not (isNull fxClearStaticCaches) then
        try
            fxClearStaticCaches.Invoke(null, [||]) |> ignore
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
let private clearScriptStatics (preludeTypes: HashSet<Type>) (assemblies: Assembly[]) =
    for assembly in assemblies do
        try
            for scriptType in assembly.GetTypes() |> Array.filter (preludeTypes.Contains >> not) do
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
    GC.Collect(2, GCCollectionMode.Forced, true, false)
    GC.WaitForPendingFinalizers()
    GC.Collect(2, GCCollectionMode.Forced, true, false)

let private sweepTransientFinalizers collectionsBefore =
    // Any intervening collection, even Gen-0, can promote a stream opened by the script.
    let collect generation = GC.Collect(generation, GCCollectionMode.Forced, true, false)
    match collectionsBefore with
    | ValueSome(struct (gen0, gen1)) when GC.CollectionCount 1 = gen1 ->
        let collectGen1 () =
            collect 1
            if GC.CollectionCount 1 <> gen1 + 1 then collect 2

        if GC.CollectionCount 0 = gen0 then
            collect 0
            if GC.CollectionCount 0 <> gen0 + 1 then collectGen1 ()
        else
            collectGen1 ()
    | _ -> collect 2
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

// EvalInteraction stops at the first top-level `;;` terminator, whereas `dotnet fsi` executes every
// `;;`-delimited interaction in a script. Use FCS's own `FSharpSourceTokenizer` to find top-level
// `SEMICOLON_SEMICOLON` tokens outside strings/comments/backtick identifiers/inactive `#if` blocks
// and outside interpolated `{...}` holes, closing/reopening any active `#if` scopes across chunk
// boundaries and preserving exact diagnostic line and column numbers.
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

let private splitInteractions (source: string) (scriptName: string) : string[] =
    if
        source.IndexOf(";;", StringComparison.Ordinal) < 0
        && not (source.TrimStart().StartsWith(";", StringComparison.Ordinal))
    then
        [| appendTrailingUnit source |]
    else
        let chunks = ResizeArray<string>()
        let length = source.Length
        let lineStarts = ResizeArray<int>()
        let lineEnds = ResizeArray<int>()
        let mutable scanIdx = 0
        let mutable curStart = 0

        while scanIdx < length do
            if source.[scanIdx] = '\n' then
                lineStarts.Add curStart
                let lineEnd = if scanIdx > curStart && source.[scanIdx - 1] = '\r' then scanIdx - 1 else scanIdx
                lineEnds.Add lineEnd
                curStart <- scanIdx + 1

            scanIdx <- scanIdx + 1

        lineStarts.Add curStart
        let finalEnd = if length > curStart && source.[length - 1] = '\r' then length - 1 else length
        lineEnds.Add finalEnd

        let tokenizer = FSharpSourceTokenizer([ "INTERACTIVE" ], Some scriptName, None)
        let mutable lexState = FSharpTokenizerLexState.Initial
        let mutable hashIfDepth = 0
        let mutable interpDepth = 0
        let mutable chunkHasNonWs = false
        let mutable chunkStart = 0
        let mutable chunkStartLines = 0
        let mutable chunkStartCols = 0
        let mutable chunkOpenIfs = 0

        let emitChunk endExclusive nextStart nextLines nextCols closeIfs reopenIfs forceEmit =
            let body = source.Substring(chunkStart, endExclusive - chunkStart)

            if forceEmit || chunks.Count = 0 || body.Trim().Length > 0 || chunkOpenIfs > 0 then
                let hasCodeOnFirstLine =
                    let nl = body.IndexOf '\n'
                    let firstLine = if nl >= 0 then body.Substring(0, nl) else body
                    firstLine.Trim().Length > 0

                let colPrefix =
                    if chunkStartCols > 0 && hasCodeOnFirstLine then
                        String(' ', chunkStartCols)
                    else
                        ""

                let openLines =
                    if chunkOpenIfs > 0 then
                        String.replicate chunkOpenIfs "#if INTERACTIVE\n"
                    else
                        ""

                let padNewlines = max 0 (chunkStartLines - chunkOpenIfs)
                let prefix = openLines + (if padNewlines > 0 then String('\n', padNewlines) else "")
                let combined = prefix + colPrefix + body
                let withUnit = if forceEmit then combined else appendTrailingUnit combined
                let suffix = if closeIfs > 0 then String.replicate closeIfs "\n#endif" else ""
                chunks.Add(withUnit + suffix)
            chunkStart <- nextStart
            chunkStartLines <- nextLines
            chunkStartCols <- nextCols
            chunkOpenIfs <- reopenIfs
            chunkHasNonWs <- false

        for lineIdx = 0 to lineStarts.Count - 1 do
            let lStart = lineStarts.[lineIdx]
            let lEnd = lineEnds.[lineIdx]
            let lineText = source.Substring(lStart, lEnd - lStart)
            let lineTokenizer = tokenizer.CreateLineTokenizer lineText

            let rec scanLine state =
                match lineTokenizer.ScanToken state with
                | Some tok, nextState ->
                    match tok.TokenName with
                    | "WHITESPACE"
                    | "LINE_COMMENT"
                    | "COMMENT"
                    | "INACTIVECODE" -> ()
                    | "HASH_IF" ->
                        let slice = lineText.Substring tok.LeftColumn

                        if slice.StartsWith("#if", StringComparison.Ordinal) then
                            hashIfDepth <- hashIfDepth + 1
                        elif slice.StartsWith("#endif", StringComparison.Ordinal) then
                            hashIfDepth <- max 0 (hashIfDepth - 1)
                    | "INTERP_STRING_BEGIN_PART" ->
                        chunkHasNonWs <- true
                        interpDepth <- interpDepth + 1
                    | "INTERP_STRING_END" ->
                        interpDepth <- max 0 (interpDepth - 1)
                    | "SEMICOLON" when interpDepth = 0 && not chunkHasNonWs ->
                        let nextStart = lStart + tok.RightColumn + 1
                        emitChunk nextStart nextStart lineIdx (tok.RightColumn + 1) hashIfDepth hashIfDepth true
                    | "SEMICOLON_SEMICOLON" when interpDepth = 0 ->
                        let semiStart = lStart + tok.LeftColumn
                        let nextStart = lStart + tok.RightColumn + 1
                        emitChunk semiStart nextStart lineIdx (tok.RightColumn + 1) hashIfDepth hashIfDepth false
                    | _ ->
                        chunkHasNonWs <- true

                    scanLine nextState
                | None, nextState -> nextState

            lexState <- scanLine lexState

        if chunkStart < length || chunks.Count = 0 || chunkOpenIfs > 0 then
            let lastLine = lineStarts.Count - 1
            let tailCols = if chunkStart >= lineStarts.[lastLine] then chunkStart - lineStarts.[lastLine] else 0
            emitChunk length length lastLine tailCols 0 0 false

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
        use document = System.Text.Json.JsonDocument.Parse "[{\"a\":1,\"b\":\"x\",\"ok\":true}]"
        let root = document.RootElement
        root.EnumerateArray()
        |> Seq.filter (fun item -> item.GetProperty("ok").GetBoolean())
        |> Seq.map (fun item -> item.GetProperty("b").GetString(), item.GetProperty("a").GetInt32())
        |> Seq.sortBy (fun (label, score) -> -score, label)
        |> Seq.toArray
        |> Array.length

    type private WarmInput = { Number: int }
    type private WarmExpression = | Input of WarmInput | Constant of int
    let private warmCodegen =
        let value = match Input { Number = warmTypicalJsonPipeline } with Input row -> row.Number | Constant n -> n
        let text = System.Text.StringBuilder().Append(sprintf "%d" value).Append(' ').Append(0).ToString()
        try text.Split() |> Array.sumBy int with _ -> 0

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

let private environmentMap (snapshot: (string * string)[]) =
    let variables = Dictionary<string, string>(snapshot.Length, environmentComparer)
    for key, value in snapshot do
        variables.[key] <- value
    variables

let private readProcessEnvironment () =
    let snapshot = Environment.GetEnvironmentVariables()
    let variables = Dictionary<string, string>(snapshot.Count, environmentComparer)
    for entry in snapshot do
        let pair = unbox<System.Collections.DictionaryEntry> entry
        let key = unbox<string> pair.Key
        if key.Length > 0 && key.IndexOf '=' < 0 then
            variables.[key] <- unbox<string> pair.Value
    variables

let private applyEnvironment (current: Dictionary<string, string>) (desired: Dictionary<string, string>) =
    for key in current.Keys do
        if not (desired.ContainsKey key) then
            Environment.SetEnvironmentVariable(key, null, EnvironmentVariableTarget.Process)

    for KeyValue(key, value) in desired do
        match current.TryGetValue key with
        | true, existing when String.Equals(existing, value, StringComparison.Ordinal) -> ()
        | _ -> Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process)

type private ProcessStateGate() =
    let sync = obj ()
    let daemonDirectory = Directory.GetCurrentDirectory()
    let mutable activeRuns = 0
    let mutable activeFilePreludes = 0
    let mutable lastExit = Environment.TickCount64
    let mutable currentDirectory = daemonDirectory
    let daemonBaseline = readProcessEnvironment ()
    let mutable currentEnvironment = daemonBaseline

    let matchesEnvironment (environment: (string * string)[]) =
        currentEnvironment.Count = environment.Length
        && (environment |> Array.forall (fun (key, value) ->
            match currentEnvironment.TryGetValue key with
            | true, existing -> String.Equals(existing, value, StringComparison.Ordinal)
            | _ -> false))

    member _.Enter(directory: string, environment: (string * string)[]) =
        lock sync (fun () ->
            while activeFilePreludes > 0
                  || (activeRuns > 0 && (currentDirectory <> directory || not (matchesEnvironment environment))) do
                Monitor.Wait sync |> ignore

            if activeRuns = 0 then
                if not (String.Equals(Directory.GetCurrentDirectory(), directory, StringComparison.Ordinal)) then
                    Directory.SetCurrentDirectory directory
                currentDirectory <- directory
                if not (matchesEnvironment environment) then
                    currentEnvironment <- environmentMap environment

                // Script code can mutate process variables without updating the cached request map.
                applyEnvironment (readProcessEnvironment ()) currentEnvironment

            activeRuns <- activeRuns + 1)

    member _.Exit() =
        lock sync (fun () ->
            activeRuns <- activeRuns - 1
            lastExit <- Environment.TickCount64

            if activeRuns = 0 then
                try
                    Directory.SetCurrentDirectory daemonDirectory
                    currentDirectory <- daemonDirectory
                finally
                    Monitor.PulseAll sync)

    member _.EnterFilePreludeBlocking() =
        lock sync (fun () ->
            while activeRuns > 0 || activeFilePreludes > 0 do
                Monitor.Wait sync |> ignore

            if not (String.Equals(Directory.GetCurrentDirectory(), daemonDirectory, StringComparison.Ordinal)) then
                Directory.SetCurrentDirectory daemonDirectory
            currentDirectory <- daemonDirectory
            applyEnvironment (readProcessEnvironment ()) daemonBaseline
            currentEnvironment <- daemonBaseline
            activeFilePreludes <- 1)

    member _.TryEnterFilePrelude() =
        lock sync (fun () ->
            if activeRuns = 0 && activeFilePreludes = 0 then
                if not (String.Equals(Directory.GetCurrentDirectory(), daemonDirectory, StringComparison.Ordinal)) then
                    Directory.SetCurrentDirectory daemonDirectory
                currentDirectory <- daemonDirectory
                applyEnvironment (readProcessEnvironment ()) daemonBaseline
                currentEnvironment <- daemonBaseline
                activeFilePreludes <- 1
                true
            else
                false)

    member _.ExitFilePrelude() =
        lock sync (fun () ->
            activeFilePreludes <- activeFilePreludes - 1
            try
                if activeRuns = 0 && activeFilePreludes = 0 then
                    Directory.SetCurrentDirectory daemonDirectory
                    currentDirectory <- daemonDirectory
            finally
                Monitor.PulseAll sync)

    member _.CanEnterFilePreludeNow() =
        lock sync (fun () -> activeRuns = 0 && activeFilePreludes = 0)

    member _.EnsureBaselineWhenIdle() =
        lock sync (fun () ->
            if activeRuns = 0 && activeFilePreludes = 0 then
                if not (String.Equals(Directory.GetCurrentDirectory(), daemonDirectory, StringComparison.Ordinal)) then
                    Directory.SetCurrentDirectory daemonDirectory
                currentDirectory <- daemonDirectory
                applyEnvironment (readProcessEnvironment ()) daemonBaseline
                currentEnvironment <- daemonBaseline)
    member _.RespawnEnvironment() =
        daemonBaseline |> Seq.map (fun (KeyValue(key, value)) -> key, value) |> Seq.toArray

    member _.IsQuiet(quietMs: int64) =
        lock sync (fun () -> activeRuns = 0 && Environment.TickCount64 - lastExit >= quietMs)

type private PreludePool(spec: Prelude.Spec, tag: Prelude.Tag) =
    member _.Spec = spec
    member val Tag = tag with get, set
    member val Warm = Queue<WarmSession>()
    member val Failure: string option = None with get, set
    member val Claimers = 0 with get, set
exception private PreludeEvaluationFailure of attemptedTag: Prelude.Tag * inner: exn

type SessionHost(options: SessionHostOptions) =
    let targetPoolSize = if options.Oneshot then 1 else max 0 options.PoolSize
    let poolGate = obj ()
    let pools = ResizeArray<PreludePool>()
    let mutable desiredSpec = options.Prelude
    let mutable claimWaiters = 0
    let mutable ensureWarmActive = 0
    let mutable sessionsCreated = 0
    let creationGate = new SemaphoreSlim(1, 1)
    let replenishSignal = new SemaphoreSlim(0)
    let pendingCleanups = ConcurrentQueue<CleanupWork>()
    let releasedSessions = ConcurrentQueue<WeakReference>()
    let processState = ProcessStateGate()
    let coldClaims = ref 0
    let executed = ref 0
    let logDetail message = options.LogPath |> Option.iter (fun _ -> Log.info options.LogPath message)
    let evaluateUserPrelude (session: FsiEvaluationSession) (path: string) : Prelude.Tag * string =
        match Prelude.readAndHashFile path with
        | None ->
            let missingTag = Prelude.Tag.File(Path.GetFullPath path, "unreadable")
            raise (PreludeEvaluationFailure(missingTag, InvalidOperationException("configured prelude file could not be read: " + path)))
        | Some (text, contentHash) ->
            let exactTag = Prelude.Tag.File(Path.GetFullPath path, contentHash)
            try
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

                exactTag, trackedDirectory
            with ex ->
                raise (PreludeEvaluationFailure(exactTag, ex))
    let enqueueCleanup (claimed: WarmSession) =
        match claimed.Session with
        | Some session -> clearScriptStatics claimed.PreludeTypes session.DynamicAssemblies
        | None -> ()

        clearCompilerAsyncLocals ()
        claimed.PreludeTypes.Clear()

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
        claimed.SetCompilerDirectory <- None
        claimed.ProcessExitHandlers <- [||]
        claimed.UnmanagedDllHandlers <- [||]
        claimed.AssemblyResolveHandlers <- [||]

    let releaseSessionWith collectionsBefore (claimed: WarmSession) =
        enqueueCleanup claimed
        sweepTransientFinalizers collectionsBefore

    let releaseSession = releaseSessionWith ValueNone

    let createWarmSession (deferFailureSweep: bool) (spec: Prelude.Spec) : WarmSession =
        processState.EnsureBaselineWhenIdle()
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

        let created = Interlocked.Increment(&sessionsCreated)
        logDetail ("session-created total=" + string created)
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
            let preludeTag, compilerDirectory =
                try
                    try
                        match spec with
                        | Prelude.Spec.Bare ->
                            session.EvalInteraction(prelude exitMarker)
                            Prelude.Tag.Bare, ""
                        | Prelude.Spec.Builtin ->
                            session.EvalInteraction(prelude exitMarker + builtinPrelude)
                            Prelude.Tag.Builtin, ""
                        | Prelude.Spec.File path ->
                            session.EvalInteraction(prelude exitMarker + builtinPrelude)
                            evaluateUserPrelude session path
                    with
                    | PreludeEvaluationFailure (exactTag, (:? FsiCompilationException as compEx)) ->
                        let diagnostics = creationErr.ToString()
                        let wrapped =
                            if diagnostics.Trim().Length = 0 then
                                compEx :> exn
                            else
                                Exception("configured prelude did not compile:" + Environment.NewLine + diagnostics)
                        raise (PreludeEvaluationFailure(exactTag, wrapped))
                    | :? FsiCompilationException ->
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
            if not (isNull tcConfigBField) then
                let builder = tcConfigBField.GetValue session
                let property = builder.GetType().GetProperty("implicitIncludeDir", BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
                if not (isNull property) && property.CanWrite then
                    warm.CompilerDirectory <- property.GetValue builder :?> string
                    warm.SetCompilerDirectory <- Some(fun directory -> property.SetValue(builder, directory))
            match spec with
            | Prelude.Spec.File _ -> ()
            | _ ->
                for assembly in session.DynamicAssemblies do
                    warm.PreludeTypes.UnionWith(assembly.GetTypes())
            succeeded <- true
            warm
        finally
            if not succeeded then
                if deferFailureSweep then enqueueCleanup warm else releaseSession warm
                replenishSignal.Release() |> ignore
    let tagOfSpec spec = Prelude.tagOfSpec spec

    let discard = ConcurrentQueue<WarmSession>()

    let clearPool (slot: PreludePool) =
        while slot.Warm.Count > 0 do
            discard.Enqueue(slot.Warm.Dequeue())

    let refreshTagWith tag (slot: PreludePool) =
        if slot.Tag <> tag then
            clearPool slot
            slot.Tag <- tag
            slot.Failure <- None
        tag

    let trimExcessPools () =
        while pools.Count > 2 && pools.Exists(fun candidate -> candidate.Claimers = 0) do
            let evictIdx = pools.FindIndex(fun candidate -> candidate.Claimers = 0)
            let evicted = pools.[evictIdx]
            clearPool evicted
            pools.RemoveAt evictIdx

    let activateSpec spec =
        let tag = tagOfSpec spec
        lock poolGate (fun () ->
            desiredSpec <- spec
            let index = pools.FindIndex(fun slot -> slot.Spec = spec)
            let slot =
                if index >= 0 then
                    let existing = pools.[index]
                    pools.RemoveAt index
                    existing
                else
                    PreludePool(spec, tag)
            pools.Add slot
            trimExcessPools ()
            slot, refreshTagWith tag slot)
    let poolCount () = lock poolGate (fun () -> pools |> Seq.sumBy (fun slot -> slot.Warm.Count))

    let claimSession (slot: PreludePool) (cancelToken: CancellationToken) : WarmSession =
        lock poolGate (fun () ->
            slot.Claimers <- slot.Claimers + 1
            if not (pools.Contains slot) then
                pools.Add slot
            trimExcessPools ())

        try
            let tryClaim () =
                let tag = tagOfSpec slot.Spec
                lock poolGate (fun () ->
                    refreshTagWith tag slot |> ignore
                    if slot.Warm.Count = 0 then None else Some(slot.Warm.Dequeue()))

            match tryClaim () with
            | Some warm -> warm
            | None ->
                Interlocked.Increment(&claimWaiters) |> ignore
                try
                    logDetail "session-claim waiting"
                    let mutable claimedOpt = None
                    while claimedOpt.IsNone do
                        creationGate.Wait(cancelToken)
                        let mutable releaseCreationGate = true
                        try
                            match tryClaim () with
                            | Some warm -> claimedOpt <- Some warm
                            | None ->
                                let canCreateNow =
                                    match slot.Spec with
                                    | Prelude.Spec.File _ -> processState.TryEnterFilePrelude()
                                    | _ -> true
                                if not canCreateNow then
                                    creationGate.Release() |> ignore
                                    releaseCreationGate <- false
                                    processState.EnterFilePreludeBlocking()
                                    try
                                        creationGate.Wait(cancelToken)
                                        releaseCreationGate <- true
                                        match tryClaim () with
                                        | Some warm -> claimedOpt <- Some warm
                                        | None ->
                                            Interlocked.Increment(&coldClaims.contents) |> ignore
                                            try
                                                let warm = createWarmSession false slot.Spec
                                                let currentTag = tagOfSpec slot.Spec
                                                lock poolGate (fun () ->
                                                    if refreshTagWith currentTag slot = warm.Prelude then
                                                        slot.Failure <- None)
                                                claimedOpt <- Some warm
                                            with
                                            | PreludeEvaluationFailure (exactTag, inner) ->
                                                let currentTag = tagOfSpec slot.Spec
                                                lock poolGate (fun () ->
                                                    if refreshTagWith currentTag slot = exactTag then
                                                        slot.Failure <- Some inner.Message)
                                                raise inner
                                            | ex ->
                                                let fallbackTag = tagOfSpec slot.Spec
                                                lock poolGate (fun () ->
                                                    if refreshTagWith fallbackTag slot = fallbackTag then
                                                        slot.Failure <- Some ex.Message)
                                                reraise ()
                                    finally
                                        processState.ExitFilePrelude()
                                else
                                    try
                                        Interlocked.Increment(&coldClaims.contents) |> ignore
                                        try
                                            let warm = createWarmSession false slot.Spec
                                            let currentTag = tagOfSpec slot.Spec
                                            lock poolGate (fun () ->
                                                if refreshTagWith currentTag slot = warm.Prelude then
                                                    slot.Failure <- None)
                                            claimedOpt <- Some warm
                                        with
                                        | PreludeEvaluationFailure (exactTag, inner) ->
                                            let currentTag = tagOfSpec slot.Spec
                                            lock poolGate (fun () ->
                                                if refreshTagWith currentTag slot = exactTag then
                                                    slot.Failure <- Some inner.Message)
                                            raise inner
                                        | ex ->
                                            let fallbackTag = tagOfSpec slot.Spec
                                            lock poolGate (fun () ->
                                                if refreshTagWith fallbackTag slot = fallbackTag then
                                                    slot.Failure <- Some ex.Message)
                                            reraise ()
                                    finally
                                        match slot.Spec with
                                        | Prelude.Spec.File _ -> processState.ExitFilePrelude()
                                        | _ -> ()
                        finally
                            if releaseCreationGate then
                                creationGate.Release() |> ignore
                    claimedOpt.Value
                finally
                    Interlocked.Decrement(&claimWaiters) |> ignore
        finally
            lock poolGate (fun () ->
                slot.Claimers <- slot.Claimers - 1
                trimExcessPools ())

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

    let mutable totalReleased = 0

    let disposePendingCleanups () =
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

        drained

    let drainPendingCleanups () =
        let drained = disposePendingCleanups ()
        if drained > 0 then
            totalReleased <- totalReleased + drained
            sweepFinalizers ()
            let alive = drainReleasedSessions ()
            logDetail ($"unload released={totalReleased} alive={alive}")

    let drainCleanups () =
        if not pendingCleanups.IsEmpty && creationGate.Wait 0 then
            try
                if pendingCleanups.Count >= 16 then
                    drainPendingCleanups ()
                elif Volatile.Read(&claimWaiters) = 0 && processState.IsQuiet 500L then
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
                match claimed.SetCompilerDirectory with
                | Some setDirectory when Path.IsPathFullyQualified directory && Directory.Exists directory -> setDirectory directory
                | _ -> session.EvalInteraction("#cd @\"" + Prelude.escapeVerbatim directory + "\"")
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

        let evalCode (code: string) (scriptName: string) (normalizeWindowsHeader: bool) =
            let chunks = splitInteractions code scriptName
            try
                for chunk in chunks do
                    checkedSource <- true
                    attemptedCode <- chunk
                    attemptedScriptName <- scriptName
                    typesBeforeCurrentEval <- emittedTypeCount ()
                    precheckedDiagnostics <- None
                    let errLenBefore = errWriter.SafeText().Length

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

                    // FCS `EvalInteraction` returns without throwing on syntax/indentation errors (printing
                    // `): error FSxxxx:` without emitting any CLR types), whereas warning-only empty/stray-`;`
                    // interactions print only `warning FSxxxx:` and must stay exit 0.
                    let errAfter = errWriter.SafeText()
                    if
                        errAfter.Length > errLenBefore
                        && errAfter.IndexOf("): error FS", errLenBefore, StringComparison.Ordinal) >= 0
                        && emittedTypeCount () = typesBeforeCurrentEval
                    then
                        raise (FsiCompilationException("compile error", None))
            finally
                if mayMutateCompilerDirectory code then
                    claimed.CompilerDirectory <- ""

                // Windows `dotnet fsi` strips the directory from diagnostic headers when `sourcePath`
                // shares the process drive (`file.fsx(l,c):`), whereas Linux/macOS `dotnet fsi` prints
                // the full `/path/to/file.fsx(l,c):` header emitted by FCS (verified on 3-OS CI).
                if normalizeWindowsHeader && OperatingSystem.IsWindows() then
                    let fileName = Path.GetFileName prepared.SourcePath

                    if not (String.Equals(prepared.SourcePath, fileName, StringComparison.Ordinal)) then
                        errWriter.ReplaceDiagnosticSource(prepared.SourcePath, fileName)

        let run () =
            match prepared.Rewritten with
            | Some (referenceBlock, rewrittenSource) ->
                changeDirectory prepared.ScriptDirectory
                session.EvalInteraction(referenceBlock, "fsion-nuget-refs.fsx")
                evalCode rewrittenSource checkedSourceName (request.Mode = "file" && prepared.SourceExists)
            | None ->
                if request.Mode = "file" then
                    if prepared.SourceExists then
                        changeDirectory prepared.ScriptDirectory
                        evalCode (defaultArg prepared.LoadedText "") checkedSourceName true
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
                    evalCode (defaultArg prepared.LoadedText "") prepared.SourceName false

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

            let mutable evictedAny = false
            for sessionId, entry in snapshot do
                let expired () =
                    not entry.Evicted
                    && Environment.TickCount64 - entry.LastUsedMs >= sessionTtlMs

                if expired () && entry.Gate.Wait 0 then
                    try
                        if expired () then
                            entry.Evicted <- true
                            evictedAny <- true

                            lock namedGate (fun () ->
                                match namedSessions.TryGetValue sessionId with
                                | true, existing when Object.ReferenceEquals(existing, entry) ->
                                    namedSessions.Remove sessionId |> ignore
                                | _ -> ())

                            match entry.Session with
                            | Some warm -> enqueueCleanup warm
                            | None -> ()

                            entry.Session <- None
                    finally
                        entry.Gate.Release() |> ignore
            if evictedAny then
                replenishSignal.Release() |> ignore
    let drainDiscarded () =
        let mutable stale = Unchecked.defaultof<WarmSession>
        while discard.TryDequeue(&stale) do
            enqueueCleanup stale

    let replenish () =
        while true do
            replenishSignal.Wait 125 |> ignore
            drainDiscarded ()
            drainCleanups ()

            let mutable filling = targetPoolSize > 0
            while filling
                  && Volatile.Read(&claimWaiters) = 0
                  && (Volatile.Read(&ensureWarmActive) > 0 || processState.IsQuiet 250L) do
                let next =
                    let snapshot = lock poolGate (fun () -> pools.ToArray())
                    let tags =
                        [| for candidate in snapshot do
                               let needsCheck =
                                   lock poolGate (fun () ->
                                       pools.Contains candidate
                                       && (candidate.Warm.Count < targetPoolSize || candidate.Failure.IsSome))
                               if needsCheck then
                                   yield candidate, tagOfSpec candidate.Spec |]
                    lock poolGate (fun () ->
                        for candidate, tag in tags do
                            if pools.Contains candidate then
                                refreshTagWith tag candidate |> ignore
                        pools
                        |> Seq.rev
                        |> Seq.filter (fun slot ->
                            slot.Failure.IsNone
                            && slot.Warm.Count < targetPoolSize
                            && (match slot.Spec with
                                | Prelude.Spec.File _ -> processState.CanEnterFilePreludeNow()
                                | _ -> true))
                        |> Seq.sortBy (fun slot -> slot.Warm.Count)
                        |> Seq.tryHead
                        |> Option.map (fun slot -> slot, slot.Tag))
                match next with
                | None -> filling <- false
                | Some (slot, _) ->
                    if Volatile.Read(&claimWaiters) > 0
                       || (Volatile.Read(&ensureWarmActive) = 0 && not (processState.IsQuiet 250L))
                       || not (creationGate.Wait 0) then
                        filling <- false
                    else
                        lock poolGate (fun () -> slot.Claimers <- slot.Claimers + 1)
                        try
                            if Volatile.Read(&claimWaiters) > 0
                               || (Volatile.Read(&ensureWarmActive) = 0 && not (processState.IsQuiet 250L)) then
                                filling <- false
                            else
                                let entered =
                                    match slot.Spec with
                                    | Prelude.Spec.File _ ->
                                        if processState.TryEnterFilePrelude() then ValueSome true else ValueNone
                                    | _ -> ValueSome false
                                match entered with
                                | ValueNone -> filling <- false
                                | ValueSome isFile ->
                                    try
                                        try
                                            let warm = createWarmSession true slot.Spec
                                            if processState.IsQuiet 250L then
                                                GC.Collect(1, GCCollectionMode.Optimized, false)
                                            let currentTag = tagOfSpec slot.Spec
                                            lock poolGate (fun () ->
                                                if pools.Contains slot && refreshTagWith currentTag slot = warm.Prelude then
                                                    slot.Warm.Enqueue warm
                                                else
                                                    discard.Enqueue warm)
                                        with
                                        | PreludeEvaluationFailure (exactTag, inner) ->
                                            Log.error options.LogPath ("replenisher could not create a warm session: " + inner.Message)
                                            let currentTag = tagOfSpec slot.Spec
                                            lock poolGate (fun () ->
                                                if pools.Contains slot && refreshTagWith currentTag slot = exactTag then
                                                    slot.Failure <- Some inner.Message)
                                        | ex ->
                                            Log.error options.LogPath ("replenisher could not create a warm session: " + ex.Message)
                                            let fallbackTag = tagOfSpec slot.Spec
                                            lock poolGate (fun () ->
                                                if pools.Contains slot && refreshTagWith fallbackTag slot = fallbackTag then
                                                    slot.Failure <- Some ex.Message)
                                    finally
                                        if isFile then
                                            processState.ExitFilePrelude()
                        finally
                            lock poolGate (fun () ->
                                slot.Claimers <- slot.Claimers - 1
                                trimExcessPools ())
                            creationGate.Release() |> ignore
                drainDiscarded ()
                drainCleanups ()

    do
        if not options.Oneshot then
            activateSpec options.Prelude |> ignore
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

    let waitForWarm timeoutMs failOnEvict (slot: PreludePool) =
        let deadline = Environment.TickCount64 + timeoutMs
        let state () = lock poolGate (fun () -> slot.Warm.Count, slot.Failure, pools.Contains slot)
        let mutable count, failure, retained = state ()
        while count < targetPoolSize && failure.IsNone && retained && Environment.TickCount64 < deadline do
            Thread.Sleep 5
            let nextCount, nextFailure, nextRetained = state ()
            count <- nextCount
            failure <- nextFailure
            retained <- nextRetained
        if not retained then
            if failOnEvict then
                Some "requested prelude pool was evicted during warmup"
            else
                None
        else
            match failure with
            | Some message -> Some message
            | None when count < targetPoolSize ->
                Some("timed out waiting for warm session pool to fill within " + string timeoutMs + " ms")
            | None -> None

    member _.DesiredSpec = lock poolGate (fun () -> desiredSpec)

    member _.RespawnEnvironment() = processState.RespawnEnvironment()

    member _.EnsureWarm(spec: Prelude.Spec) =
        if not options.Oneshot then
            let slot, _ = activateSpec spec
            lock poolGate (fun () -> slot.Failure <- None)
            if targetPoolSize > 0 then
                Interlocked.Increment(&ensureWarmActive) |> ignore
                try
                    replenishSignal.Release() |> ignore
                    match waitForWarm 60000L true slot with
                    | Some message -> failwith message
                    | None -> ()
                finally
                    Interlocked.Decrement(&ensureWarmActive) |> ignore
            else
                match spec with
                | Prelude.Spec.File _ ->
                    let probe =
                        processState.EnterFilePreludeBlocking()
                        try
                            creationGate.Wait()
                            try
                                createWarmSession false spec
                            finally
                                creationGate.Release() |> ignore
                        finally
                            processState.ExitFilePrelude()
                    try
                        releaseSession probe
                    finally
                        replenishSignal.Release() |> ignore
                | _ -> ()

    member _.IsWarm =
        lock poolGate (fun () ->
            match pools |> Seq.tryFind (fun slot -> slot.Spec = desiredSpec) with
            | Some slot -> slot.Warm.Count > 0
            | None -> false)

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
            let started = Stopwatch.GetTimestamp()
            let mutable evaluateMs = 0.0
            let mutable collectionsBefore = ValueNone
            let mutable entered = false

            try
                processState.Enter(request.WorkingDirectory, request.Environment)
                entered <- true

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

                                if stateless then
                                    collectionsBefore <- ValueSome(struct (GC.CollectionCount 0, GC.CollectionCount 1))

                                let evaluateStarted = Stopwatch.GetTimestamp()
                                let exitCode, diagnostics =
                                    evaluate claimed request prepared outWriter errWriter runCts.Token

                                evaluateMs <- Stopwatch.GetElapsedTime(evaluateStarted).TotalMilliseconds
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
                    if entered then
                        processState.Exit ()
            finally
                try
                    if stateless then
                        if entered then releaseSessionWith collectionsBefore claimed else releaseSession claimed
                finally
                    if options.LogPath.IsSome then
                        let gateReleaseMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - evaluateMs
                        logDetail ($"run-phases evalMs={evaluateMs:F3} gateReleaseMs={gateReleaseMs:F3}")
                    if not options.Oneshot then replenishSignal.Release() |> ignore

        let canceledOutcome () =
            Interlocked.Increment(&executed.contents) |> ignore

            { ExitCode = 1
              StandardOut = outWriter.SafeText()
              StandardError = errWriter.SafeText()
              Diagnostics = [||]
              TimedOut = false }

        let work () =
            let targetSpec = Prelude.specOfRequest request
            let slot, expectedTag = activateSpec targetSpec

            let outcome =
                try
                    match request.SessionId with
                    | None ->
                        let claimed = claimSession slot disconnectToken

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
                                    entry.Session <- Some(claimSession slot disconnectToken)

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
        if not options.Oneshot && targetPoolSize > 0 then
            let slots = lock poolGate (fun () -> pools.ToArray())
            replenishSignal.Release() |> ignore
            for slot in slots do
                waitForWarm 30000L false slot |> ignore

        drainCleanupsBlocking ()
        sweepFinalizers ()

        let scriptAssemblyCount = scriptAssemblies () |> Array.length
        let aliveDisposedSessions = drainReleasedSessions ()

        { PoolTarget = targetPoolSize
          PoolWarm = poolCount ()
          ColdClaims = coldClaims.Value
          Executed = executed.Value
          ManagedHeapBytes = GC.GetTotalMemory true
          ScriptAssemblies = scriptAssemblyCount
          AliveDisposedSessions = aliveDisposedSessions
          NamedSessions = lock namedGate (fun () -> namedSessions.Count) }
