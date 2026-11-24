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

let private cachedPosixApplicationData =
    lazy (Environment.GetFolderPath Environment.SpecialFolder.ApplicationData)

let userPreludePathFrom (lookup: string -> string option) : string option =
    let configDirectory =
        match lookup ConfigDirectoryEnvironmentVariable with
        | Some directory -> directory
        | None ->
            let home =
                if OperatingSystem.IsWindows() then
                    cachedWindowsApplicationData.Value
                else
                    match lookup "XDG_CONFIG_HOME" with
                    | Some directory -> directory
                    | None ->
                        match lookup "HOME" with
                        | Some directory when OperatingSystem.IsMacOS() ->
                            Path.Combine(directory, "Library", "Application Support")
                        | Some directory -> Path.Combine(directory, ".config")
                        | None -> cachedPosixApplicationData.Value

            Path.Combine(home, "fsion")

    let candidate = Path.GetFullPath(Path.Combine(configDirectory, "prelude.fsx"))

    if File.Exists candidate then Some candidate else None

let userPreludePath () : string option = userPreludePathFrom environment

let userPreludePathForEnvironment (snapshot: (string * string)[]) : string option =
    userPreludePathFrom (mapValue snapshot)

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
