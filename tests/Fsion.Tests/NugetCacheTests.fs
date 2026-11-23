module Fsion.Tests.NugetCacheTests

open System
open System.IO
open Xunit
open Xunit.Abstractions
open Fsion.Tests.TestInfra

type NugetCacheTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.``cached nuget directives skip official resolution on a later daemon generation`` () =
        withContext "nuget-cache" (fun context directory ->
            let cacheDirectory = createTempDir "nuget-cache-store"
            let packageId = uniquePackageId "fsioncache"
            let firstPackages = Path.Combine(directory, "packages-first")
            let secondPackages = Path.Combine(directory, "packages-second")

            try
                let feed = createTinyPackage directory packageId
                writeNugetConfig directory feed

                writeFile
                    directory
                    "first.fsx"
                    ("#r \"nuget: " + packageId + ", 1.0.0\"\n"
                     + "printfn \"first=%s\" (FsionTinyPkgNs.Greeter.Hello())\n")

                writeFile
                    directory
                    "second.fsx"
                    ("#r \"nuget:  " + packageId + ",1.0.0\"\n"
                     + "printfn \"second=%s\" (FsionTinyPkgNs.Greeter.Hello())\n")

                let firstEnvironment = [ "FSION_CACHE_DIR", cacheDirectory; "NUGET_PACKAGES", firstPackages ]

                let warmup = context.Run([ "warmup" ], timeoutMs = 180000, extraEnv = firstEnvironment)
                Assert.Equal(0, warmup.ExitCode)

                let first = context.Run([ "first.fsx" ], timeoutMs = 240000, extraEnv = firstEnvironment)
                Assert.Equal(0, first.ExitCode)
                Assert.Equal("first=tiny-package-ok", first.Stdout.Trim())

                let cacheFiles = Directory.GetFiles(cacheDirectory, "*.json")
                Assert.True(cacheFiles.Length > 0, "the resolution cache did not persist an entry")
                Assert.Contains("references", (File.ReadAllText cacheFiles.[0]))

                let stopped =
                    context.Run([ "daemon"; "stop" ], timeoutMs = 60000, extraEnv = firstEnvironment)

                Assert.Equal(0, stopped.ExitCode)

                purgeDependencyManagerCache packageId
                Directory.Move(feed, feed + "-away")

                let secondEnvironment = [ "FSION_CACHE_DIR", cacheDirectory; "NUGET_PACKAGES", secondPackages ]

                let warmupSecond = context.Run([ "warmup" ], timeoutMs = 180000, extraEnv = secondEnvironment)
                Assert.Equal(0, warmupSecond.ExitCode)

                let second = context.Run([ "second.fsx" ], timeoutMs = 240000, extraEnv = secondEnvironment)
                Assert.Equal(0, second.ExitCode)
                Assert.Equal("second=tiny-package-ok", second.Stdout.Trim())
                Assert.False(
                    Directory.Exists secondPackages,
                    "the cached run restored packages instead of feeding cached references"
                )

                output.WriteLine(
                    "first run with official resolution: "
                    + string (int first.DurationMs)
                    + " ms; cached run on a later daemon generation: "
                    + string (int second.DurationMs)
                    + " ms"
                )

                Assert.True(
                    second.DurationMs < first.DurationMs,
                    "cached run "
                    + string (int second.DurationMs)
                    + " ms was not faster than the resolving run "
                    + string (int first.DurationMs)
                    + " ms"
                )
            finally
                purgeDependencyManagerCache packageId
                tryDeleteDirectory cacheDirectory)

    [<Fact>]
    member _.``a nuget directive inside a comment or string is neither rewritten nor cached`` () =
        withContext "nuget-extraction" (fun context directory ->
            let cacheDirectory = createTempDir "nuget-extraction-cache"

            try
                let script =
                    String.concat
                        "\n"
                        [ "(*"
                          "#r \"nuget: Not.A.Package, 1.0.0\""
                          "*)"
                          "let text = \"\"\""
                          "#r \"nuget: Also.Not.A.Package, 2.0.0\""
                          "\"\"\""
                          "printfn \"ok=%b\" (text.Contains \"nuget:\")" ]

                writeFile directory "commented.fsx" script

                let result =
                    context.Run([ "commented.fsx" ], timeoutMs = 120000, extraEnv = [ "FSION_CACHE_DIR", cacheDirectory ])

                Assert.Equal(0, result.ExitCode)
                Assert.Equal("ok=true" + Environment.NewLine, result.Stdout)
                Assert.Equal(0, Directory.GetFiles(cacheDirectory, "*.json").Length)
            finally
                tryDeleteDirectory cacheDirectory)
