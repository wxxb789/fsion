module Fsion.Tests.SessionTests

open System
open System.Threading
open Xunit
open Xunit.Abstractions
open Fsion.Tests.TestInfra

type SessionTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.``named sessions preserve bindings across turns and stay isolated from stateless and other ids`` () =
        withContext "session-continuity" (fun context _ ->
            let define = context.Run([ "--session"; "alpha"; "-e"; "let data = [1 .. 1000]" ], timeoutMs = 120000)
            Assert.Equal(0, define.ExitCode)
            let daemonPid = context.DaemonPid()
            Assert.True(daemonPid.IsSome, "daemon was not reported as running")

            let second =
                context.Run([ "--session"; "alpha"; "-e"; "printfn \"%d\" (List.sum data)" ], timeoutMs = 60000)

            Assert.Equal(0, second.ExitCode)
            Assert.Equal("500500" + Environment.NewLine, second.Stdout)

            let stateless = context.Run([ "-e"; "printfn \"%d\" (List.sum data)" ], timeoutMs = 60000)
            Assert.Equal(1, stateless.ExitCode)
            Assert.Contains("FS0039", stateless.Stderr)

            let other = context.Run([ "--session"; "beta"; "-e"; "printfn \"%d\" (List.sum data)" ], timeoutMs = 60000)
            Assert.Equal(1, other.ExitCode)
            Assert.Contains("FS0039", other.Stderr)

            Assert.Equal(daemonPid, context.DaemonPid()))

    [<Fact>]
    member _.``session reset discards old bindings and keeps the recreated session usable`` () =
        withContext "session-reset" (fun context _ ->
            let define = context.Run([ "--session"; "alpha"; "-e"; "let data = 41" ], timeoutMs = 120000)
            Assert.Equal(0, define.ExitCode)

            let reset =
                context.Run(
                    [ "--session"; "alpha"; "--reset"; "-e"; "let fresh = 7\nprintfn \"%d\" fresh" ],
                    timeoutMs = 60000
                )

            Assert.Equal(0, reset.ExitCode)
            Assert.Equal("7" + Environment.NewLine, reset.Stdout)

            let gone = context.Run([ "--session"; "alpha"; "-e"; "printfn \"%d\" data" ], timeoutMs = 60000)
            Assert.Equal(1, gone.ExitCode)
            Assert.Contains("FS0039", gone.Stderr)

            let kept = context.Run([ "--session"; "alpha"; "-e"; "printfn \"%d\" fresh" ], timeoutMs = 60000)
            Assert.Equal(0, kept.ExitCode)
            Assert.Equal("7" + Environment.NewLine, kept.Stdout))

    [<Fact>]
    member _.``session idle ttl evicts and unloads the abandoned named session`` () =
        withContext "session-ttl" (fun context _ ->
            let warmup = context.Run([ "--session-ttl"; "0.05"; "warmup" ], timeoutMs = 120000)
            Assert.Equal(0, warmup.ExitCode)

            let define = context.Run([ "--session"; "alpha"; "-e"; "let data = [1 .. 1000]" ], timeoutMs = 60000)
            Assert.Equal(0, define.ExitCode)

            let first =
                context.Metrics(30000)
                |> Option.defaultWith (fun () -> failwith "fsiond did not answer GetMetrics")

            Assert.Equal(1, first.NamedSessions)

            let deadline = DateTime.UtcNow.AddSeconds(60.0)
            let mutable named = first.NamedSessions

            while named > 0 && DateTime.UtcNow < deadline do
                Thread.Sleep 500

                match context.Metrics 30000 with
                | Some metrics -> named <- metrics.NamedSessions
                | None -> ()

            Assert.Equal(0, named)

            Thread.Sleep 1000

            let settleDeadline = DateTime.UtcNow.AddSeconds(60.0)

            let mutable settled =
                context.Metrics(30000)
                |> Option.defaultWith (fun () -> failwith "fsiond did not answer GetMetrics")

            // The evicted session's collectible assembly unloads through the cleanup queue and the
            // pool must refill to the warm watermark before the exact assembly census is meaningful.
            while settled.ScriptAssemblies <> settled.PoolTarget && DateTime.UtcNow < settleDeadline do
                Thread.Sleep 500

                match context.Metrics 30000 with
                | Some metrics -> settled <- metrics
                | None -> ()

            output.WriteLine(
                "named sessions after ttl: "
                + string settled.NamedSessions
                + ", scriptAssemblies="
                + string settled.ScriptAssemblies
                + ", poolTarget="
                + string settled.PoolTarget
                + ", aliveDisposedSessions="
                + string settled.AliveDisposedSessions
            )

            Assert.Equal(0, settled.NamedSessions)
            Assert.Equal(0, settled.AliveDisposedSessions)
            Assert.Equal(settled.PoolTarget, settled.ScriptAssemblies)

            let gone =
                context.Run([ "--session"; "alpha"; "-e"; "printfn \"%d\" (List.sum data)" ], timeoutMs = 60000)

            Assert.Equal(1, gone.ExitCode)
            Assert.Contains("FS0039", gone.Stderr))
