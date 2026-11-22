module Fsion.Tests.HygieneTests

open System
open System.IO
open Xunit
open Fsion.Tests.TestInfra

[<Fact>]
let ``warm runs write no temporary dll files into the daemon temp directory`` () =
    withContext "hygiene-tmp" (fun context directory ->
        let watchDirectory = createTempDir "hygiene-watch"

        try
            let extraEnvironment =
                [ "TMP", watchDirectory
                  "TEMP", watchDirectory ]

            let warmup = context.Run([ "warmup" ], timeoutMs = 120000, extraEnv = extraEnvironment)
            Assert.Equal(0, warmup.ExitCode)

            for round in 1 .. 3 do
                let name = "run" + string round + ".fsx"
                writeFile directory name ("printfn \"round-" + string round + "\"\n")
                let result = context.Run([ name ], timeoutMs = 60000, extraEnv = extraEnvironment)
                Assert.Equal(0, result.ExitCode)
                Assert.Equal("round-" + string round + Environment.NewLine, result.Stdout)

            let dllFiles = Directory.GetFiles(watchDirectory, "*.dll", SearchOption.AllDirectories)

            Assert.True(
                dllFiles.Length = 0,
                "unexpected temporary dll files: " + String.Join(", ", dllFiles)
            )
        finally
            tryDeleteDirectory watchDirectory)

[<Fact>]
let ``stdout and stderr are captured on their own streams without cross-talk`` () =
    withContext "hygiene-streams" (fun context directory ->
        writeFile
            directory
            "streams.fsx"
            ("for i in 1 .. 20 do\n"
             + "    printfn \"out-%d\" i\n"
             + "    eprintfn \"err-%d\" i\n")

        let expected = runFsi directory [ "streams.fsx" ] None
        let actual = context.Run [ "streams.fsx" ]
        Assert.Equal(expected.ExitCode, actual.ExitCode)
        Assert.Equal(expected.Stdout, actual.Stdout)
        Assert.Equal(expected.Stderr, actual.Stderr)
        Assert.DoesNotContain("err-", actual.Stdout)
        Assert.DoesNotContain("out-", actual.Stderr))
