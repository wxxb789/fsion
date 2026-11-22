module Fsion.Tests.LatencyTests

open System
open Xunit
open Xunit.Abstractions
open Fsion.Tests.TestInfra

let private median (values: float seq) =
    let sorted = values |> Seq.sort |> Seq.toArray
    sorted.[sorted.Length / 2]

let private fsharpStringLiteral (value: string) =
    "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

let private generateTypicalScript (round: int) (seed: int) =
    let rawJson = "[{\"k\":\"v" + string seed + "\"}]"

    String.concat
        "\n"
        [ "open System"
          "open System.Text.Json"
          "let items = [| (\"alpha-"
          + string round
          + "\", "
          + string seed
          + "); (\"beta-"
          + string round
          + "\", "
          + string (seed + 1)
          + "); (\"gamma-"
          + string round
          + "\", "
          + string (seed + 2)
          + ") |]"
          "let raw = " + fsharpStringLiteral rawJson
          "let doc = JsonDocument.Parse(raw)"
          "let value = doc.RootElement[0].GetProperty(\"k\").GetString()"
          "let prefix = defaultArg (Option.ofObj (Environment.GetEnvironmentVariable \"FSION_BENCH_PREFIX\")) \"none\""
          "let filtered = items |> Array.filter (fun (_, n) -> n > 0) |> Array.sortBy snd"
          "filtered |> Array.iter (fun (name, n) -> printfn \"%s|%s|%s|%d\" prefix value name n)"
          "" ]

type LatencyTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.``warm fsion median latency is far below dotnet fsi on fresh scripts`` () =
        withContext "latency" (fun context directory ->
            let warmup = context.Run([ "warmup" ], timeoutMs = 120000)
            Assert.Equal(0, warmup.ExitCode)

            let prime = context.Run([ "-e"; "printfn \"prime\"" ], timeoutMs = 60000)
            Assert.Equal(0, prime.ExitCode)

            let fsionTimes = ResizeArray<float>()

            for round in 1 .. 8 do
                let name = "fsion-" + string round + ".fsx"
                writeFile directory name (generateTypicalScript round (round * 7919))
                let result = context.Run([ name ], timeoutMs = 60000)
                Assert.Equal(0, result.ExitCode)
                fsionTimes.Add result.DurationMs

            let fsiTimes = ResizeArray<float>()

            for round in 1 .. 3 do
                let name = "fsi-" + string round + ".fsx"
                writeFile directory name (generateTypicalScript (100 + round) (round * 104729))
                let result = runFsi directory [ name ] None
                Assert.Equal(0, result.ExitCode)
                fsiTimes.Add result.DurationMs

            writeFile directory "verify.fsx" (generateTypicalScript 500 424242)
            let expected = runFsi directory [ "verify.fsx" ] None
            let actual = context.Run([ "verify.fsx" ], timeoutMs = 60000)
            Assert.Equal(expected.ExitCode, actual.ExitCode)
            Assert.Equal(expected.Stdout, actual.Stdout)

            let fsionMedian = median fsionTimes
            let fsiMedian = median fsiTimes
            let speedup = fsiMedian / fsionMedian

            output.WriteLine("fsion median (ms): " + string fsionMedian)
            output.WriteLine("dotnet fsi median (ms): " + string fsiMedian)
            output.WriteLine("median speedup: " + string speedup)

            Assert.True(
                fsionMedian * 5.0 < fsiMedian,
                "warm fsion median "
                + string fsionMedian
                + " ms is not at least 5x below dotnet fsi median "
                + string fsiMedian
                + " ms"
            ))
