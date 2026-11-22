module Fsion.Tests.LifecycleTests

open System
open System.IO
open System.Threading
open Xunit
open Fsion.Tests.TestInfra

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
        let first = context.Run([ "--no-daemon"; "-e"; "printfn \"oneshot\"" ], timeoutMs = 120000)
        Assert.Equal(0, first.ExitCode)
        Assert.Equal("oneshot" + Environment.NewLine, first.Stdout)
        Assert.True((context.DaemonPid()).IsNone, "no-daemon left a background daemon behind")

        let second = context.Run([ "--no-daemon"; "-e"; "printfn \"again\"" ], timeoutMs = 120000)
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
