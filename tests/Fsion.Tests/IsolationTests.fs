module Fsion.Tests.IsolationTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open Fsion.Tests.TestInfra

let private expectedLines (prefix: string) (tag: string) (count: int) =
    String.concat "" [ for n in 1 .. count -> prefix + "-" + tag + "-" + string n + Environment.NewLine ]

let private median (values: float seq) =
    let sorted = values |> Seq.sort |> Seq.toArray
    sorted.[sorted.Length / 2]

type IsolationTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.``stateless runs do not leak let bindings across invocations`` () =
        withContext "isolation-bindings" (fun context _ ->
            let define = context.Run([ "-e"; "let secret = 123" ], timeoutMs = 120000)
            Assert.Equal(0, define.ExitCode)

            let leak = context.Run([ "-e"; "printfn \"%d\" secret" ], timeoutMs = 60000)
            Assert.Equal(1, leak.ExitCode)
            Assert.Contains("FS0039", leak.Stderr)
            Assert.True((context.DaemonPid()).IsSome, "the daemon did not survive the failed run"))

    [<Fact>]
    member _.``four concurrent invocations keep their own stdout and stderr lines`` () =
        withContext "isolation-concurrent" (fun context _ ->
            let warmup = context.Run([ "warmup" ], timeoutMs = 180000)
            Assert.Equal(0, warmup.ExitCode)

            let script tag =
                String.concat
                    "\n"
                    [ for n in 1 .. 10 do
                          yield "printfn \"out-" + tag + "-" + string n + "\""
                          yield "eprintfn \"err-" + tag + "-" + string n + "\""
                          yield "System.Threading.Thread.Sleep 5" ]

            let runOne tag =
                let result = context.Run([ "-e"; script tag ], timeoutMs = 180000)
                tag, result

            let results =
                [ for tag in [ "a"; "b"; "c"; "d" ] -> Task.Run(fun () -> runOne tag) ]
                |> List.map (fun task -> task.Result)

            for tag, result in results do
                Assert.Equal(0, result.ExitCode)
                Assert.Equal(expectedLines "out" tag 10, result.Stdout)
                Assert.Equal(expectedLines "err" tag 10, result.Stderr))

    [<Fact>]
    member _.``concurrent invocations from different directories see their own working directory`` () =
        let endpoint = "fsion-test-" + Guid.NewGuid().ToString("N")
        let environment = [ "FSION_PIPE", endpoint; "FSIOND_PATH", fsiondBinary ]
        let directoryA = createTempDir "isolation-cwd-a"
        let directoryB = createTempDir "isolation-cwd-b"

        let normalize (path: string) =
            path.TrimEnd('\\', '/').Replace('/', '\\').ToLowerInvariant()

        try
            let run directory =
                runProcess
                    fsionBinary
                    [ "-e"; "printfn \"cwd=%s\" (System.IO.Directory.GetCurrentDirectory())" ]
                    directory
                    environment
                    None
                    180000

            let taskA = Task.Run(fun () -> run directoryA)
            let taskB = Task.Run(fun () -> run directoryB)
            let resultA = taskA.Result
            let resultB = taskB.Result
            Assert.Equal(0, resultA.ExitCode)
            Assert.Equal(0, resultB.ExitCode)
            Assert.Equal("cwd=" + normalize directoryA, normalize (resultA.Stdout.TrimEnd('\r', '\n')))
            Assert.Equal("cwd=" + normalize directoryB, normalize (resultB.Stdout.TrimEnd('\r', '\n')))
        finally
            runProcess fsionBinary [ "daemon"; "stop" ] directoryA environment None 30000
            |> ignore

            tryDeleteDirectory directoryA
            tryDeleteDirectory directoryB

    [<Fact>]
    member _.``undisposed file handles from a throwaway script are released before fsion returns`` () =
        withContext "isolation-handles" (fun context directory ->
            let readPath = Path.Combine(directory, "read-target.txt")
            let writePath = Path.Combine(directory, "write-target.txt")
            writeFile directory "read-target.txt" "seed-content\n"

            let code =
                String.concat
                    "\n"
                    [ "let leakedReader = new System.IO.StreamReader(@\"" + readPath + "\")"
                      "leakedReader.ReadToEnd() |> ignore"
                      "let leakedWriter = new System.IO.FileStream(@\"" + writePath + "\", System.IO.FileMode.Create)"
                      "leakedWriter.Write([| 1uy; 2uy; 3uy |], 0, 3)" ]

            let result = context.Run([ "-e"; code ], timeoutMs = 120000)
            Assert.Equal(0, result.ExitCode)

            let delete path =
                try
                    File.Delete path
                    None
                with ex ->
                    Some ex.Message

            match delete readPath with
            | Some message -> Assert.Fail("the undisposed StreamReader still locks its file: " + message)
            | None -> ()

            match delete writePath with
            | Some message -> Assert.Fail("the undisposed FileStream still locks its file: " + message)
            | None -> ()

            Assert.True((context.DaemonPid()).IsSome, "the daemon did not survive the handle-release run"))

    [<Fact>]
    member _.``fifty unique scripts keep the daemon post-GC heap bounded and unload script assemblies`` () =
        withContext "isolation-memory" (fun context _ ->
            let warmup = context.Run([ "warmup" ], timeoutMs = 180000)
            Assert.Equal(0, warmup.ExitCode)
            let daemonPid = context.DaemonPid()
            Assert.True(daemonPid.IsSome, "daemon was not reported as running")

            let runUnique i =
                let code =
                    "let unique"
                    + string i
                    + " (x: int) = x + "
                    + string i
                    + "\nprintfn \"%d\" (unique"
                    + string i
                    + " 1)"

                let result = context.Run([ "-e"; code ], timeoutMs = 120000)
                Assert.Equal(0, result.ExitCode)

            for i in 1 .. 10 do
                runUnique i

            let first =
                context.Metrics(60000)
                |> Option.defaultWith (fun () -> failwith "fsiond did not answer GetMetrics")

            for i in 11 .. 50 do
                runUnique i

            let second =
                context.Metrics(60000)
                |> Option.defaultWith (fun () -> failwith "fsiond did not answer GetMetrics")

            output.WriteLine(
                "heap "
                + string first.ManagedHeapBytes
                + " -> "
                + string second.ManagedHeapBytes
                + ", script assemblies "
                + string first.ScriptAssemblies
                + " -> "
                + string second.ScriptAssemblies
                + ", poolWarm="
                + string second.PoolWarm
                + ", coldClaims="
                + string second.ColdClaims
                + ", aliveDisposedSessions="
                + string second.AliveDisposedSessions
            )

            Assert.Equal(daemonPid, context.DaemonPid())
            Assert.Equal(first.Executed + 40, second.Executed)
            let growth = second.ManagedHeapBytes - first.ManagedHeapBytes
            Assert.True(growth < 64L * 1024L * 1024L, "managed heap grew by " + string growth + " bytes across 40 runs")
            Assert.True(second.ManagedHeapBytes < 400L * 1024L * 1024L, "post-GC heap is " + string second.ManagedHeapBytes + " bytes")
            Assert.True(
                second.ScriptAssemblies <= second.PoolTarget + 1,
                "script assemblies still loaded: " + string second.ScriptAssemblies
            )
            Assert.Equal(0, second.AliveDisposedSessions))

    [<Fact>]
    member _.``spaced fresh scripts claim warm sessions without cold claims`` () =
        withContext "isolation-pool" (fun context _ ->
            let warmup = context.Run([ "warmup" ], timeoutMs = 180000)
            Assert.Equal(0, warmup.ExitCode)
            let durations = ResizeArray<float>()

            for i in 1 .. 5 do
                let deadline = DateTime.UtcNow.AddSeconds(30.0)
                let mutable warm = false

                while not warm && DateTime.UtcNow < deadline do
                    match context.Metrics 30000 with
                    | Some metrics when metrics.PoolWarm >= 1 -> warm <- true
                    | _ -> Thread.Sleep 50

                Assert.True(warm, "the pool never replenished before round " + string i)
                let code = "let fresh" + string i + " (x: int) = x * " + string i + "\nprintfn \"fresh-%d\" (fresh" + string i + " 2)"
                let result = context.Run([ "-e"; code ], timeoutMs = 60000)
                Assert.Equal(0, result.ExitCode)
                durations.Add result.DurationMs

            let metrics =
                context.Metrics(60000)
                |> Option.defaultWith (fun () -> failwith "fsiond did not answer GetMetrics")

            let measuredMedian = median durations

            output.WriteLine(
                "durations="
                + String.Join(", ", durations)
                + " median="
                + string measuredMedian
                + " poolTarget="
                + string metrics.PoolTarget
                + " coldClaims="
                + string metrics.ColdClaims
            )

            Assert.Equal(3, metrics.PoolTarget)
            Assert.Equal(5, metrics.Executed)
            Assert.Equal(0, metrics.ColdClaims)
            Assert.True(measuredMedian < 500.0, "median warm run wall time was " + string measuredMedian + " ms"))
