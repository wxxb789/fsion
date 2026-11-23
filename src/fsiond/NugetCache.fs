module Fsiond.NugetCache

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FSharp.Compiler.DependencyManager
open FSharp.Compiler.Interactive.Shell
open Fsion.PreludeConfig

type Plan =
    { ReferencedAssemblies: string list
      IncludeDirectories: string list
      RewrittenSource: string }

type private Directive =
    { LineStart: int
      LineEnd: int
      Normalized: string
      Original: string }

type private ScanState =
    | Code
    | LineComment
    | BlockComment of int
    | NormalString
    | VerbatimString
    | TripleString

let private digest (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private digestText (text: string) = digest (Encoding.UTF8.GetBytes text)

let private normalizePackage (text: string) =
    text.Split(',')
    |> Array.map (fun part -> part.Trim())
    |> Array.filter (fun part -> part.Length > 0)
    |> String.concat ", "
    |> fun value -> value.ToLowerInvariant()

let private scanDirectives (source: string) : Directive list =
    let directives = ResizeArray<Directive>()
    let length = source.Length
    let mutable state = Code
    let mutable i = 0
    let mutable lineStart = 0
    let mutable onlyWhitespace = true

    let advance () = i <- i + 1

    let newline () =
        i <- i + 1
        lineStart <- i
        onlyWhitespace <- true

    let tryParseQuoted (start: int) : (int * string) option =
        let mutable j = start

        while j < length && (source.[j] = ' ' || source.[j] = '\t') do
            j <- j + 1

        let verbatim = j < length && source.[j] = '@'

        if verbatim then
            j <- j + 1

        if j < length && source.[j] = '"' then
            let contentStart = j + 1
            let mutable k = contentStart
            let mutable closed = false

            while k < length && not closed do
                if verbatim then
                    if source.[k] = '"' then
                        if k + 1 < length && source.[k + 1] = '"' then
                            k <- k + 2
                        else
                            closed <- true
                    else
                        k <- k + 1
                elif source.[k] = '\\' && k + 1 < length then
                    k <- k + 2
                elif source.[k] = '"' then
                    closed <- true
                elif source.[k] = '\n' then
                    k <- length
                else
                    k <- k + 1

            if closed then Some(k + 1, source.Substring(contentStart, k - contentStart)) else None
        else
            None

    while i < length do
        let c = source.[i]

        match state with
        | Code ->
            if c = '\n' then
                newline ()
            elif onlyWhitespace && (c = ' ' || c = '\t' || c = '\r') then
                advance ()
            elif c = '/' && i + 1 < length && source.[i + 1] = '/' then
                onlyWhitespace <- false
                state <- LineComment
                i <- i + 2
            elif c = '(' && i + 1 < length && source.[i + 1] = '*' then
                onlyWhitespace <- false
                state <- BlockComment 1
                i <- i + 2
            elif c = '"' then
                onlyWhitespace <- false

                if i + 2 < length && source.[i + 1] = '"' && source.[i + 2] = '"' then
                    state <- TripleString
                    i <- i + 3
                else
                    state <- NormalString
                    advance ()
            elif c = '@' && i + 1 < length && source.[i + 1] = '"' then
                onlyWhitespace <- false
                state <- VerbatimString
                i <- i + 2
            elif
                onlyWhitespace
                && c = '#'
                && i + 1 < length
                && source.[i + 1] = 'r'
                && (i + 2 >= length || not (Char.IsLetterOrDigit source.[i + 2] || source.[i + 2] = '_'))
            then
                match tryParseQuoted (i + 2) with
                | Some (nextIndex, content) when content.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase) ->
                    let mutable lineEnd = nextIndex

                    while lineEnd < length && source.[lineEnd] <> '\n' do
                        lineEnd <- lineEnd + 1

                    let original = content.Substring("nuget:".Length).Trim()

                    directives.Add
                        { LineStart = lineStart
                          LineEnd = lineEnd
                          Normalized = normalizePackage original
                          Original = original }

                    onlyWhitespace <- false
                    i <- nextIndex
                | _ ->
                    onlyWhitespace <- false
                    advance ()
            else
                onlyWhitespace <- false
                advance ()
        | LineComment ->
            if c = '\n' then newline () else advance ()
        | BlockComment depth ->
            if c = '\n' then
                newline ()
            elif c = '(' && i + 1 < length && source.[i + 1] = '*' then
                state <- BlockComment(depth + 1)
                i <- i + 2
            elif c = '*' && i + 1 < length && source.[i + 1] = ')' then
                state <- if depth = 1 then Code else BlockComment(depth - 1)
                onlyWhitespace <- false
                i <- i + 2
            else
                advance ()
        | NormalString ->
            if c = '\\' && i + 1 < length then
                i <- i + 2
            elif c = '"' then
                state <- Code
                onlyWhitespace <- false
                advance ()
            elif c = '\n' then
                state <- Code
                newline ()
            else
                advance ()
        | VerbatimString ->
            if c = '"' then
                if i + 1 < length && source.[i + 1] = '"' then
                    i <- i + 2
                else
                    state <- Code
                    onlyWhitespace <- false
                    advance ()
            elif c = '\n' then
                newline ()
            else
                advance ()
        | TripleString ->
            if c = '"' && i + 2 < length && source.[i + 1] = '"' && source.[i + 2] = '"' then
                state <- Code
                onlyWhitespace <- false
                i <- i + 3
            elif c = '\n' then
                newline ()
            else
                advance ()

    directives |> Seq.toList

let private blankDirectives (source: string) (directives: Directive list) =
    let builder = StringBuilder()
    let mutable position = 0

    for directive in directives do
        if directive.LineStart >= position then
            builder.Append(source, position, directive.LineStart - position) |> ignore
            position <- directive.LineEnd

    builder.Append(source, position, source.Length - position) |> ignore
    builder.ToString()

let private targetFramework () =
    let version = Environment.Version
    "net" + string version.Major + "." + string version.Minor

let private configFingerprint (startDirectory: string) =
    let paths = ResizeArray<string>()
    let mutable directory = try Some(DirectoryInfo(Path.GetFullPath startDirectory)) with _ -> None

    while directory.IsSome do
        let current = directory.Value

        try
            for file in Directory.EnumerateFiles current.FullName do
                if Path.GetFileName(file).Equals("nuget.config", StringComparison.OrdinalIgnoreCase) then
                    paths.Add file
        with _ ->
            ()

        directory <- if isNull current.Parent then None else Some current.Parent

    let userConfig =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "NuGet", "NuGet.Config")

    if File.Exists userConfig then
        paths.Add userConfig

    paths
    |> Seq.distinct
    |> Seq.map (fun path ->
        path
        + "="
        + (match Fsiond.Prelude.hashFile path with
           | Some hash -> hash
           | None -> "missing"))
    |> String.concat "\n"

let private cacheKey (tfm: string) (rid: string) (packages: string list) (fingerprint: string) =
    digestText (String.concat "\n" ("fsion-nuget-cache-v1" :: tfm :: rid :: (packages @ [ fingerprint ])))

let private tryReadEntry (cachePath: string) : (string list * string list) option =
    try
        use document = JsonDocument.Parse(File.ReadAllText cachePath)
        let root = document.RootElement

        if root.GetProperty("version").GetInt32() <> 1 then
            None
        else
            let references = [ for element in root.GetProperty("references").EnumerateArray() -> element.GetString() ]
            let roots = [ for element in root.GetProperty("roots").EnumerateArray() -> element.GetString() ]

            if references.Length > 0 && references |> List.forall File.Exists then
                Some(references, roots)
            else
                None
    with _ ->
        None

let private writeEntry
    (cachePath: string)
    (tfm: string)
    (rid: string)
    (packages: string list)
    (fingerprint: string)
    (references: string list)
    (roots: string list)
    =
    try
        let directory = Path.GetDirectoryName cachePath

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory directory |> ignore

        use stream = new MemoryStream()

        do
            use writer = new Utf8JsonWriter(stream)
            writer.WriteStartObject()
            writer.WriteNumber("version", 1)
            writer.WriteString("tfm", tfm)
            writer.WriteString("rid", rid)
            writer.WriteStartArray("directives")

            for package in packages do
                writer.WriteStringValue package

            writer.WriteEndArray()
            writer.WriteString("fingerprint", fingerprint)
            writer.WriteStartArray("references")

            for reference in references do
                writer.WriteStringValue reference

            writer.WriteEndArray()
            writer.WriteStartArray("roots")

            for root in roots do
                writer.WriteStringValue root

            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.Flush()

        let temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp"
        File.WriteAllBytes(temporaryPath, stream.ToArray())
        File.Move(temporaryPath, cachePath, true)
    with ex ->
        Log.error None ("nuget cache write failed: " + ex.Message)

let private resolveGate = obj ()
let private providerInstance = lazy (new DependencyProvider())
let mutable private cachedManager : IDependencyManagerProvider option = None

let private resolveWithOfficialProvider
    (tfm: string)
    (rid: string)
    (packages: string list)
    (includeDirectory: string)
    (scriptName: string)
    : (string list * string list) option =
    lock resolveGate (fun () ->
        try
            let provider = providerInstance.Value

            let manager =
                match cachedManager with
                | Some manager -> manager
                | None ->
                    let compilerTools =
                        match Path.GetDirectoryName(typeof<FsiEvaluationSession>.Assembly.Location) with
                        | null | "" -> [| AppContext.BaseDirectory |]
                        | directory -> [| directory |]

                    let found = provider.TryFindDependencyManagerByKey(compilerTools, "", None, (fun _ _ _ -> ()), "nuget")
                    cachedManager <- Some found
                    found

            let errors = ResizeArray<string>()
            let report (kind: ErrorReportType) (number: int) (message: string) = errors.Add message
            let lines = packages |> List.map (fun package -> Tuple.Create("nuget", package))

            let directory =
                if Directory.Exists includeDirectory then
                    includeDirectory
                else
                    AppContext.BaseDirectory

            let result = provider.Resolve(manager, ".fsx", lines, report, tfm, rid, directory, scriptName, scriptName, 120000)

            if result.Success then
                Some(result.Resolutions |> Seq.toList, result.Roots |> Seq.toList)
            else
                Log.error None ("nuget resolution failed: " + String.concat "; " (Seq.append errors result.StdError))
                None
        with ex ->
            Log.error None ("nuget resolution error: " + ex.Message)
            None)

let prepare (source: string) (includeDirectory: string) (scriptName: string) : Plan option =
    let directives = scanDirectives source

    if directives.Length = 0 then
        None
    else
        let packages = directives |> List.map (fun directive -> directive.Normalized) |> List.distinct |> List.sort
        let originals = directives |> List.map (fun directive -> directive.Original) |> List.distinct
        let tfm = targetFramework ()
        let rid = RuntimeInformation.RuntimeIdentifier
        let fingerprint = configFingerprint includeDirectory
        let cachePath = Path.Combine(cacheDirectory (), cacheKey tfm rid packages fingerprint + ".json")

        let resolved =
            match tryReadEntry cachePath with
            | Some entry -> Some entry
            | None ->
                match resolveWithOfficialProvider tfm rid originals includeDirectory scriptName with
                | Some (references, roots) ->
                    writeEntry cachePath tfm rid packages fingerprint references roots
                    Some(references, roots)
                | None -> None

        match resolved with
        | Some (references, roots) ->
            Some
                { ReferencedAssemblies = references
                  IncludeDirectories = roots |> List.filter Directory.Exists
                  RewrittenSource = blankDirectives source directives }
        | None -> None

let referenceBlock (plan: Plan) =
    let escape (value: string) = value.Replace("\"", "\"\"")

    plan.ReferencedAssemblies
    |> List.map (fun reference -> "#r @\"" + escape reference + "\"")
    |> fun references ->
        references @ (plan.IncludeDirectories |> List.map (fun directory -> "#I @\"" + escape directory + "\""))
    |> String.concat "\n"
