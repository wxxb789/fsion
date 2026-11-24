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

let private specialFolder (variable: string) folder =
    match environment variable with
    | Some directory -> directory
    | None -> Environment.GetFolderPath folder

let userPreludePath () : string option =
    let configDirectory =
        match environment ConfigDirectoryEnvironmentVariable with
        | Some directory -> directory
        | None ->
            let home =
                if OperatingSystem.IsWindows() then
                    specialFolder "APPDATA" Environment.SpecialFolder.ApplicationData
                else
                    specialFolder "XDG_CONFIG_HOME" Environment.SpecialFolder.ApplicationData

            Path.Combine(home, "fsion")

    let candidate = Path.GetFullPath(Path.Combine(configDirectory, "prelude.fsx"))

    if File.Exists candidate then Some candidate else None

let cacheDirectory () : string =
    match environment CacheDirectoryEnvironmentVariable with
    | Some directory -> directory
    | None ->
        let baseDirectory =
            if OperatingSystem.IsWindows() then
                specialFolder "LOCALAPPDATA" Environment.SpecialFolder.LocalApplicationData
            else
                match environment "XDG_CACHE_HOME" with
                | Some directory -> directory
                | None -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".cache")

        Path.Combine(baseDirectory, "fsion", "nuget-cache")
