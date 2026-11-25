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
                Assert.Equal("posix-home-config", homeResult.Stdout)

                if OperatingSystem.IsMacOS() then
                    let macHome = Path.Combine(directory, "mac-home")
                    writeFile macHome "Library/Application Support/fsion/prelude.fsx" "let macPreludeValue = \"mac-app-support\"\n"
                    let macResult =
                        context.Run(
                            [ "-e"; "System.Console.Write(macPreludeValue)" ],
                            timeoutMs = 120000,
                            extraEnv = [ "FSION_CONFIG_DIR", null; "FSION_PRELUDE", null; "XDG_CONFIG_HOME", null; "HOME", macHome ]
                        )
                    Assert.True(macResult.ExitCode = 0, macResult.Stderr)
                    Assert.Equal("mac-app-support", macResult.Stdout))

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

                let mutDir = Path.Combine(directory, "prelude-mut-cwd")
                Directory.CreateDirectory mutDir |> ignore
                writeFile mutDir "settings.txt" "wrong-mutated-cwd"
                let mutPrelude = Path.Combine(directory, "mut-cwd-prelude.fsx")
                writeFile directory "mut-cwd-prelude.fsx" (sprintf "System.IO.Directory.SetCurrentDirectory(@\"%s\")\n" mutDir)
                let daemonDir = Path.GetDirectoryName Fsion.Tests.TestInfra.fsiondBinary
                let mutRun =
                    runProcess
                        Fsion.Tests.TestInfra.fsionBinary
                        [ "--prelude"; mutPrelude; "-e"; "System.Console.Write(System.IO.Directory.GetCurrentDirectory())" ]
                        daemonDir
                        poolZeroEnv
                        None
                        60000
                Assert.True(mutRun.ExitCode = 0, mutRun.Stderr)
                Assert.Equal(daemonDir, mutRun.Stdout)

                let dispEntered = Path.Combine(directory, "disp-entered.txt")
                let dispRelease = Path.Combine(directory, "disp-release.txt")
                let dispCode =
                    sprintf
                        "let h = { new System.IDisposable with member _.Dispose() = System.IO.File.WriteAllText(@\"%s\", \"1\"); while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20 }\nSystem.Console.Write(\"disp-A\")"
                        dispEntered
                        dispRelease
                let dispA =
                    System.Threading.Tasks.Task.Run(fun () ->
                        runProcess Fsion.Tests.TestInfra.fsionBinary [ "-e"; dispCode ] callerSubdir poolZeroEnv None 120000)
                waitFor "stateless IDisposable.Dispose started" (fun () -> File.Exists dispEntered)
                let dispB =
                    runProcess
                        Fsion.Tests.TestInfra.fsionBinary
                        [ "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"go\"); System.Console.Write(\"disp-B\")" dispRelease ]
                        daemonDir
                        poolZeroEnv
                        None
                        60000
                Assert.True(dispB.ExitCode = 0, dispB.Stderr)
                Assert.Equal("disp-B", dispB.Stdout)
                let dispADone = dispA.GetAwaiter().GetResult()
                Assert.True(dispADone.ExitCode = 0, dispADone.Stderr)
                Assert.Equal("disp-A", dispADone.Stdout)

                let leakPreludeEntered = Path.Combine(directory, "leak-prelude-entered.txt")
                let leakPreludeRelease = Path.Combine(directory, "leak-prelude-release.txt")
                let leakSubdir = Path.Combine(directory, "leak-prelude-sub")
                Directory.CreateDirectory leakSubdir |> ignore
                let leakPreludePath = Path.Combine(directory, "leak-prelude.fsx")
                writeFile
                    directory
                    "leak-prelude.fsx"
                    (sprintf
                        "System.IO.Directory.SetCurrentDirectory(@\"%s\")\nSystem.Environment.SetEnvironmentVariable(\"FSION_PRELUDE_INFLIGHT\", \"mutated\")\nSystem.IO.File.WriteAllText(@\"%s\", \"1\")\nwhile not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20\n"
                        leakSubdir
                        leakPreludeEntered
                        leakPreludeRelease)
                let warmBuiltinBaseline =
                    runProcess Fsion.Tests.TestInfra.fsionBinary [ "--pool-size"; "1"; "warmup" ] daemonDir poolZeroEnv None 120000
                Assert.True(warmBuiltinBaseline.ExitCode = 0, warmBuiltinBaseline.Stderr)
                let inflightPrelude =
                    System.Threading.Tasks.Task.Run(fun () ->
                        runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; leakPreludePath; "-e"; "()" ] daemonDir poolZeroEnv None 120000)
                waitFor "mutating file prelude entered" (fun () -> File.Exists leakPreludeEntered)
                let baselineEntered = Path.Combine(directory, "baseline-entered.txt")
                let baselineTurn =
                    System.Threading.Tasks.Task.Run(fun () ->
                        runProcess
                            Fsion.Tests.TestInfra.fsionBinary
                            [ "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"1\"); System.Console.Write(System.IO.Directory.GetCurrentDirectory() + \"|\" + (match System.Environment.GetEnvironmentVariable(\"FSION_PRELUDE_INFLIGHT\") with null -> \"unset\" | v -> v))" baselineEntered ]
                            daemonDir
                            poolZeroEnv
                            None
                            120000)
                System.Threading.Thread.Sleep 150
                Assert.False(File.Exists baselineEntered, "baseline turn entered while file prelude was mutating cwd/env")
                writeFile directory "leak-prelude-release.txt" "go"
                let inflightDone = inflightPrelude.GetAwaiter().GetResult()
                Assert.True(inflightDone.ExitCode = 0, inflightDone.Stderr)
                let baselineDone = baselineTurn.GetAwaiter().GetResult()
                Assert.True(baselineDone.ExitCode = 0, baselineDone.Stderr)
                Assert.Equal(daemonDir + "|unset", baselineDone.Stdout)

                let finEntered = Path.Combine(directory, "fin-entered.txt")
                let finRelease = Path.Combine(directory, "fin-release.txt")
                let finCode =
                    sprintf
                        "type Fin() =\n    override _.Finalize() =\n        System.IO.File.WriteAllText(@\"%s\", \"1\")\n        while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20\nlet hold = Fin()\nSystem.Console.Write(\"fin-armed\")"
                        finEntered
                        finRelease
                let finEndpoint = "fsion-test-fin-" + Guid.NewGuid().ToString("N")
                let finEnv =
                    [ "FSION_PIPE", finEndpoint
                      "FSIOND_PATH", Fsion.Tests.TestInfra.fsiondBinary
                      "FSION_CONFIG_DIR", Path.Combine(directory, ".fsion-fin-config") ]
                try
                    let finWarm =
                        runProcess Fsion.Tests.TestInfra.fsionBinary [ "--pool-size"; "0"; "--session-ttl"; "0.01"; "warmup" ] daemonDir finEnv None 120000
                    Assert.True(finWarm.ExitCode = 0, finWarm.Stderr)
                    let finArm =
                        runProcess Fsion.Tests.TestInfra.fsionBinary [ "--session"; "fin-sess"; "-e"; finCode ] daemonDir finEnv None 60000
                    Assert.True(finArm.ExitCode = 0, finArm.Stderr)
                    Assert.Equal("fin-armed", finArm.Stdout)
                    waitFor "idle unload finalizer started" (fun () -> File.Exists finEntered)
                    let finUnblock =
                        runProcess
                            Fsion.Tests.TestInfra.fsionBinary
                            [ "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"go\"); System.Console.Write(\"fin-unblocked\")" finRelease ]
                            daemonDir
                            finEnv
                            None
                            60000
                    Assert.True(finUnblock.ExitCode = 0, finUnblock.Stderr)
                    Assert.Equal("fin-unblocked", finUnblock.Stdout)
                finally
                    runProcess Fsion.Tests.TestInfra.fsionBinary [ "daemon"; "stop" ] daemonDir finEnv None 20000 |> ignore

                let thrEndpoint = "fsion-test-thr-" + Guid.NewGuid().ToString("N")
                let thrLog = Path.Combine(directory, "thr.log")
                let thrEnv =
                    [ "FSION_PIPE", thrEndpoint
                      "FSIOND_PATH", Fsion.Tests.TestInfra.fsiondBinary
                      "FSIOND_LOG", thrLog
                      "FSION_CONFIG_DIR", Path.Combine(directory, ".fsion-thr-config") ]
                let thrAEntered = Path.Combine(directory, "thr-a-entered.txt")
                let thrARelease = Path.Combine(directory, "thr-a-release.txt")
                let thrAPath = Path.Combine(directory, "thr-a.fsx")
                let thrBPath = Path.Combine(directory, "thr-b.fsx")
                writeFile directory "thr-a.fsx" "let thrVal = \"A\"\n"
                writeFile directory "thr-b.fsx" "let thrVal = \"B\"\n"
                try
                    let thrStart = runProcess Fsion.Tests.TestInfra.fsionBinary [ "--pool-size"; "1"; "warmup" ] daemonDir thrEnv None 120000
                    Assert.True(thrStart.ExitCode = 0, thrStart.Stderr)
                    let thrWarmAInitial = runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; thrAPath; "warmup" ] daemonDir thrEnv None 120000
                    Assert.True(thrWarmAInitial.ExitCode = 0, thrWarmAInitial.Stderr)
                    writeFile
                        directory
                        "thr-a.fsx"
                        (sprintf
                            "System.IO.File.AppendAllText(@\"%s.count\", \"x\")\nif not (System.IO.File.Exists(@\"%s\")) then\n    System.IO.File.WriteAllText(@\"%s\", \"1\")\n    while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20\nlet thrVal = \"A2\"\n"
                            thrAEntered
                            thrAEntered
                            thrAEntered
                            thrARelease)
                    let thrReplenishA =
                        System.Threading.Tasks.Task.Run(fun () ->
                            runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; thrAPath; "warmup" ] daemonDir thrEnv None 120000)
                    waitFor "thr-a replenisher entered" (fun () -> File.Exists thrAEntered)
                    let thrRunBuiltin =
                        runProcess Fsion.Tests.TestInfra.fsionBinary [ "warmup" ] daemonDir thrEnv None 120000
                    Assert.True(thrRunBuiltin.ExitCode = 0, thrRunBuiltin.Stderr)
                    let thrRunBare =
                        System.Threading.Tasks.Task.Run(fun () ->
                            runProcess Fsion.Tests.TestInfra.fsionBinary [ "--no-prelude"; "warmup" ] daemonDir thrEnv None 120000)
                    System.Threading.Thread.Sleep 150
                    let thrClaimA =
                        System.Threading.Tasks.Task.Run(fun () ->
                            runProcess
                                Fsion.Tests.TestInfra.fsionBinary
                                [ "--prelude"; thrAPath; "-e"; sprintf "let n = System.IO.File.ReadAllText(@\"%s.count\").Length in System.Console.Write(thrVal + \"|\" + string n)" thrAEntered ]
                                daemonDir
                                thrEnv
                                None
                                120000)
                    waitFor "claimant for thr-a waiting" (fun () ->
                        File.Exists thrLog && File.ReadAllText(thrLog).Contains("session-claim waiting"))
                    writeFile directory "thr-a-release.txt" "go"
                    let claimADone = thrClaimA.GetAwaiter().GetResult()
                    Assert.True(claimADone.ExitCode = 0, claimADone.Stderr)
                    Assert.Equal("A2|1", claimADone.Stdout)
                    let _ = thrReplenishA.GetAwaiter().GetResult()
                    let bareDone = thrRunBare.GetAwaiter().GetResult()
                    Assert.True(bareDone.ExitCode = 0, bareDone.Stderr)

                    let seqAEntered = Path.Combine(directory, "seq-a-entered.txt")
                    let seqARelease = Path.Combine(directory, "seq-a-release.txt")
                    let seqASub = Path.Combine(directory, "seq-a-sub")
                    Directory.CreateDirectory seqASub |> ignore
                    let seqAPath = Path.Combine(directory, "seq-a.fsx")
                    let seqBPath = Path.Combine(directory, "seq-b.fsx")
                    writeFile
                        directory
                        "seq-a.fsx"
                        (sprintf
                            "System.IO.Directory.SetCurrentDirectory(@\"%s\")\nSystem.Environment.SetEnvironmentVariable(\"FSION_SEQ_PRELUDE\", \"dirty\")\nSystem.IO.File.WriteAllText(@\"%s\", \"1\")\nwhile not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20\n"
                            seqASub
                            seqAEntered
                            seqARelease)
                    writeFile
                        directory
                        "seq-b.fsx"
                        "let seqClean = System.IO.Directory.GetCurrentDirectory() + \"|\" + (match System.Environment.GetEnvironmentVariable(\"FSION_SEQ_PRELUDE\") with null -> \"unset\" | v -> v)\n"
                    let seqTaskA =
                        System.Threading.Tasks.Task.Run(fun () ->
                            runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; seqAPath; "-e"; "()" ] daemonDir poolZeroEnv None 120000)
                    waitFor "seq-a entered" (fun () -> File.Exists seqAEntered)
                    let seqTaskB =
                        System.Threading.Tasks.Task.Run(fun () ->
                            runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; seqBPath; "-e"; "System.Console.Write(seqClean)" ] daemonDir poolZeroEnv None 120000)
                    System.Threading.Thread.Sleep 150
                    writeFile directory "seq-a-release.txt" "go"
                    let seqResA = seqTaskA.GetAwaiter().GetResult()
                    let seqResB = seqTaskB.GetAwaiter().GetResult()
                    Assert.True(seqResA.ExitCode = 0, seqResA.Stderr)
                    Assert.True(seqResB.ExitCode = 0, seqResB.Stderr)
                    Assert.Equal(daemonDir + "|unset", seqResB.Stdout)

                    let p0DispEntered = Path.Combine(directory, "p0-disp-entered.txt")
                    let p0DispRelease = Path.Combine(directory, "p0-disp-release.txt")
                    let p0DispPath = Path.Combine(directory, "p0-disp.fsx")
                    writeFile
                        directory
                        "p0-disp.fsx"
                        (sprintf
                            "let hold = { new System.IDisposable with member _.Dispose() = System.IO.File.WriteAllText(@\"%s\", \"1\"); while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20 }\n"
                            p0DispEntered
                            p0DispRelease)
                    let p0Named =
                        runProcess Fsion.Tests.TestInfra.fsionBinary [ "--session"; "p0-sess"; "-e"; "()" ] daemonDir poolZeroEnv None 60000
                    Assert.True(p0Named.ExitCode = 0, p0Named.Stderr)
                    let p0Warm =
                        System.Threading.Tasks.Task.Run(fun () ->
                            runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; p0DispPath; "warmup" ] daemonDir poolZeroEnv None 120000)
                    waitFor "pool=0 warmup Dispose entered" (fun () -> File.Exists p0DispEntered)
                    let p0Unblock =
                        runProcess
                            Fsion.Tests.TestInfra.fsionBinary
                            [ "--session"; "p0-sess"; "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"go\"); System.Console.Write(\"p0-ok\")" p0DispRelease ]
                            daemonDir
                            poolZeroEnv
                            None
                            60000
                    Assert.True(p0Unblock.ExitCode = 0, p0Unblock.Stderr)
                    Assert.Equal("p0-ok", p0Unblock.Stdout)
                    let p0WarmRes = p0Warm.GetAwaiter().GetResult()
                    Assert.True(p0WarmRes.ExitCode = 0, p0WarmRes.Stderr)

                    let p0FailEntered = Path.Combine(directory, "p0-fail-entered.txt")
                    let p0FailRelease = Path.Combine(directory, "p0-fail-release.txt")
                    let p0FailPath = Path.Combine(directory, "p0-fail.fsx")
                    writeFile
                        directory
                        "p0-fail.fsx"
                        (sprintf
                            "type P0FailFin() =\n    override _.Finalize() =\n        System.IO.File.WriteAllText(@\"%s\", \"1\")\n        while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20\nlet mutable p0Holder : obj = box (P0FailFin())\np0Holder <- null\nSystem.GC.Collect()\nfailwith \"p0-fail-boom\"\n"
                            p0FailEntered
                            p0FailRelease)
                    let p0FailWarm =
                        System.Threading.Tasks.Task.Run(fun () ->
                            runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; p0FailPath; "warmup" ] daemonDir poolZeroEnv None 120000)
                    waitFor "failed prelude finalizer entered" (fun () -> File.Exists p0FailEntered)
                    let p0FailUnblock =
                        runProcess
                            Fsion.Tests.TestInfra.fsionBinary
                            [ "--session"; "p0-sess"; "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"go\"); System.Console.Write(\"p0-fail-unblocked\")" p0FailRelease ]
                            daemonDir
                            poolZeroEnv
                            None
                            60000
                    Assert.True(p0FailUnblock.ExitCode = 0, p0FailUnblock.Stderr)
                    Assert.Equal("p0-fail-unblocked", p0FailUnblock.Stdout)
                    let p0FailWarmRes = p0FailWarm.GetAwaiter().GetResult()
                    Assert.True(p0FailWarmRes.ExitCode <> 0)

                    let nrPipe = "fsion-test-nr-" + Guid.NewGuid().ToString("N")
                    let nrEnv =
                        [ "FSION_PIPE", nrPipe
                          "FSIOND_PATH", Fsion.Tests.TestInfra.fsiondBinary
                          "FSION_CONFIG_DIR", Path.Combine(directory, ".fsion-nr-config")
                          "FSION_PRELUDE", null ]
                    try
                        let nrPathA = Path.Combine(directory, "nr-a.fsx")
                        writeFile directory "nr-a.fsx" "let nrVal = \"A1\"\n"
                        let nrStart = runProcess Fsion.Tests.TestInfra.fsionBinary [ "--pool-size"; "1"; "--prelude"; nrPathA; "-e"; "()" ] daemonDir nrEnv None 120000
                        Assert.True(nrStart.ExitCode = 0, nrStart.Stderr)
                        let nrSessA = runProcess Fsion.Tests.TestInfra.fsionBinary [ "--session"; "nr"; "--prelude"; nrPathA; "-e"; "System.Console.Write(nrVal)" ] daemonDir nrEnv None 60000
                        Assert.True(nrSessA.ExitCode = 0, nrSessA.Stderr)
                        Assert.Equal("A1", nrSessA.Stdout)
                        let nrPathB = Path.Combine(directory, "nr-b.fsx")
                        writeFile directory "nr-b.fsx" "let nrOther = \"B\"\n"
                        let nrWarmB = runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; nrPathB; "warmup" ] daemonDir nrEnv None 120000
                        Assert.True(nrWarmB.ExitCode = 0, nrWarmB.Stderr)
                        let nrRewarmA = runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; nrPathA; "warmup" ] daemonDir nrEnv None 120000
                        Assert.True(nrRewarmA.ExitCode = 0, nrRewarmA.Stderr)
                        let nrHold = Path.Combine(directory, "nr-hold.txt")
                        let nrHoldRelease = Path.Combine(directory, "nr-hold-release.txt")
                        let nrTurnHold =
                            System.Threading.Tasks.Task.Run(fun () ->
                                runProcess
                                    Fsion.Tests.TestInfra.fsionBinary
                                    [ "--session"; "nr"; "--prelude"; nrPathA; "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"1\"); while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20" nrHold nrHoldRelease ]
                                    daemonDir
                                    nrEnv
                                    None
                                    120000)
                        waitFor "nr session holding gate" (fun () -> File.Exists nrHold)
                        let nrResetQueued =
                            System.Threading.Tasks.Task.Run(fun () ->
                                runProcess
                                    Fsion.Tests.TestInfra.fsionBinary
                                    [ "--session"; "nr"; "--reset"; "--prelude"; nrPathA; "-e"; "System.Console.Write(nrVal)" ]
                                    daemonDir
                                    nrEnv
                                    None
                                    120000)
                        System.Threading.Thread.Sleep 150
                        let nrEvict1 = runProcess Fsion.Tests.TestInfra.fsionBinary [ "--no-prelude"; "-e"; "()" ] daemonDir nrEnv None 60000
                        Assert.True(nrEvict1.ExitCode = 0, nrEvict1.Stderr)
                        let nrEvict2 =
                            System.Threading.Tasks.Task.Run(fun () ->
                                runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; nrPathB; "-e"; "()" ] daemonDir nrEnv None 120000)
                        System.Threading.Thread.Sleep 150
                        writeFile directory "nr-a.fsx" "let nrVal = \"A2\"\n"
                        writeFile directory "nr-hold-release.txt" "go"
                        let nrHoldRes = nrTurnHold.GetAwaiter().GetResult()
                        Assert.True(nrHoldRes.ExitCode = 0, nrHoldRes.Stderr)
                        let nrEvict2Res = nrEvict2.GetAwaiter().GetResult()
                        Assert.True(nrEvict2Res.ExitCode = 0, nrEvict2Res.Stderr)
                        let nrResetRes = nrResetQueued.GetAwaiter().GetResult()
                        Assert.True(nrResetRes.ExitCode = 0, nrResetRes.Stderr)
                        Assert.Equal("A2", nrResetRes.Stdout)
                    finally
                        runProcess Fsion.Tests.TestInfra.fsionBinary [ "daemon"; "stop" ] daemonDir nrEnv None 20000 |> ignore

                    let bgFailPipe = "fsion-test-bgf-" + Guid.NewGuid().ToString("N")
                    let bgFailEnv =
                        [ "FSION_PIPE", bgFailPipe
                          "FSIOND_PATH", Fsion.Tests.TestInfra.fsiondBinary
                          "FSION_CONFIG_DIR", Path.Combine(directory, ".fsion-bgf-config")
                          "FSION_PRELUDE", null ]
                    try
                        let bgFailPath = Path.Combine(directory, "bg-fail.fsx")
                        let bgArmMarker = Path.Combine(directory, "bg-arm.txt")
                        let bgSecondRunning = Path.Combine(directory, "bg-second-running.txt")
                        let bgEvalRelease = Path.Combine(directory, "bg-eval-release.txt")
                        let bgDispEntered = Path.Combine(directory, "bg-disp-entered.txt")
                        let bgDispRelease = Path.Combine(directory, "bg-disp-release.txt")
                        writeFile
                            directory
                            "bg-fail.fsx"
                            (sprintf
                                "let armed = System.IO.File.Exists(@\"%s\")\nlet hold = { new System.IDisposable with member _.Dispose() = if armed then System.IO.File.WriteAllText(@\"%s\", \"1\"); while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20 }\nif armed then\n    System.IO.File.WriteAllText(@\"%s\", \"1\")\n    while not (System.IO.File.Exists(@\"%s\")) do System.Threading.Thread.Sleep 20\n    failwith \"bg-fail-boom\"\n"
                                bgArmMarker
                                bgDispEntered
                                bgDispRelease
                                bgSecondRunning
                                bgEvalRelease)
                        let bgWarm = runProcess Fsion.Tests.TestInfra.fsionBinary [ "--pool-size"; "2"; "--prelude"; bgFailPath; "warmup" ] daemonDir bgFailEnv None 120000
                        Assert.True(bgWarm.ExitCode = 0, bgWarm.Stderr)
                        writeFile directory "bg-arm.txt" "arm"
                        let bgConsumeOne = runProcess Fsion.Tests.TestInfra.fsionBinary [ "--prelude"; bgFailPath; "-e"; "()" ] daemonDir bgFailEnv None 60000
                        Assert.True(bgConsumeOne.ExitCode = 0, bgConsumeOne.Stderr)
                        waitFor "replenisher entered armed bg-fail evaluation" (fun () -> File.Exists bgSecondRunning)
                        writeFile directory "bg-eval-release.txt" "boom"
                        waitFor "replenisher failed prelude Dispose entered" (fun () -> File.Exists bgDispEntered)
                        let bgColdUnblock =
                            runProcess
                                Fsion.Tests.TestInfra.fsionBinary
                                [ "--no-prelude"; "-e"; sprintf "System.IO.File.WriteAllText(@\"%s\", \"go\"); System.Console.Write(\"bg-cold-ok\")" bgDispRelease ]
                                daemonDir
                                bgFailEnv
                                None
                                60000
                        Assert.True(bgColdUnblock.ExitCode = 0, bgColdUnblock.Stderr)
                        Assert.Equal("bg-cold-ok", bgColdUnblock.Stdout)
                    finally
                        runProcess Fsion.Tests.TestInfra.fsionBinary [ "daemon"; "stop" ] daemonDir bgFailEnv None 20000 |> ignore
                finally
                    runProcess Fsion.Tests.TestInfra.fsionBinary [ "daemon"; "stop" ] daemonDir thrEnv None 20000 |> ignore
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
