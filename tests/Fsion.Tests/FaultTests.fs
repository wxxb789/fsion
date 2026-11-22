module Fsion.Tests.FaultTests

open System
open Xunit
open Fsion.Tests.TestInfra

let private primeAndGetPid (context: FsionContext) =
    let prime = context.Run [ "-e"; "printfn \"prime\"" ]
    Assert.Equal(0, prime.ExitCode)
    let pid = context.DaemonPid()
    Assert.True(pid.IsSome, "daemon was not reported as running")
    pid

[<Fact>]
let ``exit 42 returns the code while fsiond keeps serving the next invocation`` () =
    withContext "fault-exit" (fun context directory ->
        writeFile directory "exit42.fsx" "printfn \"before\"\nexit 42\nprintfn \"after\"\n"
        let pidBefore = primeAndGetPid context

        let exitRun = context.Run [ "exit42.fsx" ]
        Assert.Equal(42, exitRun.ExitCode)
        Assert.Equal("before" + Environment.NewLine, exitRun.Stdout)
        Assert.Equal("", exitRun.Stderr)
        Assert.Equal(pidBefore, context.DaemonPid())

        let next = context.Run [ "-e"; "printfn \"next\"" ]
        Assert.Equal(0, next.ExitCode)
        Assert.Equal("next" + Environment.NewLine, next.Stdout)
        Assert.Equal(pidBefore, context.DaemonPid()))

[<Fact>]
let ``a compile error does not crash fsiond and the next invocation succeeds`` () =
    withContext "fault-compile" (fun context directory ->
        writeFile directory "typeerr.fsx" "let x : int = \"nope\"\n"
        let pidBefore = primeAndGetPid context

        let failed = context.Run [ "typeerr.fsx" ]
        Assert.Equal(1, failed.ExitCode)
        Assert.Contains("FS0001", failed.Stderr)
        Assert.Equal(pidBefore, context.DaemonPid())

        let next = context.Run [ "-e"; "printfn \"recovered\"" ]
        Assert.Equal(0, next.ExitCode)
        Assert.Equal("recovered" + Environment.NewLine, next.Stdout)
        Assert.Equal(pidBefore, context.DaemonPid()))

[<Fact>]
let ``a runtime exception does not crash fsiond and the next invocation succeeds`` () =
    withContext "fault-runtime" (fun context directory ->
        writeFile directory "boom.fsx" "do failwith \"boom\"\n"
        let pidBefore = primeAndGetPid context

        let failed = context.Run [ "boom.fsx" ]
        Assert.Equal(1, failed.ExitCode)
        Assert.Contains("System.Exception: boom", failed.Stderr)
        Assert.Equal(pidBefore, context.DaemonPid())

        let next = context.Run [ "-e"; "printfn \"recovered\"" ]
        Assert.Equal(0, next.ExitCode)
        Assert.Equal(pidBefore, context.DaemonPid()))
