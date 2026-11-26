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

let private readLog (path: string) =
    if File.Exists path then
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        use reader = new StreamReader(stream)
        reader.ReadToEnd()
    else
        ""

let private awaitCondition message (condition: unit -> bool) =
    Assert.True(SpinWait.SpinUntil(Func<bool>(condition), TimeSpan.FromSeconds 30.0), message)

let private assertRun (result: ProcessResult) =
    Assert.True(result.ExitCode = 0, result.Stderr)

let private assertFileUnlocked path =
    use stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
    Assert.True(stream.Length > 0L)

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
    member _.``per-request environment snapshots apply to the run and are restored afterwards`` () =
        let readSnapshot =
            "printfn \"%s\" (match System.Environment.GetEnvironmentVariable \"FSION_TEST_SNAPSHOT\" with | null -> \"<null>\" | value -> value)"

        withContext "isolation-env-snapshot" (fun context _ ->
            let first =
                context.Run(
                    [ "-e"; readSnapshot ],
                    timeoutMs = 120000,
                    extraEnv = [ "FSION_TEST_SNAPSHOT", "alpha-42" ]
                )

            Assert.Equal(0, first.ExitCode)
            Assert.Equal("alpha-42" + Environment.NewLine, first.Stdout)

            let second = context.Run([ "-e"; readSnapshot ], timeoutMs = 60000)
            Assert.Equal(0, second.ExitCode)
            Assert.Equal("<null>" + Environment.NewLine, second.Stdout))

    [<Fact>]
    member _.``script SetEnvironmentVariable does not leak into subsequent cold session creation`` () =
        withContext "isolation-cold-prelude-env" (fun context directory ->
            let run args =
                context.Run([ "--pool-size"; "0" ] @ args, timeoutMs = 120000, extraEnv = [ "FSION_LEAK_PROBE", null ])

            let mutate =
                "System.Environment.SetEnvironmentVariable(\"FSION_LEAK_PROBE\", \"leaked\"); "
                + "System.Console.Write(System.Environment.GetEnvironmentVariable(\"FSION_LEAK_PROBE\"))"
            let first = run [ "-e"; mutate ]
            assertRun first
            Assert.Equal("leaked", first.Stdout)

            writeFile directory "prelude.fsx"
                "let coldProbe = System.Environment.GetEnvironmentVariable(\"FSION_LEAK_PROBE\") |> Option.ofObj"
            let second = run [ "--prelude"; Path.Combine(directory, "prelude.fsx"); "-e"; "System.Console.Write(defaultArg coldProbe \"<null>\")" ]
            assertRun second
            Assert.Equal("<null>", second.Stdout)
            Assert.Equal(2, context.MetricsOrFail().ColdClaims))

    [<Fact>]
    member _.``parallel daemon log writers preserve every line with a concurrent reader`` () =
        withContext "parallel-log" (fun _ directory ->
            let path = Path.Combine(directory, "daemon.log")
            File.WriteAllText(path, "")
            use reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            let writers = 8
            let linesPerWriter = 64
            use start = new Barrier(writers)
            let tasks =
                Array.init writers (fun writer ->
                    Task.Factory.StartNew(
                        (fun () ->
                            start.SignalAndWait()
                            for line in 1 .. linesPerWriter do
                                Fsiond.Log.info (Some path) $"writer={writer} line={line}"),
                        TaskCreationOptions.LongRunning))
            Task.WaitAll tasks

            use text = new StreamReader(reader)
            let actual =
                text.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun line -> line.Substring(line.IndexOf(" [info] ", StringComparison.Ordinal) + 8))
            let expected = [| for writer in 0 .. writers - 1 do for line in 1 .. linesPerWriter -> $"writer={writer} line={line}" |]
            Assert.Equal(expected.Length, actual.Length)
            Assert.Equal<string>(Array.sort expected, Array.sort actual))

    [<Fact>]
    member _.``concurrent fsiond processes preserve every record in a shared log`` () =
        withContext "shared-log" (fun _ directory ->
            let path = Path.Combine(directory, "daemon.log")
            let writers = 8
            let messages = Array.init 64 (fun index -> $"invalid-{index}-" + String(char (65 + index % 26), 16000))
            use start = new Barrier(writers)
            let tasks =
                Array.init writers (fun writer ->
                    Task.Factory.StartNew(
                        (fun () ->
                            start.SignalAndWait()
                            for index in writer .. writers .. messages.Length - 1 do
                                let args = [ "--log"; path; messages.[index] ]
                                let result = runProcess fsiondBinary args directory [] None 30000
                                Assert.Equal(1, result.ExitCode)),
                        TaskCreationOptions.LongRunning))
            Task.WaitAll tasks

            let actual = File.ReadAllLines path
            Assert.Equal(messages.Length, actual.Length)
            let prefix = " [error] invalid arguments: --log " + path + " "
            let records =
                actual |> Array.map (fun line ->
                    let offset = line.IndexOf(prefix, StringComparison.Ordinal)
                    Assert.True(offset > 0, "missing log record prefix")
                    line.Substring(offset + prefix.Length))
            Assert.Equal<string>(Array.sort messages, Array.sort records))

    [<Fact>]
    member _.``pool size zero disables warm sessions and every run claims cold`` () =
        let poolDisabled = [ "FSION_POOL_SIZE", "0" ]

        withContext "isolation-pool-zero" (fun context _ ->
            let warmup = context.Run([ "warmup" ], timeoutMs = 180000, extraEnv = poolDisabled)
            Assert.Equal(0, warmup.ExitCode)
            Assert.Contains("warm=False", warmup.Stdout)

            let run =
                context.Run([ "-e"; "printfn \"cold\"" ], timeoutMs = 120000, extraEnv = poolDisabled)

            Assert.Equal(0, run.ExitCode)

            let metrics =
                context.Metrics(30000)
                |> Option.defaultWith (fun () -> failwith "fsiond did not answer GetMetrics")

            Assert.Equal(0, metrics.PoolTarget)
            Assert.Equal(0, metrics.PoolWarm)
            Assert.True(metrics.ColdClaims >= 1, "expected at least one cold claim, got " + string metrics.ColdClaims))

    [<Fact>]
    member _.``--pool-size reaches the auto-started daemon and zero disables warm sessions`` () =
        withContext "isolation-pool-flag" (fun context _ ->
            let warmup = context.Run([ "--pool-size"; "0"; "warmup" ], timeoutMs = 180000)
            Assert.Equal(0, warmup.ExitCode)
            Assert.Contains("warm=False", warmup.Stdout)

            let before =
                context.Metrics(30000)
                |> Option.defaultWith (fun () -> failwith "fsiond did not answer GetMetrics")

            Assert.Equal(0, before.PoolTarget)
            Assert.Equal(0, before.PoolWarm)

            let run = context.Run([ "--pool-size"; "0"; "-e"; "printfn \"flag-cold\"" ], timeoutMs = 180000)
            Assert.Equal(0, run.ExitCode)

            let after =
                context.Metrics(30000)
                |> Option.defaultWith (fun () -> failwith "fsiond did not answer GetMetrics")

            Assert.Equal(0, after.PoolTarget)
            Assert.True(
                after.ColdClaims >= before.ColdClaims + 1,
                "expected the flag run to claim cold, got " + string after.ColdClaims
            )

            let invalid = context.Run([ "--pool-size"; "-1"; "-e"; "()" ], timeoutMs = 30000)
            Assert.Equal(1, invalid.ExitCode)
            Assert.Contains("invalid --pool-size value", invalid.Stderr))

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

            Assert.Equal(4, metrics.PoolTarget)
            Assert.Equal(5, metrics.Executed)
            Assert.Equal(0, metrics.ColdClaims)
            Assert.True(measuredMedian < 500.0, "median warm run wall time was " + string measuredMedian + " ms"))

    [<Theory>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    member _.``promoted local streams unlock before a stateless response`` (generation: int) =
        withContext "promoted-handles" (fun context directory ->
            let path = Path.Combine(directory, "promoted.txt")
            writeFile directory "promoted.txt" "payload"
            let code =
                String.concat "\n"
                    [ "open System"
                      "open System.IO"
                      "let run () ="
                      "    let stream = new FileStream(fsi.CommandLineArgs.[1], FileMode.Open, FileAccess.Read, FileShare.Read)"
                      $"    let target = {generation}"
                      "    for _ in 1 .. 4 do"
                      "        if GC.GetGeneration stream < target then GC.Collect(GC.GetGeneration stream, GCCollectionMode.Forced, true, false)"
                      "    if GC.GetGeneration stream < target then failwith \"stream was not promoted\""
                      "    Console.Write(stream.ReadByte())"
                      "    GC.KeepAlive stream"
                      "run ()" ]
            let log = Path.Combine(directory, "daemon.log")
            let result = context.Run([ "--pool-size"; "0"; "-e"; code; path ], timeoutMs = 120000, extraEnv = [ "FSIOND_LOG", log ])
            assertRun result
            Assert.Equal("112", result.Stdout)
            try
                assertFileUnlocked path
            finally
                output.WriteLine(readLog log)
            File.Delete path)

    [<Fact>]
    member _.``local streams unlock despite an aggregate handle drop`` () =
        withContext "handle-drop" (fun context directory ->
            let path = Path.Combine(directory, "leaked.txt")
            let log = Path.Combine(directory, "daemon.log")
            let run args =
                context.Run([ "--pool-size"; "0" ] @ args, timeoutMs = 120000, extraEnv = [ "FSIOND_LOG", log ])
            writeFile directory "leaked.txt" "payload"
            run [ "--session"; "setup"; "-e"
                  "System.AppDomain.CurrentDomain.SetData(\"handle-drop\", Array.init 64 (fun _ -> new System.Threading.ManualResetEvent(false)))" ]
            |> assertRun
            for i in 1 .. 3 do
                run [ "-e"; $"let value{i} = {i} * 2" ] |> assertRun

            let code =
                String.concat "\n"
                    [ "open System"
                      "open System.Diagnostics"
                      "open System.IO"
                      "open System.Threading"
                      "let run () ="
                      "    use daemon = Process.GetCurrentProcess()"
                      "    let handles () = daemon.Refresh(); daemon.HandleCount"
                      "    let before = if OperatingSystem.IsWindows() then handles () else 0"
                      "    let stream = File.OpenRead(fsi.CommandLineArgs.[1])"
                      "    let events = AppDomain.CurrentDomain.GetData(\"handle-drop\") :?> ManualResetEvent array"
                      "    for handle in events do handle.Dispose()"
                      "    AppDomain.CurrentDomain.SetData(\"handle-drop\", null)"
                      "    if OperatingSystem.IsWindows() then"
                      "        let after = handles ()"
                      "        if after >= before then failwith $\"expected a handle drop: {before} -> {after}\""
                      "    Console.Write(stream.ReadByte())"
                      "    GC.KeepAlive stream"
                      "run ()" ]
            let leaked = run [ "-e"; code; path ]
            assertRun leaked
            Assert.Equal("112", leaked.Stdout)
            assertFileUnlocked path
            File.Delete path

            let structPath = Path.Combine(directory, "struct-leaked.txt")
            writeFile directory "struct-leaked.txt" "payload"
            let holdEntered = Path.Combine(directory, "struct-hold-entered.txt")
            let holdRelease = Path.Combine(directory, "struct-hold-release.txt")
            let holdNamed =
                System.Threading.Tasks.Task.Run(fun () ->
                    run
                        [ "--session"; "setup"; "-e"
                          sprintf "System.IO.File.WriteAllText(@\"%s\", \"1\"); while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20" holdEntered holdRelease ])
            awaitCondition "named hold turn entered" (fun () -> File.Exists holdEntered)
            try
                let structCode =
                    String.concat "\n"
                        [ "[<Struct>]"
                          "type StructHolder = { Stream: System.IO.FileStream }"
                          "let holder = { Stream = System.IO.File.OpenRead(fsi.CommandLineArgs.[1]) }"
                          "System.Console.Write(holder.Stream.ReadByte())" ]
                let structRun = run [ "-e"; structCode; structPath ]
                assertRun structRun
                Assert.Equal("112", structRun.Stdout)
                assertFileUnlocked structPath
                File.Delete structPath
            finally
                writeFile directory "struct-hold-release.txt" "go"
                holdNamed.GetAwaiter().GetResult() |> assertRun

            let tokenScript1 = Path.Combine(directory, "token-1.fsx")
            let tokenScript2 = Path.Combine(directory, "token-2.fsx")
            writeFile directory "token-1.fsx" "let token = Async.DefaultCancellationToken\nSystem.Console.Write(token.CanBeCanceled)\n"
            writeFile directory "token-2.fsx" "System.Console.Write(Async.DefaultCancellationToken.WaitHandle.WaitOne(0))\n"
            let tokenRun1 = run [ tokenScript1 ]
            assertRun tokenRun1
            Assert.Equal("True", tokenRun1.Stdout)
            let tokenRun2 = run [ tokenScript2 ]
            assertRun tokenRun2
            Assert.Equal("False", tokenRun2.Stdout))

    [<Fact>]
    member _.``an empty pool claimant takes the session made by its in-flight producer`` () =
        withContext "single-producer" (fun context directory ->
            let log = Path.Combine(directory, "daemon.log")
            let prelude = Path.Combine(directory, "prelude.fsx")
            let block = Path.Combine(directory, "block")
            let creating = Path.Combine(directory, "creating")
            let active = Path.Combine(directory, "active")
            let env = [ "FSIOND_LOG", log ]
            let literal path = "@\"" + path + "\""
            writeFile directory "prelude.fsx"
                (String.concat "\n"
                     [ "open System.IO"
                       "open System.Threading"
                       "if File.Exists " + literal block + " then"
                       "    File.WriteAllText(" + literal creating + ", \"creating\")"
                       "    while File.Exists " + literal block + " do Thread.Sleep 10"
                       "let greeting = \"hello\"" ])
            let run args =
                context.Run(
                    [ "--pool-size"; "2"; "--prelude"; prelude ] @ args,
                    timeoutMs = 120000,
                    extraEnv = env
                )
            run [ "warmup" ] |> assertRun
            let before = context.MetricsOrFail()
            Assert.Equal(2, before.PoolWarm)
            writeFile directory "block" ""
            try
                let first = run [ "-e"; "System.Console.Write(greeting)" ]
                assertRun first
                Assert.Equal("hello", first.Stdout)
                awaitCondition "replenishment did not start" (fun () -> File.Exists creating)
                let second = Task.Run(fun () -> run [ "-e"; "System.Console.Write(greeting + \"2\")" ])
                Thread.Sleep 150
                let third = Task.Run(fun () -> run [ "-e"; "System.IO.File.WriteAllText(" + literal active + ", greeting)" ])
                awaitCondition "empty-pool claimant did not wait for the in-flight producer" (fun () -> (readLog log).Contains("session-claim waiting"))
                Assert.False(File.Exists active)
                File.Delete block
                let secondRes = second.GetAwaiter().GetResult()
                assertRun secondRes
                Assert.Equal("hello2", secondRes.Stdout)
                assertRun (third.GetAwaiter().GetResult())
                Assert.Equal("hello", File.ReadAllText active)
                let after = context.MetricsOrFail()
                Assert.Equal(before.Executed + 3, after.Executed)
                Assert.Equal(before.ColdClaims, after.ColdClaims)
                Assert.Equal(2, after.PoolWarm)
                let creations = (readLog log).Split('\n') |> Array.filter (fun line -> line.Contains "session-created total=")
                Assert.Equal(5, creations.Length)
            finally
                File.Delete block)

    [<Fact>]
    member _.``no-daemon creates just the one session it executes`` () =
        withContext "oneshot-creation-count" (fun context directory ->
            let log = Path.Combine(directory, "oneshot.log")
            let result =
                context.Run(
                    [ "--no-daemon"; "--pool-size"; "5"; "-e"; "System.Console.Write(42)" ],
                    timeoutMs = 120000,
                    extraEnv = [ "FSIOND_LOG", log ])
            assertRun result
            Assert.Equal("42", result.Stdout)
            awaitCondition "oneshot daemon did not report one execution" (fun () -> (readLog log).Contains "totalExecuted=1")
            let creations = (readLog log).Split('\n') |> Array.filter (fun line -> line.Contains "session-created total=")
            Assert.Single creations |> ignore
            Assert.Contains("total=1", creations.[0]))

    [<Fact>]
    member _.``twenty unseen zero-gap runs unload their collectible sessions`` () =
        withContext "zero-gap-unload" (fun context directory ->
            let log = Path.Combine(directory, "daemon.log")
            let run args = context.Run(args, timeoutMs = 120000, extraEnv = [ "FSIOND_LOG", log ])
            run [ "warmup" ] |> assertRun
            let before = context.MetricsOrFail()
            for i in 1 .. 20 do
                writeFile directory $"burst-{i}.fsx" $"let value = {i} * 2\nSystem.Console.Write(value)\n"
            for i in 1 .. 20 do
                let result = run [ Path.Combine(directory, $"burst-{i}.fsx") ]
                assertRun result
                Assert.Equal(string (i * 2), result.Stdout)

            awaitCondition "background unload did not reach zero released sessions"
                (fun () -> (readLog log).Contains "unload released=20 alive=0")
            let after = context.MetricsOrFail()
            Assert.Equal(before.Executed + 20, after.Executed)
            Assert.Equal(0, after.AliveDisposedSessions)
            Assert.True(after.ScriptAssemblies <= after.PoolTarget)
            Assert.InRange(after.ManagedHeapBytes, 1L, 400L * 1024L * 1024L)
            Assert.True(after.ManagedHeapBytes - before.ManagedHeapBytes < 64L * 1024L * 1024L)
            output.WriteLine($"heap {before.ManagedHeapBytes} -> {after.ManagedHeapBytes}; assemblies {after.ScriptAssemblies}; cold claims {after.ColdClaims - before.ColdClaims}"))

    [<Fact>]
    member _.``clearing script statics does not invoke custom struct parameterless constructors`` () =
        withContext "struct-ctor-safety" (fun context directory ->
            let markerPath = Path.Combine(directory, "struct-ctor-called.txt")
            let csDir = Path.Combine(directory, "structlib")
            Directory.CreateDirectory csDir |> ignore
            writeFile
                csDir
                "StructLib.csproj"
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n"
            writeFile
                csDir
                "Probe.cs"
                (sprintf
                    "namespace StructLib;\npublic struct SideEffectStruct {\n    public int Value;\n    public SideEffectStruct() {\n        System.IO.File.WriteAllText(@\"%s\", \"called\");\n        Value = 99;\n    }\n}\n"
                    markerPath)
            let buildLib = runProcess "dotnet" [ "build"; csDir; "-c"; "Release"; "--nologo" ] directory [] None 120000
            Assert.True(buildLib.ExitCode = 0, buildLib.Stdout + "\n" + buildLib.Stderr)
            let dllPath = Path.Combine(csDir, "bin", "Release", "net10.0", "StructLib.dll")
            let scriptPath = Path.Combine(directory, "use-struct.fsx")
            File.WriteAllText(
                scriptPath,
                sprintf
                    "#r @\"%s\"\nlet mutable s : StructLib.SideEffectStruct = Unchecked.defaultof<StructLib.SideEffectStruct>\nSystem.Console.Write(s.Value)\n"
                    dllPath)
            let runRes = context.Run([ scriptPath ], timeoutMs = 120000)
            assertRun runRes
            Assert.Equal("0", runRes.Stdout)
            Assert.False(File.Exists markerPath, "clearScriptStatics invoked custom struct parameterless constructor"))

    [<Fact>]
    member _.``LOH-referenced streams unlock before a stateless response and slow Dispose does not block siblings`` () =
        withContext "loh-and-slow-dispose" (fun context directory ->
            let warmup = context.Run([ "--pool-size"; "2"; "warmup" ], timeoutMs = 120000)
            assertRun warmup

            let targetPath = Path.Combine(directory, "loh-target.bin")
            File.WriteAllBytes(targetPath, [| 112uy; 113uy |])
            let lohScript = Path.Combine(directory, "loh-leak.fsx")
            File.WriteAllText(
                lohScript,
                sprintf
                    "let leakLoh () =\n    let arr = Array.zeroCreate<System.IO.FileStream> 32768\n    arr.[0] <- System.IO.File.OpenRead(@\"%s\")\n    arr.[0].ReadByte()\nSystem.Console.Write(leakLoh ())\n"
                    targetPath)
            let lohRes = context.Run([ lohScript ], timeoutMs = 60000)
            assertRun lohRes
            Assert.Equal("112", lohRes.Stdout)
            use exclusive = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            exclusive.Close()
            File.Delete targetPath

            let p0WorkDir = Path.Combine(directory, "p0-work")
            Directory.CreateDirectory p0WorkDir |> ignore
            use poolZeroContext = new FsionContext(p0WorkDir)
            let p0Log = Path.Combine(directory, "daemon-reuse.log")
            let runP0 args =
                poolZeroContext.Run(args, timeoutMs = 120000, extraEnv = [ "FSIOND_LOG", p0Log ])
            runP0 [ "--pool-size"; "0"; "warmup" ] |> assertRun
            Assert.Equal(0, poolZeroContext.MetricsOrFail().PoolTarget)

            let reuseEventsPath = Path.Combine(directory, "loh-reuse-events.bin")
            File.WriteAllBytes(reuseEventsPath, [| 97uy; 98uy |])
            runP0
                [ "--session"; "reuse-seed"; "-e"
                  "System.AppDomain.CurrentDomain.SetData(\"reuse-events\", Array.init 384 (fun _ -> new System.Threading.ManualResetEvent(false)))" ]
            |> assertRun
            let reuseEventsCode =
                String.concat "\n"
                    [ "open System"
                      "open System.IO"
                      "open System.Threading"
                      "let leakReuse () ="
                      "    let events = AppDomain.CurrentDomain.GetData(\"reuse-events\") :?> ManualResetEvent array"
                      "    for ev in events do ev.Dispose()"
                      "    AppDomain.CurrentDomain.SetData(\"reuse-events\", null)"
                      "    let arr = Array.zeroCreate<FileStream> 32768"
                      "    arr.[0] <- File.OpenRead(fsi.CommandLineArgs.[1])"
                      "    Console.Write(arr.[0].ReadByte())"
                      "leakReuse ()" ]
            let reuseEventsRes = runP0 [ "-e"; reuseEventsCode; reuseEventsPath ]
            assertRun reuseEventsRes
            Assert.Equal("97", reuseEventsRes.Stdout)
            assertFileUnlocked reuseEventsPath
            File.Delete reuseEventsPath

            let sameFileReusePath = Path.Combine(directory, "loh-same-file-reuse.bin")
            File.WriteAllBytes(sameFileReusePath, [| 99uy; 100uy |])
            runP0
                [ "--session"; "reuse-seed"; "-e"
                  "System.AppDomain.CurrentDomain.SetData(\"same-file-streams\", Array.init 384 (fun _ -> System.IO.File.OpenRead(fsi.CommandLineArgs.[1])))"
                  sameFileReusePath ]
            |> assertRun
            let sameFileReuseCode =
                String.concat "\n"
                    [ "open System"
                      "open System.IO"
                      "let leakSameFile () ="
                      "    let streams = AppDomain.CurrentDomain.GetData(\"same-file-streams\") :?> FileStream array"
                      "    for s in streams do s.Dispose()"
                      "    AppDomain.CurrentDomain.SetData(\"same-file-streams\", null)"
                      "    let arr = Array.zeroCreate<FileStream> 32768"
                      "    arr.[0] <- File.OpenRead(fsi.CommandLineArgs.[1])"
                      "    Console.Write(arr.[0].ReadByte())"
                      "leakSameFile ()" ]
            let sameFileRes = runP0 [ "-e"; sameFileReuseCode; sameFileReusePath ]
            assertRun sameFileRes
            Assert.Equal("99", sameFileRes.Stdout)
            assertFileUnlocked sameFileReusePath
            File.Delete sameFileReusePath


            if OperatingSystem.IsWindows() then
                let rxSameFilePath = Path.Combine(directory, "loh-rx-same-file-reuse.bin")
                File.WriteAllBytes(rxSameFilePath, [| 101uy; 102uy |])
                let rxSeedCode =
                    String.concat "\n"
                        [ "open System"
                          "open System.Runtime.InteropServices"
                          "module RxNative ="
                          "    [<DllImport(\"kernel32.dll\", SetLastError = true)>]"
                          "    extern nativeint CreateFileW([<MarshalAs(UnmanagedType.LPWStr)>] string lpFileName, uint32 dwDesiredAccess, uint32 dwShareMode, nativeint lpSecurityAttributes, uint32 dwCreationDisposition, uint32 dwFlagsAndAttributes, nativeint hTemplateFile)"
                          "    [<DllImport(\"kernel32.dll\", SetLastError = true)>]"
                          "    extern uint32 GetFileType(nativeint hFile)"
                          "let p = fsi.CommandLineArgs.[1]"
                          "let handles ="
                          "    Array.init 384 (fun _ ->"
                          "        let h = RxNative.CreateFileW(p, 0x80000000u ||| 0x20000000u, 7u, 0n, 3u, 0x80u, 0n)"
                          "        if h = -1n then failwith \"CreateFileW failed\""
                          "        RxNative.GetFileType h |> ignore"
                          "        h)"
                          "AppDomain.CurrentDomain.SetData(\"rx-handles\", handles)" ]
                runP0 [ "--session"; "reuse-seed"; "-e"; rxSeedCode; rxSameFilePath ] |> assertRun
                let rxLeakCode =
                    String.concat "\n"
                        [ "open System"
                          "open System.IO"
                          "open System.Runtime.InteropServices"
                          "module RxClose ="
                          "    [<DllImport(\"kernel32.dll\", SetLastError = true)>]"
                          "    extern bool CloseHandle(nativeint hObject)"
                          "let leakRxSameFile () ="
                          "    let handles = AppDomain.CurrentDomain.GetData(\"rx-handles\") :?> nativeint array"
                          "    for h in handles do RxClose.CloseHandle h |> ignore"
                          "    AppDomain.CurrentDomain.SetData(\"rx-handles\", null)"
                          "    let arr = Array.zeroCreate<FileStream> 32768"
                          "    arr.[0] <- File.OpenRead(fsi.CommandLineArgs.[1])"
                          "    Console.Write(arr.[0].ReadByte())"
                          "leakRxSameFile ()" ]
                let rxLeakRes = runP0 [ "-e"; rxLeakCode; rxSameFilePath ]
                assertRun rxLeakRes
                Assert.Equal("101", rxLeakRes.Stdout)
                assertFileUnlocked rxSameFilePath
                File.Delete rxSameFilePath

                // Single-handle reuse on `Environment.ProcessPath` (native host executable has no resident handle
                // at startup, so a single pre-existing read handle closed and reopened into the same numeric slot
                // into a local LOH array must still escalate to Gen-2 and release before `fsion` returns).
                let singleProcSeedCode =
                    String.concat "\n"
                        [ "open System"
                          "open System.IO"
                          "let target = Environment.ProcessPath"
                          "let fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)"
                          "AppDomain.CurrentDomain.SetData(\"single-proc-stream\", fs)"
                          "Console.Write target" ]
                let seedProcRes = runP0 [ "--session"; "reuse-seed"; "-e"; singleProcSeedCode ]
                assertRun seedProcRes
                let daemonExePath = seedProcRes.Stdout
                let singleProcLeakCode =
                    String.concat "\n"
                        [ "open System"
                          "open System.IO"
                          "let leakSingleProc () ="
                          "    let oldFs = AppDomain.CurrentDomain.GetData(\"single-proc-stream\") :?> FileStream"
                          "    let oldHandle = oldFs.SafeFileHandle.DangerousGetHandle()"
                          "    let target = Environment.ProcessPath"
                          "    oldFs.Dispose()"
                          "    AppDomain.CurrentDomain.SetData(\"single-proc-stream\", null)"
                          "    let arr = Array.zeroCreate<FileStream> 32768"
                          "    let reopened = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)"
                          "    let newHandle = reopened.SafeFileHandle.DangerousGetHandle()"
                          "    arr.[0] <- reopened"
                          "    let b = arr.[0].ReadByte()"
                          "    Console.Write(sprintf \"%b:%d\" (oldHandle = newHandle) b)"
                          "leakSingleProc ()" ]
                let singleProcLeakRes = runP0 [ "-e"; singleProcLeakCode ]
                assertRun singleProcLeakRes
                Assert.StartsWith("true:", singleProcLeakRes.Stdout)
                use exclusiveReadOnExe =
                    new FileStream(daemonExePath, FileMode.Open, FileAccess.Read, FileShare.None)
                exclusiveReadOnExe.Close()
                // Late-loaded BCL disk assembly (not in builtinPrelude.warmAssemblies) must not
                // permanently flip HasPreExistingUserFiles = true and force Gen-2 on subsequent stateless runs.
                // Run against the warm-pool daemon (`context`) so `System.GC.ConserveMemory = 9` cold session
                // creation under `--pool-size 0` doesn't trigger natural Gen-2 collections on every cold compile.
                let hasZeroGen2Step (counts: int[]) =
                    [ 0 .. counts.Length - 2 ] |> List.exists (fun i -> counts.[i + 1] = counts.[i])
                use warmCtx = new FsionContext(p0WorkDir)
                assertRun (warmCtx.Run([ "--pool-size"; "4"; "warmup" ], timeoutMs = 120000))
                let xmlLoadRes = warmCtx.Run([ "-e"; "let d = System.Xml.XmlDocument()\nd.LoadXml(\"<a/>\")\nSystem.Console.Write(d.DocumentElement.Name)" ], timeoutMs = 60000)
                assertRun xmlLoadRes
                Assert.Equal("a", xmlLoadRes.Stdout)
                assertRun (warmCtx.Run([ "warmup" ], timeoutMs = 120000))
                let gen2Counts =
                    [| for _ in 1 .. 4 ->
                        let r = warmCtx.Run([ "-e"; "System.Console.Write(System.GC.CollectionCount 2)" ], timeoutMs = 60000)
                        assertRun r
                        Int32.Parse(r.Stdout.Trim()) |]
                Assert.True(hasZeroGen2Step gen2Counts, sprintf "forced per-run Gen-2 after Xml load: %A" gen2Counts)
                // Loading a Default-ALC BCL DLL while a user FileStream temporarily holds the same DLL open
                // must promote the remaining CoreCLR PE handle once the user stream closes, avoiding per-run Gen-2.
                let concurrentLoadCode =
                    String.concat "\n"
                        [ "let dir = System.IO.Path.GetDirectoryName(typeof<obj>.Assembly.Location)"
                          "let path = System.IO.Path.Combine(dir, \"System.Xml.XPath.XDocument.dll\")"
                          "use stream = System.IO.File.OpenRead path"
                          "System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path) |> ignore" ]
                assertRun (warmCtx.Run([ "-e"; concurrentLoadCode ], timeoutMs = 60000))
                assertRun (warmCtx.Run([ "warmup" ], timeoutMs = 120000))
                let afterLoadCounts =
                    [| for _ in 1 .. 4 ->
                        let r = warmCtx.Run([ "-e"; "System.Console.Write(System.GC.CollectionCount 2)" ], timeoutMs = 60000)
                        assertRun r
                        Int32.Parse(r.Stdout.Trim()) |]
                Assert.True(hasZeroGen2Step afterLoadCounts, sprintf "forced per-run Gen-2 after concurrent load: %A" afterLoadCounts)
            if OperatingSystem.IsLinux() then
                // On Linux `/proc/self/fd`, enumerating the directory produces a vanished self-FD (`ENOENT`)
                // and CoreCLR runtime FDs (e.g. CLR/DLL files), neither of which may force Gen-2 on stateless runs.
                // Use a dedicated `--pool-size 4` daemon with `warmup` so all 4 turns hit warm sessions without
                // cold session creation triggering `ConserveMemory=9` Gen-2 collections.
                let linuxConcurrentLoadCode =
                    String.concat "\n"
                        [ "let dir = System.IO.Path.GetDirectoryName(typeof<obj>.Assembly.Location)"
                          "let path = System.IO.Path.Combine(dir, \"System.Xml.XPath.XDocument.dll\")"
                          "use stream = System.IO.File.OpenRead path"
                          "System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path) |> ignore" ]
                let hasZeroGen2Step (counts: int[]) =
                    [ 0 .. counts.Length - 2 ] |> List.exists (fun i -> counts.[i + 1] = counts.[i])
                use warmLinuxCtx = new FsionContext(p0WorkDir)
                assertRun (warmLinuxCtx.Run([ "--pool-size"; "4"; "warmup" ], timeoutMs = 120000))
                let warmLinuxCounts =
                    [| for _ in 1 .. 4 ->
                        let r = warmLinuxCtx.Run([ "-e"; "System.Console.Write(System.GC.CollectionCount 2)" ], timeoutMs = 60000)
                        assertRun r
                        Int32.Parse(r.Stdout.Trim()) |]
                Assert.True(hasZeroGen2Step warmLinuxCounts, sprintf "forced per-run Gen-2 on warm Linux daemon: %A" warmLinuxCounts)
                assertRun (warmLinuxCtx.Run([ "-e"; linuxConcurrentLoadCode ], timeoutMs = 60000))
                assertRun (warmLinuxCtx.Run([ "warmup" ], timeoutMs = 120000))
                let linuxAfterLoadCounts =
                    [| for _ in 1 .. 4 ->
                        let r = warmLinuxCtx.Run([ "-e"; "System.Console.Write(System.GC.CollectionCount 2)" ], timeoutMs = 60000)
                        assertRun r
                        Int32.Parse(r.Stdout.Trim()) |]
                Assert.True(
                    hasZeroGen2Step linuxAfterLoadCounts,
                    sprintf "forced per-run Gen-2 on Linux after concurrent load: %A" linuxAfterLoadCounts)
            let disposeEntered = Path.Combine(directory, "dispose-entered.txt")
            let disposeRelease = Path.Combine(directory, "dispose-release.txt")
            let slowDisposeScript = Path.Combine(directory, "slow-dispose.fsx")
            File.WriteAllText(
                slowDisposeScript,
                sprintf
                    "type Blocker() =\n    interface System.IDisposable with\n        member _.Dispose() =\n            System.IO.File.WriteAllText(@\"%s\", \"1\")\n            while not (System.IO.File.Exists(@\"%s\")) do\n                System.Threading.Thread.Sleep 20\nlet b = new Blocker()\nSystem.Console.Write(\"a-done\")\n"
                    disposeEntered
                    disposeRelease)
            try
                let taskA = System.Threading.Tasks.Task.Run(fun () -> context.Run([ slowDisposeScript ], timeoutMs = 60000))
                awaitCondition "script A never entered Dispose()" (fun () -> File.Exists disposeEntered)
                let resB = context.Run([ "-e"; "System.Console.Write(\"b-unblocked\")" ], timeoutMs = 15000)
                assertRun resB
                Assert.Equal("b-unblocked", resB.Stdout)
                Assert.False(taskA.IsCompleted, "script A completed before Dispose() was released")
                writeFile directory "dispose-release.txt" "go"
                let resA = taskA.GetAwaiter().GetResult()
                assertRun resA
                Assert.Equal("a-done", resA.Stdout)
            finally
                writeFile directory "dispose-release.txt" "go")
