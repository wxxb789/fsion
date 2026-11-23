module Fsion.Tests.TestInfra

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open Fsion
open Fsion.Protocol

[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()

let isWindows = OperatingSystem.IsWindows()

let private findRepoRoot () =
    let rec walk (directory: DirectoryInfo) =
        if isNull directory then
            failwith "could not locate the fsion repository root"
        elif File.Exists(Path.Combine(directory.FullName, "fsion.slnx")) then
            directory.FullName
        else
            walk directory.Parent

    walk (DirectoryInfo AppContext.BaseDirectory)

let repoRoot = findRepoRoot ()

let configuration =
    let baseDirectory = AppContext.BaseDirectory
    let separator = Path.DirectorySeparatorChar

    if baseDirectory.Contains(string separator + "Release" + string separator) then
        "Release"
    else
        "Debug"

let targetFramework =
    let version = Environment.Version
    "net" + string version.Major + "." + string version.Minor

let fsionBinary =
    match Environment.GetEnvironmentVariable "FSION_BINARY" with
    | null | "" ->
        Path.Combine(
            repoRoot,
            "artifacts",
            "publish",
            "fsion",
            targetFramework,
            if isWindows then "fsion.exe" else "fsion"
        )
    | value -> value

let fsiondBinary =
    Path.Combine(
        repoRoot,
        "src",
        "fsiond",
        "bin",
        configuration,
        targetFramework,
        if isWindows then "fsiond.exe" else "fsiond"
    )

type ProcessResult =
    { ExitCode: int
      Stdout: string
      Stderr: string
      DurationMs: float }

let runProcess
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string)
    (environment: (string * string) list)
    (stdin: string option)
    (timeoutMs: int)
    : ProcessResult =
    let startInfo = ProcessStartInfo(fileName)
    startInfo.WorkingDirectory <- workingDirectory
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.RedirectStandardInput <- true
    startInfo.StandardInputEncoding <- UTF8Encoding(false)
    startInfo.StandardOutputEncoding <- UTF8Encoding(false)
    startInfo.StandardErrorEncoding <- UTF8Encoding(false)

    for argument in arguments do
        startInfo.ArgumentList.Add argument

    for key, value in environment do
        startInfo.Environment.[key] <- value

    let stopwatch = Stopwatch.StartNew()
    use childProcess = Process.Start startInfo

    if isNull childProcess then
        failwith ("failed to start " + fileName)

    let stdoutTask = childProcess.StandardOutput.ReadToEndAsync()
    let stderrTask = childProcess.StandardError.ReadToEndAsync()

    match stdin with
    | Some text -> childProcess.StandardInput.Write text
    | None -> ()

    childProcess.StandardInput.Close()

    if not (childProcess.WaitForExit timeoutMs) then
        childProcess.Kill true
        childProcess.WaitForExit()
        failwith ("process timed out: " + fileName + " " + String.Join(" ", arguments))

    childProcess.WaitForExit()
    stopwatch.Stop()

    { ExitCode = childProcess.ExitCode
      Stdout = stdoutTask.GetAwaiter().GetResult()
      Stderr = stderrTask.GetAwaiter().GetResult()
      DurationMs = stopwatch.Elapsed.TotalMilliseconds }

let runFsi (workingDirectory: string) (arguments: string list) (stdin: string option) : ProcessResult =
    runProcess "dotnet" ([ "fsi"; "--nologo" ] @ arguments) workingDirectory [] stdin 180000

let createTempDir (prefix: string) =
    let directory =
        Path.Combine(Path.GetTempPath(), "fsion-tests", prefix + "-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory directory |> ignore
    directory

let tryDeleteDirectory (directory: string) =
    try
        if Directory.Exists directory then
            Directory.Delete(directory, true)
    with _ ->
        ()

let writeFile (directory: string) (name: string) (content: string) =
    let path = Path.Combine(directory, name)
    let parent = Path.GetDirectoryName path

    if not (String.IsNullOrEmpty parent) then
        Directory.CreateDirectory parent |> ignore

    File.WriteAllText(path, content, UTF8Encoding(false))

let normalizeGeneratedNames (text: string) =
    let withoutModules = Regex.Replace(text, @"FSI_\d+(_[A-Za-z0-9_$]+)?", "FSI_X")
    Regex.Replace(withoutModules, @" in [^\r\n]+:line \d+", "")

type FsionContext(workingDirectory: string) =
    let endpoint = "fsion-test-" + Guid.NewGuid().ToString("N")

    let baseEnvironment =
        [ "FSION_PIPE", endpoint
          "FSIOND_PATH", fsiondBinary ]

    let mutable disposed = false

    member _.Run(args: string list, ?stdin: string, ?timeoutMs: int, ?extraEnv: (string * string) list) =
        let environment = baseEnvironment @ (defaultArg extraEnv [])
        runProcess fsionBinary args workingDirectory environment stdin (defaultArg timeoutMs 90000)

    member _.Environment = baseEnvironment

    member _.Metrics(timeoutMs: int) : MetricsInfo option =
        match Ipc.tryConnect { Name = endpoint } (TimeSpan.FromMilliseconds(float timeoutMs)) with
        | None -> None
        | Some stream ->
            use connection = stream
            sendRequest connection GetMetrics

            match tryReceiveFrame connection with
            | Some payload ->
                match parseResponse payload with
                | Metrics metrics -> Some metrics
                | _ -> None
            | None -> None

    member _.DaemonPid() : int option =
        let result = runProcess fsionBinary [ "daemon"; "status" ] workingDirectory baseEnvironment None 30000
        let matched = Regex.Match(result.Stdout, "pid=(\\d+)")

        if matched.Success then
            Some(int matched.Groups.[1].Value)
        else
            None

    member this.StopOwnDaemon() =
        if not disposed then
            disposed <- true
            let pid = this.DaemonPid()

            match pid with
            | None -> ()
            | Some daemonPid ->
                runProcess fsionBinary [ "daemon"; "stop" ] workingDirectory baseEnvironment None 20000
                |> ignore

                let deadline = DateTime.UtcNow.AddSeconds 10.0
                let mutable alive = true

                while alive && DateTime.UtcNow < deadline do
                    alive <- this.DaemonPid().IsSome

                    if alive then
                        Thread.Sleep 100

                if alive then
                    try
                        let daemon = Process.GetProcessById daemonPid
                        daemon.Kill true
                        daemon.WaitForExit 5000 |> ignore
                    with _ ->
                        ()

    interface IDisposable with
        member this.Dispose() = this.StopOwnDaemon()

let withContext (prefix: string) (body: FsionContext -> string -> unit) =
    let workingDirectory = createTempDir prefix
    let context = new FsionContext(workingDirectory)

    try
        body context workingDirectory
    finally
        (context :> IDisposable).Dispose()
        tryDeleteDirectory workingDirectory
