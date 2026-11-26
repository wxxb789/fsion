module Fsion.Tests.StateGuardTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Xunit
open Fsion.Tests.TestInfra

type StateGuardTests() =
    [<Fact>]
    member _.``piped stdin in file and -e modes matches dotnet fsi byte for byte including >64KB and --json`` () =
        withContext "stdin-parity" (fun context directory ->
            let scriptPath = Path.Combine(directory, "read-stdin.fsx")
            writeFile
                directory
                "read-stdin.fsx"
                """
let text = stdin.ReadToEnd()
let lines = text.Split('\n')
printfn "len=%d lines=%d head=%s" text.Length lines.Length (if lines.Length > 0 then lines.[0].TrimEnd('\r') else "")
"""

            let payload =
                let sb = StringBuilder()
                sb.AppendLine("alpha-🚀-中文") |> ignore
                for i in 1 .. 2500 do
                    sb.Append("line-").Append(i).Append("-0123456789abcdef0123456789abcdef\n") |> ignore
                sb.ToString()

            Assert.True(Encoding.UTF8.GetByteCount payload > 64 * 1024)

            let fsiFile = runFsi directory [ scriptPath ] (Some payload)
            let fsionFile = context.Run([ scriptPath ], stdin = payload, timeoutMs = 120000)
            Assert.Equal(fsiFile.ExitCode, fsionFile.ExitCode)
            Assert.Equal(fsiFile.Stdout, fsionFile.Stdout)
            Assert.Equal(fsiFile.Stderr, fsionFile.Stderr)

            let evalCode = "let line = System.Console.ReadLine()\nprintfn \"LINE:%s\" line"
            let smallPayload = "first-line-λ\nsecond-line\n"
            let fsionEval = context.Run([ "-e"; evalCode ], stdin = smallPayload, timeoutMs = 60000)
            Assert.Equal(0, fsionEval.ExitCode)
            Assert.Equal("LINE:first-line-λ" + Environment.NewLine, fsionEval.Stdout)

            let fsionJson = context.Run([ "--json"; scriptPath ], stdin = payload, timeoutMs = 60000)
            Assert.Equal(0, fsionJson.ExitCode)
            use doc = JsonDocument.Parse fsionJson.Stdout
            Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32())
            Assert.Equal(fsiFile.Stdout, doc.RootElement.GetProperty("stdout").GetString()))

    [<Fact>]
    member _.``unclosed idle stdin pipe does not block scripts and ReadLine returns without waiting for EOF`` () =
        withContext "stdin-nonblocking" (fun context directory ->
            let prime = context.Run([ "--pool-size"; "2"; "warmup" ], timeoutMs = 120000)
            Assert.True(prime.ExitCode = 0, prime.Stderr)

            let scriptPath = Path.Combine(directory, "no-stdin.fsx")
            writeFile directory "no-stdin.fsx" "printfn \"instant-ok\"\n"

            // Negative control 1: RedirectStandardInput = true, never write or close StandardInput.
            let psi1 = ProcessStartInfo(fsionBinary)
            psi1.WorkingDirectory <- directory
            psi1.UseShellExecute <- false
            psi1.RedirectStandardInput <- true
            psi1.RedirectStandardOutput <- true
            psi1.RedirectStandardError <- true
            psi1.ArgumentList.Add scriptPath
            for key, value in context.Environment do
                psi1.Environment.[key] <- value
            let sw = Stopwatch.StartNew()
            use p1 = Process.Start psi1
            let outTask = p1.StandardOutput.ReadToEndAsync()
            let errTask = p1.StandardError.ReadToEndAsync()
            let exited = p1.WaitForExit 5000
            sw.Stop()
            if not exited then
                try p1.Kill(true) with _ -> ()
            Assert.True(exited, "fsion blocked on unclosed idle stdin pipe")
            Assert.Equal(0, p1.ExitCode)
            Assert.Equal("instant-ok" + Environment.NewLine, outTask.GetAwaiter().GetResult())
            Assert.Equal("", errTask.GetAwaiter().GetResult())
            Assert.True(sw.ElapsedMilliseconds < 2000L, $"expected fast completion without stdin read, got {sw.ElapsedMilliseconds} ms")
            // Negative control 2: script calls Console.ReadLine(), caller writes one line and keeps stdin open.
            let psi2 = ProcessStartInfo(fsionBinary)
            psi2.WorkingDirectory <- directory
            psi2.UseShellExecute <- false
            psi2.RedirectStandardInput <- true
            psi2.RedirectStandardOutput <- true
            psi2.RedirectStandardError <- true
            psi2.ArgumentList.Add "-e"
            psi2.ArgumentList.Add "let s = System.Console.ReadLine()\nprintfn \"GOT:%s\" s"
            for key, value in context.Environment do
                psi2.Environment.[key] <- value
            use p2 = Process.Start psi2
            let out2 = p2.StandardOutput.ReadToEndAsync()
            let err2 = p2.StandardError.ReadToEndAsync()
            p2.StandardInput.WriteLine "hello-before-eof"
            p2.StandardInput.Flush()
            let exited2 = p2.WaitForExit 5000
            if not exited2 then
                try p2.Kill(true) with _ -> ()
            Assert.True(exited2, "fsion ReadLine blocked waiting for EOF on open stdin pipe")
            Assert.Equal(0, p2.ExitCode)
            Assert.Equal("GOT:hello-before-eof" + Environment.NewLine, out2.GetAwaiter().GetResult())
            Assert.Equal("", err2.GetAwaiter().GetResult()))

    [<Fact>]
    member _.``exit inside try-with preserves first exit code and suppresses internal markers and post-exit output`` () =
        withContext "exit-try-with" (fun context _ ->
            let case1 = "try printfn \"ok\"; exit 0 with ex -> eprintfn \"caught: %s\" ex.Message; exit 1"
            let r1 = context.Run([ "-e"; case1 ], timeoutMs = 120000)
            Assert.Equal(0, r1.ExitCode)
            Assert.Equal("ok" + Environment.NewLine, r1.Stdout)
            Assert.Equal("", r1.Stderr)

            let case2 = "try eprintfn \"before\"; exit 5 with _ -> printfn \"swallowed\"; eprintfn \"after\""
            let r2 = context.Run([ "-e"; case2 ], timeoutMs = 60000)
            Assert.Equal(5, r2.ExitCode)
            Assert.Equal("", r2.Stdout)
            Assert.Equal("before" + Environment.NewLine, r2.Stderr)
            Assert.DoesNotContain("@@FSION-EXIT", r2.Stdout)
            Assert.DoesNotContain("@@FSION-EXIT", r2.Stderr))

    [<Fact>]
    member _.``post-run global state guards isolate Console, AppDomain events, CultureInfo, and Trace listeners`` () =
        withContext "global-state-guards" (fun context _ ->
            let prime = context.Run([ "--pool-size"; "2"; "warmup" ], timeoutMs = 120000)
            Assert.True(prime.ExitCode = 0, prime.Stderr)
            let daemonPid = context.DaemonPid()
            Assert.True(daemonPid.IsSome)

            let poisonCode =
                """
open System
open System.Diagnostics
open System.Globalization
open System.IO
Console.SetOut(new StringWriter())
Console.SetError(new StringWriter())
Console.SetIn(new StringReader("fake"))
AppDomain.CurrentDomain.FirstChanceException.Add(fun _ -> printfn "POISON")
CultureInfo.DefaultThreadCurrentCulture <- CultureInfo.GetCultureInfo("de-DE")
CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo("de-DE")
Trace.Listeners.Add(new ConsoleTraceListener()) |> ignore
"""
            let r1 = context.Run([ "-e"; poisonCode ], timeoutMs = 60000)
            Assert.True(r1.ExitCode = 0, r1.Stderr)
            Assert.Equal(daemonPid, context.DaemonPid())

            let probeCode =
                """
open System
open System.Diagnostics
open System.Globalization
try failwith "boom" with _ -> ()
Trace.WriteLine("trace-check")
printfn "clean %.2f|%s" 1.5 (if isNull CultureInfo.DefaultThreadCurrentCulture then "null" else CultureInfo.DefaultThreadCurrentCulture.Name)
"""
            let r2 = context.Run([ "-e"; probeCode ], timeoutMs = 60000)
            Assert.True(r2.ExitCode = 0, r2.Stderr)
            Assert.Equal(daemonPid, context.DaemonPid())
            Assert.DoesNotContain("POISON", r2.Stdout)
            Assert.DoesNotContain("trace-check", r2.Stdout)
            Assert.Contains("clean 1.50|", r2.Stdout)
            Assert.DoesNotContain("de-DE", r2.Stdout)

            let metrics = context.MetricsOrFail()
            Assert.Equal(0, metrics.AliveDisposedSessions))

    [<Fact>]
    member _.``json precheck with #load comment preserves stdin, timeout on idle stdin exits promptly, and concurrent stdin readers do not block each other`` () =
        withContext "stdin-edge-cases" (fun context directory ->
            let prime = context.Run([ "--pool-size"; "2"; "warmup" ], timeoutMs = 120000)
            Assert.True(prime.ExitCode = 0, prime.Stderr)

            // Finding 1: --json with #load in comment must preserve stdin.
            let rJson = context.Run([ "--json"; "-e"; "printfn \"%s\" (System.Console.ReadLine()) // #load" ], stdin = "hello-json\n", timeoutMs = 60000)
            Assert.Equal(0, rJson.ExitCode)
            use doc = JsonDocument.Parse rJson.Stdout
            Assert.Equal("hello-json" + Environment.NewLine, doc.RootElement.GetProperty("stdout").GetString())

            // Finding 2: --timeout 1000 while blocked on unclosed idle stdin returns 124 promptly without wedging daemon.
            let daemonPid = context.DaemonPid()
            let psiTimeout = ProcessStartInfo(fsionBinary)
            psiTimeout.WorkingDirectory <- directory
            psiTimeout.UseShellExecute <- false
            psiTimeout.RedirectStandardInput <- true
            psiTimeout.RedirectStandardOutput <- true
            psiTimeout.RedirectStandardError <- true
            psiTimeout.ArgumentList.Add "--timeout"
            psiTimeout.ArgumentList.Add "1000"
            psiTimeout.ArgumentList.Add "-e"
            psiTimeout.ArgumentList.Add "System.Console.ReadLine() |> ignore"
            for key, value in context.Environment do
                psiTimeout.Environment.[key] <- value
            let sw = Stopwatch.StartNew()
            use pTimeout = Process.Start psiTimeout
            let _ = pTimeout.StandardOutput.ReadToEndAsync()
            let _ = pTimeout.StandardError.ReadToEndAsync()
            let exited = pTimeout.WaitForExit 8000
            sw.Stop()
            if not exited then try pTimeout.Kill(true) with _ -> ()
            Assert.True(exited, "fsion --timeout 1000 blocked on unclosed idle stdin")
            Assert.Equal(124, pTimeout.ExitCode)
            Assert.True(sw.ElapsedMilliseconds < 6000L, $"expected prompt timeout exit, got {sw.ElapsedMilliseconds} ms")
            Assert.Equal(daemonPid, context.DaemonPid())

            // Finding 3: concurrent stdin readers on the same daemon do not serialize on SyncTextReader.
            let startReader () =
                let psi = ProcessStartInfo(fsionBinary)
                psi.WorkingDirectory <- directory
                psi.UseShellExecute <- false
                psi.RedirectStandardInput <- true
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.ArgumentList.Add "-e"
                psi.ArgumentList.Add "printfn \"R:%s\" (System.Console.ReadLine())"
                for key, value in context.Environment do
                    psi.Environment.[key] <- value
                Process.Start psi
            use pA = startReader ()
            let outA = pA.StandardOutput.ReadToEndAsync()
            let _ = pA.StandardError.ReadToEndAsync()
            Thread.Sleep 300
            use pB = startReader ()
            let outB = pB.StandardOutput.ReadToEndAsync()
            let _ = pB.StandardError.ReadToEndAsync()
            pB.StandardInput.WriteLine "from-B"
            pB.StandardInput.Flush()
            let bDone = pB.WaitForExit 5000
            if not bDone then try pB.Kill(true) with _ -> ()
            pA.StandardInput.WriteLine "from-A"
            pA.StandardInput.Flush()
            let aDone = pA.WaitForExit 5000
            if not aDone then try pA.Kill(true) with _ -> ()
            Assert.True(bDone, "reader B was blocked by idle reader A")
            Assert.True(aDone, "reader A did not finish after receiving input")
            Assert.Equal("R:from-B" + Environment.NewLine, outB.GetAwaiter().GetResult())
            Assert.Equal("R:from-A" + Environment.NewLine, outA.GetAwaiter().GetResult()))

    [<Fact>]
    member _.``exit after Console.SetOut(Null) still truncates output and Dispose poisoning is cleaned up`` () =
        withContext "exit-dispose-edge" (fun context _ ->
            let prime = context.Run([ "--pool-size"; "2"; "warmup" ], timeoutMs = 120000)
            Assert.True(prime.ExitCode = 0, prime.Stderr)

            // Finding 8: exit marker still truncates even if Console.Out was replaced before exit.
            let exitCase =
                "let saved = System.Console.Out\nSystem.Console.SetOut(System.IO.TextWriter.Null)\ntry exit 5 with _ -> System.Console.SetOut(saved); System.Console.WriteLine(\"after\")"
            let rExit = context.Run([ "-e"; exitCase ], timeoutMs = 60000)
            Assert.Equal(5, rExit.ExitCode)
            Assert.Equal("", rExit.Stdout)

            // Finding 5: IDisposable.Dispose poisoning Console.Out during stateless cleanup is restored.
            let disposePoison =
                "let d = { new System.IDisposable with member _.Dispose() = System.Console.SetOut(System.IO.TextWriter.Null) }"
            let r1 = context.Run([ "-e"; disposePoison ], timeoutMs = 60000)
            Assert.Equal(0, r1.ExitCode)
            let _ = context.MetricsOrFail()
            let r2 = context.Run([ "-e"; "System.Console.WriteLine(\"expected\")" ], timeoutMs = 60000)
            Assert.Equal(0, r2.ExitCode)
            Assert.Equal("expected" + Environment.NewLine, r2.Stdout))

    [<Fact>]
    member _.``caught exit with timeout preserves exit code without marker leakage, and named session --reset restores Console.Out`` () =
        withContext "exit-timeout-reset" (fun context _ ->
            let prime = context.Run([ "--pool-size"; "2"; "warmup" ], timeoutMs = 120000)
            Assert.True(prime.ExitCode = 0, prime.Stderr)

            // Finding 3: --reset with IDisposable poisoning Console.Out restores Console.Out before running new script.
            let rBind =
                context.Run(
                    [ "--session"; "s-reset"; "-e"; "let d = { new System.IDisposable with member _.Dispose() = System.Console.SetOut(System.IO.TextWriter.Null) }" ],
                    timeoutMs = 60000
                )
            Assert.Equal(0, rBind.ExitCode)
            let rReset =
                context.Run(
                    [ "--session"; "s-reset"; "--reset"; "-e"; "System.Console.WriteLine(\"after-reset\")" ],
                    timeoutMs = 60000
                )
            Assert.Equal(0, rReset.ExitCode)
            Assert.Equal("after-reset" + Environment.NewLine, rReset.Stdout)

            // Finding 4: caught exit followed by timeout preserves first exit code and strips markers.
            let rCaughtTimeout =
                context.Run(
                    [ "--timeout"; "1000"; "-e"; "try printfn \"before\"; exit 5 with _ -> System.Threading.Thread.Sleep(5000)" ],
                    timeoutMs = 60000
                )
            Assert.Equal(5, rCaughtTimeout.ExitCode)
            Assert.Equal("before" + Environment.NewLine, rCaughtTimeout.Stdout)
            Assert.DoesNotContain("@@FSION-EXIT", rCaughtTimeout.Stdout)
            Assert.DoesNotContain("@@FSION-EXIT", rCaughtTimeout.Stderr))
