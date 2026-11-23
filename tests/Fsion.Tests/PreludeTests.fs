module Fsion.Tests.PreludeTests

open System
open System.IO
open Xunit
open Xunit.Abstractions
open Fsion.Tests.TestInfra

let private metricsOrFail (context: FsionContext) =
    context.Metrics 30000
    |> Option.defaultWith (fun () -> failwith "fsiond did not answer GetMetrics")

type PreludeTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.``built-in Fsion helpers are preloaded on warm sessions and --no-prelude hides them`` () =
        withContext "prelude-builtin" (fun context directory ->
            let warmup = context.Run([ "warmup" ], timeoutMs = 180000)
            Assert.Equal(0, warmup.ExitCode)

            let before = metricsOrFail context
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

            let after = metricsOrFail context
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

                writeFile
                    directory
                    "prelude.fsx"
                    ("#r \"nuget: " + packageId + ", 1.0.0\"\n" + "let preludeGreeting () = \"first\"\n")

                let warmup = context.Run([ "warmup" ], timeoutMs = 240000, extraEnv = environment)

                Assert.Equal(0, warmup.ExitCode)

                let before = metricsOrFail context
                Assert.True(before.PoolWarm > 0, "warm pool was empty after warmup")

                let code = "printfn \"%s|%s\" (preludeGreeting()) (FsionTinyPkgNs.Greeter.Hello())"

                let first = context.Run([ "-e"; code ], timeoutMs = 120000, extraEnv = environment)
                Assert.Equal(0, first.ExitCode)
                Assert.Equal("first|tiny-package-ok", first.Stdout.Trim())

                let afterFirst = metricsOrFail context
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

            let missing =
                context.Run([ "--prelude"; Path.Combine(directory, "no-such-prelude.fsx"); "-e"; "1" ], timeoutMs = 60000)

            Assert.Equal(1, missing.ExitCode)
            Assert.Contains("prelude file not found", missing.Stderr)

            let conflicting =
                context.Run([ "--prelude"; flagPrelude; "--no-prelude"; "-e"; "1" ], timeoutMs = 60000)

            Assert.Equal(1, conflicting.ExitCode)
            Assert.Contains("cannot be combined", conflicting.Stderr))
