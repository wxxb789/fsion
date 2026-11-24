module Fsion.Tests.LifecycleTests

open System
open System.IO
open System.Threading
open Xunit
open Fsion
open Fsion.Tests.TestInfra

[<Fact>]
let ``connect raises TimeoutException when an endpoint never comes up`` () =
    let endpoint: Ipc.Endpoint = { Name = "fsion-nonexistent-" + Guid.NewGuid().ToString("N") }
    let started = DateTime.UtcNow

    let thrown =
        Assert.Throws<TimeoutException>(fun () ->
            Ipc.connect endpoint (TimeSpan.FromMilliseconds 120.0) |> ignore)

    Assert.Contains("is not reachable", thrown.Message)
    Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds 5.0, "connect did not stop at its deadline")

[<Fact>]
let ``auto-start serves scripts and keeps the same daemon across invocations`` () =
    withContext "lifecycle" (fun context _ ->
        let first = context.Run [ "-e"; "printfn \"first\"" ]
        Assert.Equal(0, first.ExitCode)
        Assert.Equal("first" + Environment.NewLine, first.Stdout)
        let firstPid = context.DaemonPid()
        Assert.True(firstPid.IsSome, "daemon was not reported as running")

        let second = context.Run [ "-e"; "printfn \"second\"" ]
        Assert.Equal(0, second.ExitCode)
        Assert.Equal("second" + Environment.NewLine, second.Stdout)
        Assert.Equal(firstPid, context.DaemonPid()))

[<Fact>]
let ``warmup starts a ready daemon and the next run is warm`` () =
    withContext "lifecycle" (fun context _ ->
        let warmup = context.Run([ "warmup" ], timeoutMs = 120000)
        Assert.Equal(0, warmup.ExitCode)
        Assert.Contains("fsiond ready", warmup.Stdout)
        Assert.Contains("warm=True", warmup.Stdout)
        Assert.True((context.DaemonPid()).IsSome, "daemon was not reported as running")

        let run = context.Run([ "-e"; "printfn \"warmed\"" ], timeoutMs = 30000)
        Assert.Equal(0, run.ExitCode)
        Assert.Equal("warmed" + Environment.NewLine, run.Stdout))

[<Fact>]
let ``daemon stop is reversible and the next invocation auto-starts a new daemon`` () =
    withContext "lifecycle" (fun context _ ->
        let first = context.Run [ "-e"; "printfn \"a\"" ]
        Assert.Equal(0, first.ExitCode)
        let firstPid = context.DaemonPid()
        Assert.True(firstPid.IsSome, "daemon was not reported as running")

        let stop = context.Run [ "daemon"; "stop" ]
        Assert.Equal(0, stop.ExitCode)
        Assert.Contains("fsiond stopped", stop.Stdout)
        Assert.True((context.DaemonPid()).IsNone, "daemon was still running after stop")

        let second = context.Run [ "-e"; "printfn \"b\"" ]
        Assert.Equal(0, second.ExitCode)
        let secondPid = context.DaemonPid()
        Assert.True(secondPid.IsSome, "daemon was not restarted")
        Assert.NotEqual(firstPid, secondPid))

[<Fact>]
let ``daemon restart replaces the process and serves scripts afterwards`` () =
    withContext "lifecycle" (fun context _ ->
        let first = context.Run [ "-e"; "printfn \"before\"" ]
        Assert.Equal(0, first.ExitCode)
        let firstPid = context.DaemonPid()
        Assert.True(firstPid.IsSome, "daemon was not reported as running")

        let restart = context.Run([ "daemon"; "restart" ], timeoutMs = 120000)
        Assert.Equal(0, restart.ExitCode)
        let secondPid = context.DaemonPid()
        Assert.True(secondPid.IsSome, "daemon was not running after restart")
        Assert.NotEqual(firstPid, secondPid)

        let after = context.Run([ "-e"; "printfn \"after\"" ], timeoutMs = 60000)
        Assert.Equal(0, after.ExitCode)
        Assert.Equal("after" + Environment.NewLine, after.Stdout)
        Assert.Equal(secondPid, context.DaemonPid()))

[<Fact>]
let ``no-daemon runs a single-shot daemon and leaves no background endpoint`` () =
    withContext "lifecycle" (fun context _ ->
        let oneshotEndpoint = "fsion-test-oneshot-" + Guid.NewGuid().ToString("N")
        let oneshotEnvironment = [ "FSION_ONESHOT_PIPE", oneshotEndpoint ]
        let code = "printfn \"oneshot\"\nSystem.Threading.Thread.Sleep 3000\n"

        let firstTask =
            System.Threading.Tasks.Task.Run(fun () ->
                context.Run([ "--no-daemon"; "-e"; code ], timeoutMs = 120000, extraEnv = oneshotEnvironment))

        let reachableDeadline = DateTime.UtcNow.AddSeconds 30.0
        let mutable sawOneshotRunning = false

        while not sawOneshotRunning && DateTime.UtcNow < reachableDeadline do
            let status =
                context.Run([ "--pipe"; oneshotEndpoint; "daemon"; "status" ], timeoutMs = 30000)

            sawOneshotRunning <- status.Stdout.Contains "fsiond running"

            if not sawOneshotRunning then
                Thread.Sleep 100

        let first = firstTask.Result
        Assert.True(sawOneshotRunning, "the single-shot fsiond was never reachable on " + oneshotEndpoint)
        Assert.Equal(0, first.ExitCode)
        Assert.Equal("oneshot" + Environment.NewLine, first.Stdout)
        Assert.True((context.DaemonPid()).IsNone, "no-daemon left a background daemon behind")

        let exitDeadline = DateTime.UtcNow.AddSeconds 3.0
        let mutable reachable = true

        while reachable && DateTime.UtcNow < exitDeadline do
            let status =
                context.Run([ "--pipe"; oneshotEndpoint; "daemon"; "status" ], timeoutMs = 30000)

            reachable <- status.Stdout.Contains "fsiond running"

            if reachable then
                Thread.Sleep 100

        Assert.False(reachable, "the single-shot fsiond was still reachable on " + oneshotEndpoint)

        let second =
            context.Run(
                [ "--no-daemon"; "-e"; "printfn \"again\"" ],
                timeoutMs = 120000,
                extraEnv = oneshotEnvironment
            )

        Assert.Equal(0, second.ExitCode)
        Assert.Equal("again" + Environment.NewLine, second.Stdout))

[<Fact>]
let ``idle ttl retires an auto-started daemon`` () =
    withContext "lifecycle" (fun context _ ->
        let warmup = context.Run([ "--idle-ttl"; "0.05"; "warmup" ], timeoutMs = 120000)
        Assert.Equal(0, warmup.ExitCode)
        Assert.True((context.DaemonPid()).IsSome, "daemon was not reported as running")

        let deadline = DateTime.UtcNow.AddSeconds 30.0
        let mutable running = true

        while running && DateTime.UtcNow < deadline do
            Thread.Sleep 500
            running <- (context.DaemonPid()).IsSome

        Assert.False(running, "daemon did not exit after the idle TTL elapsed"))

let private readAllTextEventually (path: string) =
    let deadline = DateTime.UtcNow.AddSeconds 10.0
    let mutable text = ""
    let mutable complete = false

    while not complete && DateTime.UtcNow < deadline do
        try
            text <- File.ReadAllText path
            complete <- true
        with :? IOException ->
            Thread.Sleep 100

    text

[<Fact>]
let ``daemon log path containing spaces is passed to fsiond intact`` () =
    withContext "lifecycle-log-path" (fun context directory ->
        let logDirectory = Path.Combine(directory, "dir with spaces")
        Directory.CreateDirectory logDirectory |> ignore
        let logPath = Path.Combine(logDirectory, "daemon.log")

        let run =
            context.Run([ "-e"; "printfn \"logged\"" ], extraEnv = [ "FSIOND_LOG", logPath ])

        Assert.Equal(0, run.ExitCode)
        Assert.Equal("logged" + Environment.NewLine, run.Stdout)
        Assert.True(File.Exists logPath, "fsiond did not write its log to the spaced path")
        Assert.Contains("listening on", readAllTextEventually logPath))

[<Fact>]
let ``--pipe overrides FSION_PIPE and its daemon is stopped afterwards`` () =
    withContext "lifecycle-pipe-override" (fun context _ ->
        let overrideEndpoint = "fsion-test-override-" + Guid.NewGuid().ToString("N")

        try
            let run = context.Run [ "--pipe"; overrideEndpoint; "-e"; "printfn \"override\"" ]
            Assert.Equal(0, run.ExitCode)
            Assert.Equal("override" + Environment.NewLine, run.Stdout)

            let status = context.Run [ "--pipe"; overrideEndpoint; "daemon"; "status" ]
            Assert.Contains("fsiond running", status.Stdout)
            Assert.Contains(overrideEndpoint, status.Stdout)
            Assert.True((context.DaemonPid()).IsNone, "FSION_PIPE endpoint was started despite --pipe")
        finally
            context.Run([ "--pipe"; overrideEndpoint; "daemon"; "stop" ], timeoutMs = 30000)
            |> ignore)

[<Fact>]
let ``--pipe without a value fails before contacting any daemon`` () =
    withContext "lifecycle-pipe-missing" (fun context _ ->
        let run = context.Run [ "--pipe" ]
        Assert.NotEqual(0, run.ExitCode)
        Assert.Contains("--pipe", run.Stderr)
        Assert.True((context.DaemonPid()).IsNone, "a daemon was started for an invalid command line"))

[<Fact>]
let ``--pipe with an empty value fails fast without contacting any daemon`` () =
    withContext "lifecycle-pipe-empty" (fun context _ ->
        let run = context.Run [ "--pipe"; "" ]
        Assert.NotEqual(0, run.ExitCode)
        Assert.Contains("--pipe", run.Stderr)
        Assert.True(run.DurationMs < 10000.0, "--pipe with an empty value did not fail fast")
        Assert.True((context.DaemonPid()).IsNone, "a daemon was started for an empty pipe name"))

[<Fact>]
let ``--pipe with path separators fails fast without contacting any daemon`` () =
    withContext "lifecycle-pipe-separators" (fun context _ ->
        for badName in [ "bad\\name"; "bad/name" ] do
            let run = context.Run [ "--pipe"; badName; "daemon"; "status" ]
            Assert.NotEqual(0, run.ExitCode)
            Assert.Contains("path separators", run.Stderr)
            Assert.True(run.DurationMs < 10000.0, "--pipe " + badName + " did not fail fast")
            Assert.True((context.DaemonPid()).IsNone, "a daemon was started for an invalid pipe name"))

[<Fact>]
let ``fsion discovers fsiond next to its own binary without FSIOND_PATH`` () =
    let deployDirectory = createTempDir "colocated-bin"
    let workingDirectory = createTempDir "colocated-work"

    try
        let clientName = Path.GetFileName fsionBinary
        let deployedClient = Path.Combine(deployDirectory, clientName)
        File.Copy(fsionBinary, deployedClient, true)

        for file in Directory.EnumerateFiles(Path.GetDirectoryName fsiondBinary) do
            File.Copy(file, Path.Combine(deployDirectory, Path.GetFileName file), true)

        let endpoint = "fsion-test-" + Guid.NewGuid().ToString("N")
        let environment = [ "FSION_PIPE", endpoint ]

        let run =
            runProcess deployedClient [ "-e"; "printfn \"colocated\"" ] workingDirectory environment None 120000

        Assert.Equal(0, run.ExitCode)
        Assert.Equal("colocated" + Environment.NewLine, run.Stdout)

        runProcess deployedClient [ "daemon"; "stop" ] workingDirectory environment None 30000
        |> ignore
    finally
        tryDeleteDirectory deployDirectory
        tryDeleteDirectory workingDirectory

[<Fact>]
let ``defaultEndpointName ignores USERNAME and USER environment overrides`` () =
    let workingDirectory = createTempDir "endpoint-user-override"

    try
        let envVar = if OperatingSystem.IsWindows() then "USERNAME" else "USER"
        let first =
            runProcess
                fsionBinary
                [ "daemon"; "status" ]
                workingDirectory
                [ "FSION_PIPE", null; envVar, "spoofed-alpha" ]
                None
                30000

        let second =
            runProcess
                fsionBinary
                [ "daemon"; "status" ]
                workingDirectory
                [ "FSION_PIPE", null; envVar, "spoofed-beta" ]
                None
                30000

        Assert.Equal(0, first.ExitCode)
        Assert.Equal(0, second.ExitCode)
        Assert.Equal(first.Stdout, second.Stdout)
        Assert.DoesNotContain("spoofed-alpha", first.Stdout)
        Assert.DoesNotContain("spoofed-beta", second.Stdout)
    finally
        tryDeleteDirectory workingDirectory
