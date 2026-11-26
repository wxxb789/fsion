module Fsion.Tests.RecoveryTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open Xunit
open Xunit.Abstractions
open Fsion.Tests.TestInfra

let private processGone (pid: int) (timeoutMs: int) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable gone = false

    while not gone && DateTime.UtcNow < deadline do
        try
            let candidate = Process.GetProcessById pid

            if candidate.HasExited then
                gone <- true
            else
                Thread.Sleep 100
        with :? ArgumentException ->
            gone <- true

    gone

type RecoveryTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.``cooperative async timeout cancels in place with exit 124 and keeps the daemon alive`` () =
        let cooperative = "Async.RunSynchronously(Async.Sleep 10000, cancellationToken = fsionFsiState.CancellationToken)"

        withContext "recovery-cooperative" (fun context _ ->
            let prime = context.Run([ "-e"; "printfn \"prime\"" ], timeoutMs = 120000)
            Assert.Equal(0, prime.ExitCode)
            let pid = context.DaemonPid()
            Assert.True(pid.IsSome, "daemon was not reported as running")

            let timed = context.Run([ "--timeout"; "300"; "-e"; cooperative ], timeoutMs = 60000)
            output.WriteLine("cooperative timeout wall time (ms): " + string timed.DurationMs)
            Assert.Equal(124, timed.ExitCode)
            Assert.Contains("timed out after 300 ms", timed.Stderr)
            Assert.True(timed.DurationMs < 5000.0, "cooperative timeout took " + string timed.DurationMs + " ms")
            Assert.Equal(pid, context.DaemonPid())

            let json = context.Run([ "--json"; "--timeout"; "300"; "-e"; cooperative ], timeoutMs = 60000)
            Assert.Equal(124, json.ExitCode)
            use document = JsonDocument.Parse json.Stdout
            Assert.True(document.RootElement.GetProperty("timedOut").GetBoolean())
            Assert.Equal(124, document.RootElement.GetProperty("exitCode").GetInt32())
            Assert.Equal(pid, context.DaemonPid())

            let after = context.Run([ "-e"; "printfn \"alive\"" ], timeoutMs = 60000)
            Assert.Equal(0, after.ExitCode)
            Assert.Equal("alive" + Environment.NewLine, after.Stdout)
            Assert.Equal(pid, context.DaemonPid()))

    [<Fact>]
    member _.``uncooperative infinite loop drains the daemon and the next call starts a fresh generation`` () =
        withContext "recovery-wedge" (fun context _ ->
            let prime = context.Run([ "-e"; "printfn \"prime\"" ], timeoutMs = 120000)
            Assert.Equal(0, prime.ExitCode)
            let oldPid = context.DaemonPid()
            Assert.True(oldPid.IsSome, "daemon was not reported as running")

            let timed = context.Run([ "--timeout"; "400"; "-e"; "while true do ()" ], timeoutMs = 60000)
            output.WriteLine("wedge timeout wall time (ms): " + string timed.DurationMs)
            Assert.Equal(124, timed.ExitCode)
            Assert.Contains("timed out after 400 ms", timed.Stderr)
            Assert.True(timed.DurationMs < 4000.0, "wedged timeout took " + string timed.DurationMs + " ms")
            Assert.True(
                processGone oldPid.Value 15000,
                "the drained daemon (pid " + string oldPid.Value + ") did not terminate"
            )

            let next = context.Run([ "-e"; "printfn \"%d\" (1 + 1)" ], timeoutMs = 120000)
            Assert.Equal(0, next.ExitCode)
            Assert.Equal("2" + Environment.NewLine, next.Stdout)
            let newPid = context.DaemonPid()
            Assert.True(newPid.IsSome, "no daemon served the call after the drained generation")
            Assert.NotEqual(oldPid, newPid))

    [<Fact>]
    member _.``a replacement daemon cold prelude does not inherit the wedged script environment`` () =
        withContext "recovery-wedge-environment" (fun context directory ->
            let cleanEnvironment = [ "FSION_WEDGE_LEAK", null ]
            let prime = context.Run([ "--pool-size"; "0"; "-e"; "()" ], extraEnv = cleanEnvironment)
            Assert.Equal(0, prime.ExitCode)
            let oldPid = context.DaemonPid()
            Assert.True(oldPid.IsSome, "daemon was not reported as running")

            let wedge = """System.Environment.SetEnvironmentVariable("FSION_WEDGE_LEAK", "leaked"); while true do ()"""
            let timed = context.Run([ "--timeout"; "1200"; "-e"; wedge ], extraEnv = cleanEnvironment)
            Assert.Equal(124, timed.ExitCode)
            Assert.True(processGone oldPid.Value 15000, "the drained daemon did not terminate")
            let replacement = context.DaemonPid()
            Assert.True(replacement.IsSome, "no replacement daemon appeared without a client auto-start")
            Assert.NotEqual(oldPid, replacement)

            writeFile directory "prelude.fsx"
                """let wedgeProbe = System.Environment.GetEnvironmentVariable("FSION_WEDGE_LEAK") |> Option.ofObj"""
            let prelude = Path.Combine(directory, "prelude.fsx")
            let next =
                context.Run(
                    [ "--pool-size"; "0"; "--prelude"; prelude; "-e"; """System.Console.Write(defaultArg wedgeProbe "<null>")""" ],
                    extraEnv = cleanEnvironment
                )
            Assert.True(next.ExitCode = 0, next.Stderr)
            Assert.Equal(replacement, context.DaemonPid())
            Assert.Equal("<null>", next.Stdout)

            let bootMarker = Path.Combine(directory, "boot-prelude-runs.txt")
            let bootPrelude = Path.Combine(directory, "boot-prelude.fsx")
            writeFile
                directory
                "boot-prelude.fsx"
                (sprintf "System.IO.File.AppendAllText(@\"%s\", \"run\\n\")\nlet bootVal = 1\n" bootMarker)
            let bootPipe = "fsion-test-bootp-" + Guid.NewGuid().ToString("N")
            let bootStartEnv =
                [ "FSION_PIPE", bootPipe
                  "FSIOND_PATH", fsiondBinary
                  "FSION_CONFIG_DIR", Path.Combine(directory, ".fsion-bootp-config")
                  "FSION_PRELUDE", bootPrelude ]
            let bootClearEnv =
                [ "FSION_PIPE", bootPipe
                  "FSIOND_PATH", fsiondBinary
                  "FSION_CONFIG_DIR", Path.Combine(directory, ".fsion-bootp-config")
                  "FSION_PRELUDE", null ]
            try
                let bootWarm = runProcess fsionBinary [ "--pool-size"; "1"; "warmup" ] directory bootStartEnv None 120000
                Assert.True(bootWarm.ExitCode = 0, bootWarm.Stderr)
                let bootCountBefore = File.ReadAllLines(bootMarker).Length
                Assert.True(bootCountBefore >= 1)
                let wedgeBuiltin =
                    runProcess fsionBinary [ "--timeout"; "500"; "-e"; "while true do ()" ] directory bootClearEnv None 60000
                Assert.Equal(124, wedgeBuiltin.ExitCode)
                let afterBuiltin =
                    runProcess fsionBinary [ "-e"; "System.Console.Write(\"builtin-ok\")" ] directory bootClearEnv None 120000
                Assert.True(afterBuiltin.ExitCode = 0, afterBuiltin.Stderr)
                Assert.Equal("builtin-ok", afterBuiltin.Stdout)
                Thread.Sleep 800
                Assert.Equal(bootCountBefore, File.ReadAllLines(bootMarker).Length)
            finally
                runProcess fsionBinary [ "daemon"; "stop" ] directory bootClearEnv None 20000 |> ignore)

    [<Fact>]
    member _.``client disconnect cancels a cooperative wait without retiring the daemon`` () =
        withContext "recovery-disconnect" (fun context directory ->
            let marker = Path.Combine(directory, "started.txt")

            let prime = context.Run([ "-e"; "printfn \"prime\"" ], timeoutMs = 120000)
            Assert.Equal(0, prime.ExitCode)
            let pid = context.DaemonPid()
            Assert.True(pid.IsSome, "daemon was not reported as running")

            let code =
                "System.IO.File.WriteAllText(@\""
                + marker.Replace("\\", "\\\\")
                + "\", \"1\")\nAsync.RunSynchronously(Async.Sleep 60000, cancellationToken = fsionFsiState.CancellationToken)"

            let startInfo = ProcessStartInfo(fsionBinary)
            startInfo.WorkingDirectory <- directory
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.RedirectStandardInput <- true

            for key, value in context.Environment do
                startInfo.Environment.[key] <- value

            for argument in [ "--session"; "alpha"; "-e"; code ] do
                startInfo.ArgumentList.Add argument

            let client = Process.Start startInfo
            Assert.False(isNull client, "could not start the fsion client")

            try
                let markerDeadline = DateTime.UtcNow.AddSeconds(60.0)
                let mutable started = false

                while not started && DateTime.UtcNow < markerDeadline do
                    started <- File.Exists marker

                    if not started then
                        Thread.Sleep 200

                Assert.True(started, "the named session turn never started")
                Thread.Sleep 500
                client.Kill true
                client.WaitForExit()
                Assert.True(client.HasExited, "the fsion client did not exit after being killed")

                Thread.Sleep 1500
                Assert.Equal(pid, context.DaemonPid())

                let after =
                    context.Run([ "--session"; "alpha"; "--timeout"; "10000"; "-e"; "printfn \"ok\"" ], timeoutMs = 60000)

                Assert.Equal(0, after.ExitCode)
                Assert.Equal("ok" + Environment.NewLine, after.Stdout)
                Assert.Equal(pid, context.DaemonPid())
            finally
                if not client.HasExited then
                    client.Kill true)

    [<Fact>]
    member _.``a short --timeout on a freshly auto-started cold daemon runs a fast script without draining the daemon`` () =
        withContext "recovery-cold-timeout" (fun context _ ->
            let first =
                context.Run([ "--timeout"; "500"; "-e"; "printfn \"%d\" (1 + 1)" ], timeoutMs = 120000)

            Assert.Equal(0, first.ExitCode)
            Assert.Equal("2" + Environment.NewLine, first.Stdout)
            let pid1 = context.DaemonPid()
            Assert.True(pid1.IsSome, "daemon was not reported as running")

            let second = context.Run([ "-e"; "printfn \"%d\" (3 + 4)" ], timeoutMs = 120000)
            Assert.Equal(0, second.ExitCode)
            Assert.Equal("7" + Environment.NewLine, second.Stdout)
            Assert.Equal(pid1, context.DaemonPid()))

    [<Fact>]
    member _.``a --timeout waiting behind a busy named session cancels in place without draining the daemon`` () =
        withContext "recovery-session-queue" (fun context directory ->
            let prime = context.Run([ "-e"; "printfn \"prime\"" ], timeoutMs = 120000)
            Assert.Equal(0, prime.ExitCode)
            let pid1 = context.DaemonPid()
            Assert.True(pid1.IsSome, "daemon was not reported as running")

            let marker = Path.Combine(directory, "busy-started.txt")

            let code =
                "System.IO.File.WriteAllText(@\""
                + marker.Replace("\\", "\\\\")
                + "\", \"1\")\nAsync.RunSynchronously(Async.Sleep 3000, cancellationToken = fsionFsiState.CancellationToken)"

            let startInfo = ProcessStartInfo(fsionBinary)
            startInfo.WorkingDirectory <- directory
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.RedirectStandardInput <- true

            for key, value in context.Environment do
                startInfo.Environment.[key] <- value

            for argument in [ "--session"; "alpha"; "-e"; code ] do
                startInfo.ArgumentList.Add argument

            let busyturn = Process.Start startInfo
            Assert.False(isNull busyturn, "could not start the busy fsion client")

            try
                let markerDeadline = DateTime.UtcNow.AddSeconds(60.0)
                let mutable started = false

                while not started && DateTime.UtcNow < markerDeadline do
                    started <- File.Exists marker

                    if not started then
                        Thread.Sleep 200

                Assert.True(started, "the busy named-session turn never started")
                Thread.Sleep 300

                let queued =
                    context.Run(
                        [ "--session"; "alpha"; "--timeout"; "250"; "-e"; "printfn \"queued\"" ],
                        timeoutMs = 60000
                    )

                output.WriteLine("queued timeout wall time (ms): " + string queued.DurationMs)
                Assert.Equal(124, queued.ExitCode)
                Assert.Contains("timed out after 250 ms", queued.Stderr)
                Assert.True(queued.DurationMs < 5000.0, "queued timeout took " + string queued.DurationMs + " ms")

                Assert.True(busyturn.WaitForExit 60000, "the busy named-session turn did not finish")
                Assert.Equal(0, busyturn.ExitCode)
                Assert.Equal(pid1, context.DaemonPid())
            finally
                if not busyturn.HasExited then
                    busyturn.Kill true)
