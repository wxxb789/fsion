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

let userPreludePath () : string option =
    let configDirectory =
        match environment ConfigDirectoryEnvironmentVariable with
        | Some directory -> directory
        | None -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "fsion")

    let candidate = Path.GetFullPath(Path.Combine(configDirectory, "prelude.fsx"))

    if File.Exists candidate then Some candidate else None

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
