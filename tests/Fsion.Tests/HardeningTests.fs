module Fsion.Tests.HardeningTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open Fsion
open Fsion.Ipc
open Fsion.Protocol
open Fsion.Tests.TestInfra

let private processGone (pid: int) (timeoutMs: int) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable gone = false

    while not gone && DateTime.UtcNow < deadline do
        try
            let candidate = Process.GetProcessById pid
            if candidate.HasExited then gone <- true else Thread.Sleep 100
        with :? ArgumentException ->
            gone <- true

    gone

let private waitFor (message: string) (condition: unit -> bool) =
    Assert.True(SpinWait.SpinUntil(Func<bool>(condition), TimeSpan.FromSeconds 30.0), message)

type HardeningTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.``default endpoint incorporates TFM and build fingerprint and control commands tolerate protocol version skew`` () =
        let defName = defaultEndpointName ()
        Assert.Contains(targetFrameworkMoniker, defName)
        Assert.Contains(buildFingerprint, defName)

        // Simulate a version-mismatched peer (protocol version 999) on an explicit --pipe:
        // fsion daemon status and fsion daemon stop must succeed cleanly instead of failing on version skew.
        let endpoint = { Name = "fsion-skew-" + Guid.NewGuid().ToString("N") }
        use listener = createListener endpoint
        let mutable stopSeen = 0
        let serverTask =
            Task.Run(fun () ->
                let mutable running = true
                while running do
                    try
                        use stream = listener.Accept().GetAwaiter().GetResult()
                        match tryReceiveFrame stream with
                        | None -> ()
                        | Some payload ->
                            use ms = new MemoryStream(payload)
                            use reader = new BinaryReader(ms, Encoding.UTF8)
                            let _clientVersion = reader.ReadInt32()
                            let kind = reader.ReadByte()
                            use outMs = new MemoryStream()
                            use writer = new BinaryWriter(outMs, Encoding.UTF8, true)
                            writer.Write 999 // mismatched protocol version
                            match kind with
                            | 2uy -> // GetStatus
                                writer.Write 12uy
                                writer.Write 424242
                                writer.Write endpoint.Name
                                writer.Write 100L
                                writer.Write 10L
                                writer.Write 7
                                writer.Write true
                                writer.Write 999
                                writer.Write "net99.0"
                                writer.Write "p999-net99.0"
                                writer.Write 4
                                writer.Write 30.0
                                writer.Write 15.0
                                writer.Write "gen-skew"
                            | 3uy -> // Shutdown
                                writer.Write 13uy
                                Interlocked.Exchange(&stopSeen, 1) |> ignore
                                running <- false
                            | _ ->
                                writer.Write 15uy
                                writer.Write "unsupported protocol version"
                            writer.Flush()
                            let respPayload = outMs.ToArray()
                            use frameMs = new MemoryStream()
                            use frameWriter = new BinaryWriter(frameMs, Encoding.UTF8, true)
                            frameWriter.Write respPayload.Length
                            frameWriter.Write respPayload
                            frameWriter.Flush()
                            let frame = frameMs.ToArray()
                            stream.Write(frame, 0, frame.Length)
                            stream.Flush()
                            if not running then
                                listener.Stop()
                    with _ ->
                        running <- false)

        try
            withContext "skew-client" (fun _ directory ->
                let env = [ "FSION_PIPE", endpoint.Name; "FSIOND_PATH", fsiondBinary ]
                let status = runProcess fsionBinary [ "daemon"; "status" ] directory env None 30000
                Assert.True(status.ExitCode = 0, status.Stderr)
                Assert.Contains("pid=424242", status.Stdout)
                Assert.Contains("protocol=999", status.Stdout)
                Assert.Contains("tfm=net99.0", status.Stdout)

                let stop = runProcess fsionBinary [ "daemon"; "stop" ] directory env None 30000
                Assert.True(stop.ExitCode = 0, stop.Stderr)
                Assert.Contains("fsiond stopped", stop.Stdout)
                Assert.Equal(1, Volatile.Read(&stopSeen)))
        finally
            listener.Stop()
            serverTask.Wait 3000 |> ignore

    [<Fact>]
    member _.``IPC permissions enforce owner-only access and detached POSIX daemon survives caller process group`` () =
        withContext "ipc-security" (fun context directory ->
            let prime = context.Run([ "-e"; "printfn \"ok\"" ], timeoutMs = 120000)
            Assert.True(prime.ExitCode = 0, prime.Stderr)
            Assert.Equal("ok" + Environment.NewLine, prime.Stdout)
            let status = context.Run([ "daemon"; "status" ], timeoutMs = 30000)
            Assert.True(status.ExitCode = 0, status.Stderr)
            Assert.Contains("tfm=" + targetFrameworkMoniker, status.Stdout)
            Assert.Contains("protocol=" + string ProtocolVersion, status.Stdout)

            if not (OperatingSystem.IsWindows()) then
                let pipeName =
                    context.Environment
                    |> List.pick (fun (k, v) -> if k = "FSION_PIPE" then Some v else None)
                let sock = socketPath { Name = pipeName }
                let dir = Path.GetDirectoryName sock
                let dirMode = File.GetUnixFileMode dir
                let sockMode = File.GetUnixFileMode sock
                output.WriteLine($"POSIX socket dir={dir} mode={dirMode}, sock={sock} mode={sockMode}")
                Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute, dirMode)
                Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite, sockMode))

    [<Fact>]
    member _.``nested same-daemon invocation with conflicting env does not deadlock and FIFO gate prevents starvation`` () =
        withContext "gate-fairness" (fun context directory ->
            let prime = context.Run([ "--pool-size"; "2"; "warmup" ], timeoutMs = 120000)
            Assert.True(prime.ExitCode = 0, prime.Stderr)
            let daemonPid = context.DaemonPid()
            Assert.True(daemonPid.IsSome)

            // 1a. Nested child with --timeout 500 and modified env returns 124 cleanly without wedging daemon.
            let nestedTimedCode =
                sprintf
                    """
open System.Diagnostics
let psi = ProcessStartInfo(@"%s")
psi.UseShellExecute <- false
psi.RedirectStandardOutput <- true
psi.RedirectStandardError <- true
psi.ArgumentList.Add("--timeout")
psi.ArgumentList.Add("500")
psi.ArgumentList.Add("-e")
psi.ArgumentList.Add("printfn \"child\"")
psi.Environment.["CHILD_VAR"] <- "1"
use p = Process.Start(psi)
let stdout = p.StandardOutput.ReadToEnd()
let stderr = p.StandardError.ReadToEnd()
p.WaitForExit()
printfn "CHILD_EXIT=%%d" p.ExitCode
"""
                    fsionBinary
            let nestedTimed = context.Run([ "-e"; nestedTimedCode ], timeoutMs = 60000)
            Assert.True(nestedTimed.ExitCode = 0, nestedTimed.Stderr)
            Assert.Contains("CHILD_EXIT=124", nestedTimed.Stdout)
            Assert.Equal(daemonPid, context.DaemonPid())
            // 1b. Nested child without --timeout fails fast with diagnostic instead of deadlocking forever.
            let nestedNoTimeoutCode =
                sprintf
                    """
open System.Diagnostics
let psi = ProcessStartInfo(@"%s")
psi.UseShellExecute <- false
psi.RedirectStandardOutput <- true
psi.RedirectStandardError <- true
psi.ArgumentList.Add("-e")
psi.ArgumentList.Add("printfn \"child\"")
psi.Environment.["CHILD_VAR"] <- "1"
use p = Process.Start(psi)
let stdout = p.StandardOutput.ReadToEnd()
let stderr = p.StandardError.ReadToEnd()
p.WaitForExit()
printfn "CHILD_EXIT=%%d|ERR=%%s" p.ExitCode (stderr.Trim())
"""
                    fsionBinary
            let nestedFailFast = context.Run([ "-e"; nestedNoTimeoutCode ], timeoutMs = 60000)
            Assert.True(nestedFailFast.ExitCode = 0, nestedFailFast.Stderr)
            Assert.Contains("CHILD_EXIT=1", nestedFailFast.Stdout)
            Assert.Contains("deadlock", nestedFailFast.Stdout)
            Assert.Equal(daemonPid, context.DaemonPid())

            // 2. FIFO fairness: dir A holds gate; dir B queues; second dir A request queues behind B;
            // B must run BEFORE the second dir A request once A1 releases.
            let dirA = Path.Combine(directory, "dirA")
            let dirB = Path.Combine(directory, "dirB")
            Directory.CreateDirectory dirA |> ignore
            Directory.CreateDirectory dirB |> ignore
            let a1Entered = Path.Combine(directory, "a1-entered.txt")
            let a1Release = Path.Combine(directory, "a1-release.txt")
            let orderLog = Path.Combine(directory, "order.log")

            let a1 =
                Task.Run(fun () ->
                    runProcess
                        fsionBinary
                        [ "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"1\")\nwhile not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20\nSystem.IO.File.AppendAllText(@\"%s\", \"A1\\n\")" a1Entered a1Release orderLog ]
                        dirA
                        context.Environment
                        None
                        60000)

            waitFor "A1 entered gate" (fun () -> File.Exists a1Entered)

            let b1 =
                Task.Run(fun () ->
                    runProcess
                        fsionBinary
                        [ "-e"; sprintf "System.IO.File.AppendAllText(@\"%s\", \"B1\\n\")" orderLog ]
                        dirB
                        context.Environment
                        None
                        60000)

            Thread.Sleep 300

            let a2 =
                Task.Run(fun () ->
                    runProcess
                        fsionBinary
                        [ "-e"; sprintf "System.IO.File.AppendAllText(@\"%s\", \"A2\\n\")" orderLog ]
                        dirA
                        context.Environment
                        None
                        60000)

            Thread.Sleep 300
            File.WriteAllText(a1Release, "go")

            let rA1 = a1.GetAwaiter().GetResult()
            let rB1 = b1.GetAwaiter().GetResult()
            let rA2 = a2.GetAwaiter().GetResult()
            Assert.True(rA1.ExitCode = 0, rA1.Stderr)
            Assert.True(rB1.ExitCode = 0, rB1.Stderr)
            Assert.True(rA2.ExitCode = 0, rA2.Stderr)
            let lines = File.ReadAllLines orderLog
            Assert.Equal("A1|B1|A2", String.Join("|", lines)))
    [<Fact>]
    member _.``wedged timeout does not kill healthy concurrent sibling and reports named session generation loss`` () =
        withContext "non-collateral-drain" (fun context directory ->
            let prime = context.Run([ "--pool-size"; "3"; "warmup" ], timeoutMs = 120000)
            Assert.True(prime.ExitCode = 0, prime.Stderr)
            let oldPid = context.DaemonPid()
            Assert.True(oldPid.IsSome)

            // Establish named session alpha before the wedge.
            let seedAlpha = context.Run([ "--session"; "alpha"; "-e"; "let x = 42\nprintfn \"%d\" x" ], timeoutMs = 60000)
            Assert.True(seedAlpha.ExitCode = 0, seedAlpha.Stderr)
            Assert.Equal("42" + Environment.NewLine, seedAlpha.Stdout)

            // Start healthy cooperative script A (4500ms — well past B's 300ms timeout + 1500ms grace
            // + old 3000ms main drain) and concurrently start wedging script B (--timeout 300).
            let aEntered = Path.Combine(directory, "sibling-a-entered.txt")
            let scriptA =
                Task.Run(fun () ->
                    context.Run(
                        [ "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"1\")\nSystem.Threading.Thread.Sleep 4500\nprintfn \"SIBLING_A_OK\"" aEntered ],
                        timeoutMs = 60000
                    ))

            waitFor "sibling A started" (fun () -> File.Exists aEntered)

            let resB = context.Run([ "--timeout"; "300"; "-e"; "while true do ()" ], timeoutMs = 60000)
            Assert.Equal(124, resB.ExitCode)

            // Script C started right after B times out must be served by the replacement fsiond generation.
            let resC = context.Run([ "-e"; "printfn \"FRESH_C\"" ], timeoutMs = 120000)
            Assert.True(resC.ExitCode = 0, resC.Stderr)
            Assert.Equal("FRESH_C" + Environment.NewLine, resC.Stdout)
            let newPid = context.DaemonPid()
            Assert.True(newPid.IsSome)
            Assert.NotEqual(oldPid, newPid)

            // Healthy sibling A must complete cleanly with exit 0 and full stdout (zero collateral kill).
            let resA = scriptA.GetAwaiter().GetResult()
            Assert.True(resA.ExitCode = 0, "healthy sibling A failed: " + resA.Stderr)
            Assert.Equal("SIBLING_A_OK" + Environment.NewLine, resA.Stdout)
            // Retired wedged daemon terminates once sibling A finishes.
            Assert.True(processGone oldPid.Value 15000, "retired wedged fsiond did not exit after draining sibling A")

            // Named session alpha must explicitly report generation loss instead of silent FS0039.
            let lostAlpha = context.Run([ "--session"; "alpha"; "-e"; "printfn \"%d\" (x + 1)" ], timeoutMs = 60000)
            Assert.NotEqual(0, lostAlpha.ExitCode)
            Assert.Contains("alpha", lostAlpha.Stderr)
            Assert.Contains("--reset", lostAlpha.Stderr)

            // --session alpha --reset clears the tombstone and starts fresh.
            let resetAlpha = context.Run([ "--session"; "alpha"; "--reset"; "-e"; "let x = 99\nprintfn \"%d\" x" ], timeoutMs = 60000)
            Assert.True(resetAlpha.ExitCode = 0, resetAlpha.Stderr)
            Assert.Equal("99" + Environment.NewLine, resetAlpha.Stdout))

    [<Fact>]
    member _.``runtimeconfig enables ConserveMemory and idle working set trims on Windows`` () =
        let runtimeConfigPath = Path.ChangeExtension(fsiondBinary, ".runtimeconfig.json")
        Assert.True(File.Exists runtimeConfigPath, "missing runtimeconfig.json: " + runtimeConfigPath)
        let configText = File.ReadAllText runtimeConfigPath
        use doc = JsonDocument.Parse configText
        let conserveMemory =
            doc.RootElement
                .GetProperty("runtimeOptions")
                .GetProperty("configProperties")
                .GetProperty("System.GC.ConserveMemory")
                .GetInt32()
        Assert.Equal(9, conserveMemory)

        if OperatingSystem.IsWindows() then
            withContext "idle-trim" (fun context _ ->
                let env = [ TrimIdleEnvironmentVariable, "0.02" ]
                let prime = context.Run([ "--pool-size"; "1"; "-e"; "let a = Array.zeroCreate<byte> (8 * 1024 * 1024)\nprintfn \"%d\" a.Length" ], timeoutMs = 120000, extraEnv = env)
                Assert.True(prime.ExitCode = 0, prime.Stderr)
                let pid = context.DaemonPid().Value
                let beforeWs = Process.GetProcessById(pid).WorkingSet64
                Thread.Sleep 2500
                let afterWs = Process.GetProcessById(pid).WorkingSet64
                output.WriteLine($"Windows idle trim WorkingSet64: before={beforeWs / 1024L / 1024L} MB -> after={afterWs / 1024L / 1024L} MB")
                Assert.True(afterWs < beforeWs, $"expected WorkingSet64 to shrink after idle trim: before={beforeWs}, after={afterWs}")

                let after = context.Run([ "-e"; "printfn \"post-trim-ok\"" ], timeoutMs = 60000, extraEnv = env)
                Assert.True(after.ExitCode = 0, after.Stderr)
                Assert.Equal("post-trim-ok" + Environment.NewLine, after.Stdout))
