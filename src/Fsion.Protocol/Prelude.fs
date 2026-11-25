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

let private cachedWindowsApplicationData =
    lazy (Environment.GetFolderPath Environment.SpecialFolder.ApplicationData)

let private posixAccountHomeDirectory () =
    let currentUser = Environment.UserName
    let tryPasswd () =
        try
            if File.Exists "/etc/passwd" then
                File.ReadAllLines "/etc/passwd"
                |> Array.tryPick (fun line ->
                    if line.Length = 0 || line.[0] = '#' then
                        None
                    else
                        let parts = line.Split(':')
                        if parts.Length >= 6 && String.Equals(parts.[0], currentUser, StringComparison.Ordinal) && parts.[5].Length > 0 then
                            Some parts.[5]
                        else
                            None)
            else
                None
        with _ ->
            None
    match tryPasswd () with
    | Some home -> home
    | None ->
        if OperatingSystem.IsMacOS() && currentUser.Length > 0 then
            let candidate = Path.Combine("/Users", currentUser)
            if Directory.Exists candidate then candidate else ""
        elif currentUser.Length > 0 then
            let candidate = Path.Combine("/home", currentUser)
            if Directory.Exists candidate then candidate else ""
        else
            ""

let private cachedPosixPasswdConfigDirectory =
    lazy (
        let accountHome = posixAccountHomeDirectory ()
        if accountHome.Length > 0 then Path.Combine(accountHome, ".config") else "")

let initializeProcessCaches () =
    if OperatingSystem.IsWindows() then
        cachedWindowsApplicationData.Value |> ignore
    else
        cachedPosixPasswdConfigDirectory.Value |> ignore

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
            let home =
                if OperatingSystem.IsWindows() then
                    cachedWindowsApplicationData.Value
                else
                    match lookup "XDG_CONFIG_HOME" with
                    | Some directory -> directory
                    | None ->
                        match lookup "HOME" with
                        | Some directory -> Path.Combine(directory, ".config")
                        | None -> cachedPosixPasswdConfigDirectory.Value

            if home.Length > 0 then Some(Path.Combine(home, "fsion")) else None

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
