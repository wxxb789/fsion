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
