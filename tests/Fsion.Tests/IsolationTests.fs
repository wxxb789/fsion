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
                holdNamed.GetAwaiter().GetResult() |> assertRun)

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
