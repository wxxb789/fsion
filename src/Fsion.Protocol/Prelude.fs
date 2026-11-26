module Fsion.PreludeConfig

open System
open System.IO

let [<Literal>] PreludeEnvironmentVariable = "FSION_PRELUDE"

let [<Literal>] ConfigDirectoryEnvironmentVariable = "FSION_CONFIG_DIR"

let [<Literal>] CacheDirectoryEnvironmentVariable = "FSION_CACHE_DIR"

let private environment (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null -> None
    | value when value.Length = 0 -> None
    | value -> Some value

let configuredPreludePath () : string option = environment PreludeEnvironmentVariable

let private mapValue (environment: (string * string)[]) (name: string) =
    let comparer =
        if OperatingSystem.IsWindows() then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    environment
    |> Array.tryPick (fun (key, value) ->
        if comparer.Equals(key, name) && not (isNull value) && value.Length > 0 then
            Some value
        else
            None)

let mutable private cachedWindowsApplicationData = ""
let private posixAccountHomeDirectory () =
    let savedHome = Environment.GetEnvironmentVariable "HOME"
    try
        Environment.SetEnvironmentVariable("HOME", null)
        try
            let resolved = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
            if not (isNull resolved) && resolved.Length > 0 then
                resolved
            else
                ""
        with _ ->
            ""
    finally
        Environment.SetEnvironmentVariable("HOME", savedHome)
let private cachedPosixPasswdConfigDirectory =
    lazy (
        let accountHome = posixAccountHomeDirectory ()
        if accountHome.Length > 0 then Path.Combine(accountHome, ".config") else "")
let private cachedMacApplicationSupportDirectory =
    lazy (
        if OperatingSystem.IsMacOS() then
            let accountHome = posixAccountHomeDirectory ()
            if accountHome.Length > 0 then Path.Combine(accountHome, "Library", "Application Support") else ""
        else
            "")
let initializeProcessCaches () =
    if OperatingSystem.IsWindows() then
        let resolved = Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
        if not (String.IsNullOrEmpty resolved) then
            cachedWindowsApplicationData <- resolved
    else
        cachedPosixPasswdConfigDirectory.Value |> ignore
        cachedMacApplicationSupportDirectory.Value |> ignore

let private resolveWindowsApplicationData () =
    let cached = cachedWindowsApplicationData
    if cached.Length > 0 then
        cached
    else
        // Do not permanently cache an empty result (e.g. a fresh profile where Roaming AppData
        // was created after the daemon started); re-query until non-empty.
        let resolved = Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
        if not (String.IsNullOrEmpty resolved) then
            cachedWindowsApplicationData <- resolved
            resolved
        else
            ""

let userPreludePathFrom
    (includeConfiguredConfigDir: bool)
    (workingDirectory: string option)
    (lookup: string -> string option)
    : string option =
    let configDirectoryOpt =
        match lookup ConfigDirectoryEnvironmentVariable with
        | Some directory ->
            if includeConfiguredConfigDir then Some directory else None
        | None ->
            if OperatingSystem.IsWindows() then
                let appData = resolveWindowsApplicationData ()
                if appData.Length > 0 then
                    Some(Path.Combine(appData, "fsion"))
                else
                    None
            else
                let existingDir (path: string) =
                    if path.Length = 0 then
                        None
                    else
                        let resolved =
                            match workingDirectory with
                            | Some baseDir when baseDir.Length > 0 && Path.IsPathFullyQualified baseDir ->
                                Path.GetFullPath(path, baseDir)
                            | _ -> Path.GetFullPath path
                        if Directory.Exists resolved then Some resolved else None
                if OperatingSystem.IsMacOS() then
                    let appSupportBase =
                        match lookup "HOME" with
                        | Some directory -> Path.Combine(directory, "Library", "Application Support")
                        | None -> cachedMacApplicationSupportDirectory.Value
                    existingDir appSupportBase |> Option.map (fun d -> Path.Combine(d, "fsion"))
                else
                    let xdgDirOpt =
                        match lookup "XDG_CONFIG_HOME" with
                        | Some directory when Path.IsPathRooted directory -> existingDir directory
                        | _ ->
                            let xdgBase =
                                match lookup "HOME" with
                                | Some directory -> Path.Combine(directory, ".config")
                                | None -> cachedPosixPasswdConfigDirectory.Value
                            existingDir xdgBase
                    xdgDirOpt |> Option.map (fun d -> Path.Combine(d, "fsion"))
    match configDirectoryOpt with
    | None -> None
    | Some configDirectory ->
        let candidate =
            match workingDirectory with
            | Some baseDir when baseDir.Length > 0 && Path.IsPathFullyQualified baseDir ->
                Path.GetFullPath(Path.Combine(configDirectory, "prelude.fsx"), baseDir)
            | _ -> Path.GetFullPath(Path.Combine(configDirectory, "prelude.fsx"))
        if File.Exists candidate then Some candidate else None
let userPreludePath () : string option = userPreludePathFrom true None environment

let userPreludePathForEnvironment (workingDirectory: string) (snapshot: (string * string)[]) : string option =
    userPreludePathFrom false (Some workingDirectory) (mapValue snapshot)
let cacheDirectory () : string =
    match environment CacheDirectoryEnvironmentVariable with
    | Some directory -> directory
    | None ->
        let baseDirectory =
            if OperatingSystem.IsWindows() then
                Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData
            else
                match environment "XDG_CACHE_HOME" with
                | Some directory -> directory
                | None -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".cache")
        Path.Combine(baseDirectory, "fsion", "nuget-cache")
