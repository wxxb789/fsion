module Fsion.Tests.PreludeTests

open System
open System.IO
open System.Text
open System.Text.Json
open Xunit
open Xunit.Abstractions
open Fsion.Tests.TestInfra

type PreludeTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.``alternating six builtin and bare turns retain both warmed pools`` () =
        withContext "prelude-tag-alternation" (fun context _ ->
            let run args = context.Run(args, timeoutMs = 120000)
            let bare = [ "--no-prelude" ]
            for flags in [ []; bare ] do
                let warm = run (flags @ [ "warmup" ])
                Assert.True(warm.ExitCode = 0, warm.Stderr)
                Assert.Contains("warm=True", warm.Stdout)

            let before = context.MetricsOrFail()
            for i in 1 .. 6 do
                let result =
                    if i % 2 = 1 then
                        run [ "-e"; $"System.Console.Write(Fsion.envOr \"FSION_TEST_ALTERNATION_6\" \"builtin-{i}\")" ]
                    else
                        run (bare @ [ "-e"; $"System.Console.Write(\"bare-{i}\")" ])
                Assert.True(result.ExitCode = 0, result.Stderr)
                Assert.Equal((if i % 2 = 1 then $"builtin-{i}" else $"bare-{i}"), result.Stdout)

            let after = context.MetricsOrFail()
            Assert.Equal(before.Executed + 6, after.Executed)
            Assert.Equal(before.ColdClaims, after.ColdClaims))

    [<Fact>]
    member _.``built-in Fsion helpers are preloaded on warm sessions and --no-prelude hides them`` () =
        withContext "prelude-builtin" (fun context directory ->
            let warmup = context.Run([ "warmup" ], timeoutMs = 180000)
            Assert.Equal(0, warmup.ExitCode)

            let before = context.MetricsOrFail()
            Assert.True(before.PoolWarm > 0, "warm pool was empty after warmup")

            writeFile directory "in.txt" "hello-prelude"

            let code =
                String.concat
                    "\n"
                    [ "Fsion.writeText \"out.txt\" \"written-by-fsion\""
                      "printfn \"cwd=%s\" (Fsion.cwd())"
                      "printfn \"path=%s\" (Fsion.path \"in.txt\")"
                      "printfn \"env=%s\" (match Fsion.env \"FSION_HELPER_TEST\" with | Some value -> value | None -> \"missing\")"
                      "printfn \"envMissing=%b\" ((Fsion.env \"FSION_HELPER_TEST_MISSING_9A1B\").IsNone)"
                      "printfn \"envOr=%s\" (Fsion.envOr \"FSION_HELPER_TEST_MISSING_9A1B\" \"fallback\")"
                      "printfn \"read=%s\" (Fsion.readText \"in.txt\")"
                      "printfn \"json=%s\" ((Fsion.json \"{\\\"k\\\":\\\"v\\\"}\").RootElement.GetProperty(\"k\").GetString())" ]

            let result =
                context.Run(
                    [ "-e"; code ],
                    timeoutMs = 60000,
                    extraEnv = [ "FSION_HELPER_TEST", "present" ]
                )

            Assert.Equal(0, result.ExitCode)
            Assert.Contains("cwd=" + directory, result.Stdout, StringComparison.OrdinalIgnoreCase)
            Assert.Contains("path=" + Path.Combine(directory, "in.txt"), result.Stdout, StringComparison.OrdinalIgnoreCase)
            Assert.Contains("env=present", result.Stdout)
            Assert.Contains("envMissing=true", result.Stdout)
            Assert.Contains("envOr=fallback", result.Stdout)
            Assert.Contains("read=hello-prelude", result.Stdout)
            Assert.Contains("json=v", result.Stdout)
            Assert.Equal("written-by-fsion", File.ReadAllText(Path.Combine(directory, "out.txt")))

            let after = context.MetricsOrFail()
            Assert.Equal(before.ColdClaims, after.ColdClaims)

            let hidden =
                context.Run([ "--no-prelude"; "-e"; "printfn \"%s\" (Fsion.cwd())" ], timeoutMs = 60000)

            Assert.Equal(1, hidden.ExitCode)
            Assert.Contains("FS0039", hidden.Stderr)

            let bare =
                context.Run([ "--no-prelude"; "-e"; "printfn \"bare\"; exit 3" ], timeoutMs = 120000)

            Assert.Equal(3, bare.ExitCode)
            Assert.Equal("bare" + Environment.NewLine, bare.Stdout))

    [<Fact>]
    member _.``configured prelude with a nuget package preloads bindings and refreshes on edit`` () =
        withContext "prelude-user" (fun context directory ->
            let cacheDirectory = createTempDir "prelude-nuget-cache"
            let packageId = uniquePackageId "fsiontiny"
            let packagesDirectory = Path.Combine(directory, "packages")
            let preludePath = Path.Combine(directory, "prelude.fsx")

            let environment =
                [ "FSION_PRELUDE", preludePath
                  "FSION_CACHE_DIR", cacheDirectory
                  "NUGET_PACKAGES", packagesDirectory ]

            try
                let feed = createTinyPackage directory packageId
                writeNugetConfig directory feed

                let preloadCountPath = Path.Combine(directory, "preload-count.txt")

                File.WriteAllText(
                    preludePath,
                    "#r \"nuget: "
                    + packageId
                    + ", 1.0.0\"\n"
                    + "System.IO.File.AppendAllText(@\""
                    + preloadCountPath.Replace("\"", "\"\"")
                    + "\", \"1\\n\")\n"
                    + "let preludeGreeting () = \"first\"\n",
                    UTF8Encoding(true)
                )

                let warmup = context.Run([ "warmup" ], timeoutMs = 240000, extraEnv = environment)

                Assert.Equal(0, warmup.ExitCode)

                let before = context.MetricsOrFail()
                Assert.True(before.PoolWarm > 0, "warm pool was empty after warmup")
                let preloadedBeforeRun = File.ReadAllLines preloadCountPath
                Assert.True(
                    preloadedBeforeRun.Length >= before.PoolWarm,
                    "prelude.fsx was not evaluated during warmup pool creation"
                )

                let code = "printfn \"%s|%s\" (preludeGreeting()) (FsionTinyPkgNs.Greeter.Hello())"

                let first = context.Run([ "-e"; code ], timeoutMs = 120000, extraEnv = environment)
                Assert.Equal(0, first.ExitCode)
                Assert.Equal("first|tiny-package-ok", first.Stdout.Trim())

                let afterFirst = context.MetricsOrFail()
                Assert.Equal(before.ColdClaims, afterFirst.ColdClaims)

                let daemonPid = context.DaemonPid()
                Assert.True(daemonPid.IsSome, "daemon was not reported as running")
                writeFile
                    directory
                    "prelude.fsx"
                    ("#r \"nuget: " + packageId + ", 1.0.0\"\n" + "let preludeGreeting () = \"edited\"\n")

                let second = context.Run([ "-e"; code ], timeoutMs = 120000, extraEnv = environment)
                Assert.Equal(0, second.ExitCode)
                Assert.Equal("edited|tiny-package-ok", second.Stdout.Trim())
                Assert.Equal(daemonPid, context.DaemonPid())
            finally
                purgeDependencyManagerCache packageId
                tryDeleteDirectory cacheDirectory)

    [<Fact>]
    member _.``default user prelude resolves POSIX XDG_CONFIG_HOME and keeps Windows SpecialFolder semantics`` () =
        withContext "prelude-platform-config" (fun context directory ->
            let configHome = Path.Combine(directory, "config-home")
            writeFile configHome "fsion/prelude.fsx" "let platformPreludeValue = \"platform-config\"\n"

            if OperatingSystem.IsWindows() then
                let result =
                    context.Run(
                        [ "-e"; "System.Console.Write(\"windows-special-folder-ok\")" ],
                        timeoutMs = 120000,
                        extraEnv = [ "FSION_CONFIG_DIR", null; "FSION_PRELUDE", null; "APPDATA", configHome ]
                    )

                Assert.True(result.ExitCode = 0, result.Stderr)
                Assert.Equal("windows-special-folder-ok", result.Stdout)
            else
                let result =
                    context.Run(
                        [ "-e"; "System.Console.Write(platformPreludeValue)" ],
                        timeoutMs = 120000,
                        extraEnv = [ "FSION_CONFIG_DIR", null; "FSION_PRELUDE", null; "XDG_CONFIG_HOME", configHome ]
                    )

                Assert.True(result.ExitCode = 0, result.Stderr)
                Assert.Equal("platform-config", result.Stdout)

                let homeFallback = Path.Combine(directory, "posix-home")
                writeFile homeFallback ".config/fsion/prelude.fsx" "let homePreludeValue = \"posix-home-config\"\n"
                let homeResult =
                    context.Run(
                        [ "-e"; "System.Console.Write(homePreludeValue)" ],
                        timeoutMs = 120000,
                        extraEnv = [ "FSION_CONFIG_DIR", null; "FSION_PRELUDE", null; "XDG_CONFIG_HOME", null; "HOME", homeFallback ]
                    )

                Assert.True(homeResult.ExitCode = 0, homeResult.Stderr)
                Assert.Equal("posix-home-config", homeResult.Stdout))

    [<Fact>]
    member _.``prelude resolution prefers the flag, then the environment, then the config directory`` () =
        withContext "prelude-precedence" (fun context directory ->
            let configDirectory = Path.Combine(directory, "fsion-config")
            let environment = [ "FSION_CONFIG_DIR", configDirectory ]

            writeFile configDirectory "prelude.fsx" "let preludeSource () = \"config\"\n"

            let fromConfig =
                context.Run([ "-e"; "printfn \"%s\" (preludeSource())" ], timeoutMs = 120000, extraEnv = environment)

            Assert.Equal(0, fromConfig.ExitCode)
            Assert.Equal("config" + Environment.NewLine, fromConfig.Stdout)

            let envPrelude = Path.Combine(directory, "env-prelude.fsx")
            writeFile directory "env-prelude.fsx" "let preludeSource () = \"env\"\n"

            let fromEnv =
                context.Run(
                    [ "-e"; "printfn \"%s\" (preludeSource())" ],
                    timeoutMs = 120000,
                    extraEnv = environment @ [ "FSION_PRELUDE", envPrelude ]
                )

            Assert.Equal(0, fromEnv.ExitCode)
            Assert.Equal("env" + Environment.NewLine, fromEnv.Stdout)

            let flagPrelude = Path.Combine(directory, "flag-prelude.fsx")
            writeFile directory "flag-prelude.fsx" "let preludeSource () = \"flag\"\n"

            let fromFlag =
                context.Run(
                    [ "--prelude"; flagPrelude; "-e"; "printfn \"%s\" (preludeSource())" ],
                    timeoutMs = 120000,
                    extraEnv = environment @ [ "FSION_PRELUDE", envPrelude ]
                )

            Assert.Equal(0, fromFlag.ExitCode)
            Assert.Equal("flag" + Environment.NewLine, fromFlag.Stdout)

            let hidden =
                context.Run(
                    [ "--no-prelude"; "-e"; "printfn \"%s\" (preludeSource())" ],
                    timeoutMs = 120000,
                    extraEnv = environment
                )

            Assert.Equal(1, hidden.ExitCode)
            Assert.Contains("FS0039", hidden.Stderr)

            let missingPath = Path.Combine(directory, "no-such-prelude.fsx")
            let missing = context.Run([ "--prelude"; missingPath; "-e"; "1" ], timeoutMs = 60000)

            Assert.Equal(1, missing.ExitCode)
            Assert.Contains("prelude file not found", missing.Stderr)

            let missingJson = context.Run([ "--json"; "--prelude"; missingPath; "-e"; "1" ], timeoutMs = 60000)
            Assert.Equal(1, missingJson.ExitCode)
            use missingDocument = JsonDocument.Parse missingJson.Stdout
            Assert.Equal(1, missingDocument.RootElement.GetProperty("exitCode").GetInt32())
            Assert.Contains("prelude file not found", missingDocument.RootElement.GetProperty("stderr").GetString())
            let conflicting =
                context.Run([ "--prelude"; flagPrelude; "--no-prelude"; "-e"; "1" ], timeoutMs = 60000)

            Assert.Equal(1, conflicting.ExitCode)
            Assert.Contains("cannot be combined", conflicting.Stderr)

            let staleEnvironment =
                environment @ [ "FSION_PRELUDE", Path.Combine(directory, "stale-prelude.fsx") ]

            let version = context.Run([ "--version" ], timeoutMs = 60000, extraEnv = staleEnvironment)
            Assert.Equal(0, version.ExitCode)
            Assert.StartsWith("fsion ", version.Stdout)

            let status = context.Run([ "daemon"; "status" ], timeoutMs = 60000, extraEnv = staleEnvironment)
            Assert.Equal(0, status.ExitCode))

    [<Fact>]
    member _.``a named session recycles when its prelude tag changes`` () =
        withContext "prelude-named-tag" (fun context directory ->
            let preludeA = Path.Combine(directory, "prelude-a.fsx")
            let preludeB = Path.Combine(directory, "prelude-b.fsx")

            writeFile
                directory
                "prelude-a.fsx"
                "let mutable preludeTurns = 0\nlet onlyInPreludeA () = \"only-A\"\nlet preludeVersion () = \"A\"\n"

            writeFile directory "prelude-b.fsx" "let preludeVersion () = \"B\"\n"

            let first =
                context.Run(
                    [ "--prelude"
                      preludeA
                      "--session"
                      "alpha"
                      "-e"
                      "preludeTurns <- preludeTurns + 1\nprintfn \"%s|%d|%s\" (preludeVersion()) preludeTurns (onlyInPreludeA())" ],
                    timeoutMs = 120000
                )

            Assert.Equal(0, first.ExitCode)
            Assert.Equal("A|1|only-A" + Environment.NewLine, first.Stdout)

            let secondSame =
                context.Run(
                    [ "--prelude"
                      preludeA
                      "--session"
                      "alpha"
                      "-e"
                      "preludeTurns <- preludeTurns + 1\nprintfn \"%d\" preludeTurns" ],
                    timeoutMs = 120000
                )

            Assert.Equal(0, secondSame.ExitCode)
            Assert.Equal("2" + Environment.NewLine, secondSame.Stdout)

            let daemonPid = context.DaemonPid()
            Assert.True(daemonPid.IsSome, "daemon was not reported as running")

            let switched =
                context.Run(
                    [ "--prelude"; preludeB; "--session"; "alpha"; "-e"; "printfn \"%s\" (preludeVersion())" ],
                    timeoutMs = 120000
                )

            Assert.Equal(0, switched.ExitCode)
            Assert.Equal("B" + Environment.NewLine, switched.Stdout)

            let oldBindingGone =
                context.Run(
                    [ "--prelude"; preludeB; "--session"; "alpha"; "-e"; "printfn \"%s\" (onlyInPreludeA())" ],
                    timeoutMs = 120000
                )

            Assert.Equal(1, oldBindingGone.ExitCode)
            Assert.Contains("FS0039", oldBindingGone.Stderr)
            Assert.Equal(daemonPid, context.DaemonPid()))

    [<Fact>]
    member _.``a broken configured prelude unloads its aborted session and stops pool churn until fixed`` () =
        withContext "prelude-broken-unload" (fun context directory ->
            let brokenPath = Path.Combine(directory, "broken-prelude.fsx")
            let counterPath = Path.Combine(directory, "broken-attempts.txt")
            let gatePath = Path.Combine(directory, "prelude-ready.txt")

            writeFile
                directory
                "broken-prelude.fsx"
                ("System.IO.File.AppendAllText(@\""
                 + counterPath.Replace("\"", "\"\"")
                 + "\", \"attempt\\n\")\n"
                 + "if not (System.IO.File.Exists(@\""
                 + gatePath.Replace("\"", "\"\"")
                 + "\")) then failwith \"prelude-boom\"\n"
                 + "let recoveredValue () = 42\n")

            let failed =
                context.Run([ "--prelude"; brokenPath; "-e"; "1" ], timeoutMs = 120000)

            Assert.Equal(1, failed.ExitCode)
            Assert.Contains("prelude-boom", failed.Stderr)
            let daemonPid = context.DaemonPid()
            Assert.True(daemonPid.IsSome, "daemon was not reported as running")

            System.Threading.Thread.Sleep 800
            let attemptsBeforeSecond = File.ReadAllLines(counterPath).Length

            let failedAgain =
                context.Run([ "--prelude"; brokenPath; "-e"; "1" ], timeoutMs = 120000)

            Assert.Equal(1, failedAgain.ExitCode)
            Assert.Contains("prelude-boom", failedAgain.Stderr)

            System.Threading.Thread.Sleep 800
            let attemptsAfterSecond = File.ReadAllLines(counterPath).Length
            Assert.Equal(attemptsBeforeSecond + 1, attemptsAfterSecond)


            let failedWarmup =
                context.Run([ "--prelude"; brokenPath; "warmup" ], timeoutMs = 120000)

            Assert.Equal(1, failedWarmup.ExitCode)
            Assert.Contains("prelude-boom", failedWarmup.Stderr)
            let settled = context.MetricsOrFail()
            Assert.Equal(0, settled.PoolWarm)
            Assert.Equal(0, settled.AliveDisposedSessions)
            Assert.Equal(0, settled.ScriptAssemblies)

            writeFile directory "prelude-ready.txt" "ready"

            let recovered =
                context.Run([ "--prelude"; brokenPath; "-e"; "printfn \"%d\" (recoveredValue())" ], timeoutMs = 120000)

            Assert.Equal(0, recovered.ExitCode)
            Assert.Equal("42" + Environment.NewLine, recovered.Stdout)

            let refillDeadline = DateTime.UtcNow.AddSeconds 30.0
            let mutable refilled = false

            while not refilled && DateTime.UtcNow < refillDeadline do
                if context.MetricsOrFail().PoolWarm >= 1 then
                    refilled <- true
                else
                    System.Threading.Thread.Sleep 100

            Assert.True(refilled, "warm pool did not resume replenishing after transient prelude recovery")
            Assert.Equal(daemonPid, context.DaemonPid()))

    [<Fact>]
    member _.``same-length prelude edit within the settle window invalidates warm sessions and does not loop replenisher`` () =
        withContext "prelude-same-length-edit" (fun context directory ->
            let preludePath = Path.Combine(directory, "prelude.fsx")
            writeFile directory "prelude.fsx" "let sameLen () = \"A\"\n"

            let warmup = context.Run([ "--prelude"; preludePath; "warmup" ], timeoutMs = 120000)
            Assert.True(warmup.ExitCode = 0, warmup.Stderr)

            let first = context.Run([ "--prelude"; preludePath; "-e"; "System.Console.Write(sameLen())" ], timeoutMs = 60000)
            Assert.True(first.ExitCode = 0, first.Stderr)
            Assert.Equal("A", first.Stdout)

            let stamped = File.GetLastWriteTimeUtc preludePath
            writeFile directory "prelude.fsx" "let sameLen () = \"B\"\n"
            File.SetLastWriteTimeUtc(preludePath, stamped)

            let second = context.Run([ "--prelude"; preludePath; "-e"; "System.Console.Write(sameLen())" ], timeoutMs = 60000)
            Assert.True(second.ExitCode = 0, second.Stderr)
            Assert.Equal("B", second.Stdout)

            let settled = context.MetricsOrFail()
            Assert.Equal(settled.PoolTarget, settled.PoolWarm)
            Assert.Equal(0, settled.AliveDisposedSessions))

    [<Fact>]
    member _.``background file-prelude replenish stays isolated from concurrent request environment and stale failure`` () =
        withContext "prelude-replenish-isolation" (fun context directory ->
            let waitFor message (condition: unit -> bool) =
                Assert.True(System.Threading.SpinWait.SpinUntil(Func<bool>(condition), TimeSpan.FromSeconds 30.0), message)
            let preludePath = Path.Combine(directory, "prelude.fsx")
            let enteredPath = Path.Combine(directory, "prelude-entered.txt")
            let releasePath = Path.Combine(directory, "prelude-release.txt")
            let callerEnteredPath = Path.Combine(directory, "caller-entered.txt")
            let callerReleasePath = Path.Combine(directory, "caller-release.txt")
            let callerSubdir = Path.Combine(directory, "caller-cwd")
            Directory.CreateDirectory callerSubdir |> ignore
            writeFile callerSubdir "settings.txt" "wrong-caller-settings"

            let slowPrelude =
                sprintf
                    "System.IO.File.WriteAllText(@\"%s\", \"1\")\nwhile not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20\nlet relSettings = if System.IO.File.Exists(\"settings.txt\") then System.IO.File.ReadAllText(\"settings.txt\") else \"no-rel-settings\"\nlet capturedProbe = (match System.Environment.GetEnvironmentVariable(\"FSION_REPLENISH_PROBE\") with null -> \"unset\" | v -> v) + \"|\" + relSettings\n"
                    enteredPath
                    releasePath

            writeFile directory "prelude.fsx" "let capturedProbe = \"seed\"\n"
            let warmBuiltin = context.Run([ "--pool-size"; "1"; "warmup" ], timeoutMs = 120000)
            Assert.True(warmBuiltin.ExitCode = 0, warmBuiltin.Stderr)
            let warmFile = context.Run([ "--pool-size"; "1"; "--prelude"; preludePath; "warmup" ], timeoutMs = 120000)
            Assert.True(warmFile.ExitCode = 0, warmFile.Stderr)

            let drainSeed = context.Run([ "--pool-size"; "1"; "--prelude"; preludePath; "-e"; "System.Console.Write(capturedProbe)" ], timeoutMs = 60000)
            Assert.True(drainSeed.ExitCode = 0, drainSeed.Stderr)
            Assert.Equal("seed", drainSeed.Stdout)

            writeFile directory "prelude.fsx" slowPrelude
            let triggerReplenish =
                System.Threading.Tasks.Task.Run(fun () ->
                    context.Run([ "--pool-size"; "1"; "--prelude"; preludePath; "warmup" ], timeoutMs = 120000))

            waitFor "file prelude entered replenisher" (fun () -> File.Exists enteredPath)

            let concurrentBuiltin =
                System.Threading.Tasks.Task.Run(fun () ->
                    runProcess
                        Fsion.Tests.TestInfra.fsionBinary
                        [ "--pool-size"; "1"; "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"1\"); while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20; System.Console.Write(System.Environment.GetEnvironmentVariable(\"FSION_REPLENISH_PROBE\"))" callerEnteredPath callerReleasePath ]
                        callerSubdir
                        (context.Environment @ [ "FSION_REPLENISH_PROBE", "leaked" ])
                        None
                        120000)

            System.Threading.Thread.Sleep 200
            Assert.False(File.Exists callerEnteredPath, "concurrent request entered ProcessStateGate while file prelude was evaluating")

            writeFile directory "prelude-release.txt" "go"
            let warmDone = triggerReplenish.GetAwaiter().GetResult()
            Assert.True(warmDone.ExitCode = 0, warmDone.Stderr)

            waitFor "concurrent request entered after file prelude exited" (fun () -> File.Exists callerEnteredPath)
            writeFile directory "caller-release.txt" "go"
            let builtinDone = concurrentBuiltin.GetAwaiter().GetResult()
            Assert.True(builtinDone.ExitCode = 0, builtinDone.Stderr)
            Assert.Equal("leaked", builtinDone.Stdout)

            let checkClean =
                context.Run([ "--pool-size"; "1"; "--prelude"; preludePath; "-e"; "System.Console.Write(capturedProbe)" ], timeoutMs = 60000)
            Assert.True(checkClean.ExitCode = 0, checkClean.Stderr)
            Assert.Equal("unset|no-rel-settings", checkClean.Stdout)

            let failEntered = Path.Combine(directory, "fail-entered.txt")
            let failRelease = Path.Combine(directory, "fail-release.txt")
            writeFile
                directory
                "prelude.fsx"
                (sprintf "System.IO.File.WriteAllText(@\"%s\", \"1\")\nwhile not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20\nfailwith \"stale-v1-boom\"\n" failEntered failRelease)

            let staleWarmup =
                System.Threading.Tasks.Task.Run(fun () ->
                    context.Run([ "--pool-size"; "1"; "--prelude"; preludePath; "warmup" ], timeoutMs = 120000))

            waitFor "failing v1 prelude entered" (fun () -> File.Exists failEntered)
            writeFile directory "prelude.fsx" "let fixedVersion () = \"v2-ok\"\n"

            let fixedWarmup =
                System.Threading.Tasks.Task.Run(fun () ->
                    System.Threading.Thread.Sleep 100
                    context.Run([ "--pool-size"; "1"; "--prelude"; preludePath; "warmup" ], timeoutMs = 120000))

            System.Threading.Thread.Sleep 250
            writeFile directory "fail-release.txt" "go"
            let _ = staleWarmup.GetAwaiter().GetResult()
            let v2WarmResult = fixedWarmup.GetAwaiter().GetResult()
            Assert.True(v2WarmResult.ExitCode = 0, v2WarmResult.Stderr)

            let v2Run =
                context.Run([ "--pool-size"; "1"; "--prelude"; preludePath; "-e"; "System.Console.Write(fixedVersion())" ], timeoutMs = 60000)
            Assert.True(v2Run.ExitCode = 0, v2Run.Stderr)
            Assert.Equal("v2-ok", v2Run.Stdout)

            writeFile directory "prelude.fsx" "failwith \"bg-broken\"\n"
            let brokenWarm = context.Run([ "--pool-size"; "1"; "--prelude"; preludePath; "warmup" ], timeoutMs = 120000)
            Assert.Equal(1, brokenWarm.ExitCode)
            let statusBroken = context.Run([ "daemon"; "status" ], timeoutMs = 30000)
            Assert.Contains("warm=False", statusBroken.Stdout)
            writeFile directory "prelude.fsx" "let autoFixed () = \"bg-auto-ok\"\n"
            waitFor "background replenisher auto-recovered fixed prelude" (fun () ->
                let st = context.Run([ "daemon"; "status" ], timeoutMs = 30000)
                st.Stdout.Contains("warm=True"))

            let lockHoldEntered = Path.Combine(directory, "lock-hold-entered.txt")
            let lockHoldRelease = Path.Combine(directory, "lock-hold-release.txt")
            let coldFileDone = Path.Combine(directory, "cold-file-done.txt")
            writeFile directory "prelude.fsx" "let unblockedFile () = \"file-cold-ok\"\n"
            let poolZeroEndpoint = "fsion-test-p0-" + Guid.NewGuid().ToString("N")
            let poolZeroEnv =
                [ "FSION_PIPE", poolZeroEndpoint
                  "FSIOND_PATH", Fsion.Tests.TestInfra.fsiondBinary
                  "FSION_CONFIG_DIR", Path.Combine(directory, ".fsion-p0-config") ]
            try
                let startP0 = runProcess Fsion.Tests.TestInfra.fsionBinary [ "--pool-size"; "0"; "daemon"; "start" ] callerSubdir poolZeroEnv None 60000
                Assert.True(startP0.ExitCode = 0, startP0.Stderr)
                let reqA =
                    System.Threading.Tasks.Task.Run(fun () ->
                        runProcess
                            Fsion.Tests.TestInfra.fsionBinary
                            [ "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"1\"); while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20; System.Console.Write(\"A-ok\")" lockHoldEntered lockHoldRelease ]
                            callerSubdir
                            poolZeroEnv
                            None
                            120000)
                waitFor "reqA entered non-baseline cwd" (fun () -> File.Exists lockHoldEntered)
                let reqB =
                    System.Threading.Tasks.Task.Run(fun () ->
                        let r =
                            runProcess
                                Fsion.Tests.TestInfra.fsionBinary
                                [ "--prelude"; preludePath; "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", unblockedFile()); System.Console.Write(unblockedFile())" coldFileDone ]
                                callerSubdir
                                poolZeroEnv
                                None
                                120000
                        r)
                System.Threading.Thread.Sleep 200
                let reqC =
                    runProcess
                        Fsion.Tests.TestInfra.fsionBinary
                        [ "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"go\"); System.Console.Write(\"C-ok\")" lockHoldRelease ]
                        callerSubdir
                        poolZeroEnv
                        None
                        60000
                Assert.True(reqC.ExitCode = 0, reqC.Stderr)
                Assert.Equal("C-ok", reqC.Stdout)
                let resA = reqA.GetAwaiter().GetResult()
                Assert.True(resA.ExitCode = 0, resA.Stderr)
                let resB = reqB.GetAwaiter().GetResult()
                Assert.True(resB.ExitCode = 0, resB.Stderr)
                Assert.Equal("file-cold-ok", resB.Stdout)
            finally
                runProcess Fsion.Tests.TestInfra.fsionBinary [ "daemon"; "stop" ] callerSubdir poolZeroEnv None 20000 |> ignore
            let subProject = Path.Combine(directory, "sub-project")
            let relConfig = Path.Combine(subProject, ".fsion-rel")
            writeFile relConfig "prelude.fsx" "let relPrelude () = \"rel-ok\"\n"
            let relEnv = context.Environment @ [ "FSION_CONFIG_DIR", ".fsion-rel"; "FSION_PRELUDE", null ]
            let relWarm =
                runProcess
                    Fsion.Tests.TestInfra.fsionBinary
                    [ "--pool-size"; "1"; "warmup" ]
                    subProject
                    relEnv
                    None
                    120000
            Assert.True(relWarm.ExitCode = 0, relWarm.Stderr)
            let relRun =
                runProcess
                    Fsion.Tests.TestInfra.fsionBinary
                    [ "--pool-size"; "1"; "-e"; "System.Console.Write(relPrelude())" ]
                    subProject
                    relEnv
                    None
                    60000
            Assert.True(relRun.ExitCode = 0, relRun.Stderr)
            Assert.Equal("rel-ok", relRun.Stdout)

            if OperatingSystem.IsWindows() then
                let root = Path.GetPathRoot subProject
                if not (isNull root) && root.Length >= 2 && root.[1] = ':' then
                    let driveRelEnv = context.Environment @ [ "FSION_CONFIG_DIR", root.Substring(0, 2) + ".fsion-rel"; "FSION_PRELUDE", null ]
                    let driveRelRun =
                        runProcess
                            Fsion.Tests.TestInfra.fsionBinary
                            [ "--pool-size"; "1"; "-e"; "System.Console.Write(relPrelude())" ]
                            subProject
                            driveRelEnv
                            None
                            60000
                    Assert.True(driveRelRun.ExitCode = 0, driveRelRun.Stderr)
                    Assert.Equal("rel-ok", driveRelRun.Stdout))
