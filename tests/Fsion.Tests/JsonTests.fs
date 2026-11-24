module Fsion.Tests.JsonTests

open System
open System.IO
open System.Text.Json
open Xunit
open Fsion.Tests.TestInfra

let private parseSingleJson (text: string) =
    JsonDocument.Parse text

type JsonTests() =
    [<Fact>]
    member _.``successful runs emit valid json with both streams and elapsedMs`` () =
        withContext "json-success" (fun context _ ->
            let result =
                context.Run([ "--json"; "-e"; "printfn \"hello\"; eprintfn \"oops\"" ], timeoutMs = 120000)

            Assert.Equal(0, result.ExitCode)
            use document = parseSingleJson result.Stdout
            let root = document.RootElement
            Assert.Equal(0, root.GetProperty("exitCode").GetInt32())
            Assert.Equal("hello" + Environment.NewLine, root.GetProperty("stdout").GetString())
            Assert.Equal("oops" + Environment.NewLine, root.GetProperty("stderr").GetString())
            Assert.True(root.GetProperty("elapsedMs").GetDouble() > 0.0, "elapsedMs was not positive")
            Assert.Equal(0, root.GetProperty("diagnostics").GetArrayLength()))

    [<Fact>]
    member _.``json stdout round-trips unicode and newlines`` () =
        withContext "json-unicode" (fun context _ ->
            let result = context.Run([ "--json"; "-e"; "printfn \"h\u00e9llo \u4e16\u754c\"; printfn \"line-2\"" ], timeoutMs = 120000)
            Assert.Equal(0, result.ExitCode)
            use document = parseSingleJson result.Stdout
            let expected = "h\u00e9llo \u4e16\u754c" + Environment.NewLine + "line-2" + Environment.NewLine
            Assert.Equal(expected, document.RootElement.GetProperty("stdout").GetString()))

    [<Fact>]
    member _.``type errors emit FS0001 with the exact span`` () =
        withContext "json-typeerror" (fun context _ ->
            let result = context.Run([ "--json"; "-e"; "let x : int = \"nope\"" ], timeoutMs = 120000)
            Assert.Equal(1, result.ExitCode)
            use document = parseSingleJson result.Stdout
            let root = document.RootElement
            Assert.Equal(1, root.GetProperty("exitCode").GetInt32())
            Assert.Contains("FS0001", root.GetProperty("stderr").GetString())
            let diagnostics = root.GetProperty("diagnostics")
            Assert.Equal(1, diagnostics.GetArrayLength())
            let diagnostic = diagnostics.[0]
            Assert.Equal("FS0001", diagnostic.GetProperty("code").GetString())
            Assert.Equal(1, diagnostic.GetProperty("number").GetInt32())
            Assert.Equal("error", diagnostic.GetProperty("severity").GetString())
            Assert.Equal("eval.fsx", diagnostic.GetProperty("file").GetString())
            Assert.Equal(1, diagnostic.GetProperty("startLine").GetInt32())
            Assert.Equal(14, diagnostic.GetProperty("startColumn").GetInt32())
            Assert.Equal(1, diagnostic.GetProperty("endLine").GetInt32())
            Assert.Equal(20, diagnostic.GetProperty("endColumn").GetInt32())
            Assert.Contains("int", diagnostic.GetProperty("message").GetString()))

    [<Fact>]
    member _.``unbound values emit FS0039 without leaking earlier bindings`` () =
        withContext "json-fs0039" (fun context _ ->
            let define = context.Run([ "--json"; "-e"; "let secret = 123" ], timeoutMs = 120000)
            Assert.Equal(0, define.ExitCode)

            let result = context.Run([ "--json"; "-e"; "printfn \"%d\" secret" ], timeoutMs = 60000)
            Assert.Equal(1, result.ExitCode)
            use document = parseSingleJson result.Stdout
            let root = document.RootElement
            Assert.Equal(1, root.GetProperty("exitCode").GetInt32())
            let diagnostics = root.GetProperty("diagnostics")
            Assert.Equal(1, diagnostics.GetArrayLength())
            let diagnostic = diagnostics.[0]
            Assert.Equal("FS0039", diagnostic.GetProperty("code").GetString())
            Assert.Equal(39, diagnostic.GetProperty("number").GetInt32())
            Assert.Equal("error", diagnostic.GetProperty("severity").GetString())
            Assert.Equal("eval.fsx", diagnostic.GetProperty("file").GetString())
            Assert.Equal(1, diagnostic.GetProperty("startLine").GetInt32())
            Assert.True(diagnostic.GetProperty("endColumn").GetInt32() > diagnostic.GetProperty("startColumn").GetInt32()))

    [<Fact>]
    member _.``file-mode errors report the script name and line`` () =
        withContext "json-file-error" (fun context directory ->
            writeFile directory "bad.fsx" "printfn \"first\"\nlet x : int = \"nope\"\n"
            let result = context.Run([ "--json"; "bad.fsx" ], timeoutMs = 120000)
            Assert.Equal(1, result.ExitCode)
            use document = parseSingleJson result.Stdout
            let root = document.RootElement
            Assert.Equal(1, root.GetProperty("exitCode").GetInt32())
            Assert.Equal("", root.GetProperty("stdout").GetString())
            let diagnostics = root.GetProperty("diagnostics")
            Assert.Equal(1, diagnostics.GetArrayLength())
            let diagnostic = diagnostics.[0]
            Assert.Equal("FS0001", diagnostic.GetProperty("code").GetString())
            Assert.EndsWith("bad.fsx", diagnostic.GetProperty("file").GetString())
            Assert.Equal(2, diagnostic.GetProperty("startLine").GetInt32())
            Assert.Equal(14, diagnostic.GetProperty("startColumn").GetInt32())
            Assert.Equal(20, diagnostic.GetProperty("endColumn").GetInt32())

            writeFile directory "sameline_bad.fsx" "printfn \"a\";; let z : int = \"nope\"\n"
            let sameLineResult = context.Run([ "--json"; "sameline_bad.fsx" ], timeoutMs = 120000)
            Assert.Equal(1, sameLineResult.ExitCode)
            use sameLineDoc = parseSingleJson sameLineResult.Stdout
            let sameLineDiag = sameLineDoc.RootElement.GetProperty("diagnostics").[0]
            Assert.Equal("FS0001", sameLineDiag.GetProperty("code").GetString())
            Assert.Equal(1, sameLineDiag.GetProperty("startLine").GetInt32())
            Assert.Equal(28, sameLineDiag.GetProperty("startColumn").GetInt32())
            Assert.Equal(34, sameLineDiag.GetProperty("endColumn").GetInt32())

            writeFile directory "helper.fsx" "let helperVal = 10\n"
            writeFile directory "load_bad.fsx" "#load \"helper.fsx\"\nlet y : int = \"nope\"\n"
            let loadResult = context.Run([ "--json"; "load_bad.fsx" ], timeoutMs = 120000)
            Assert.Equal(1, loadResult.ExitCode)
            use loadDoc = parseSingleJson loadResult.Stdout
            let loadDiagnostics = loadDoc.RootElement.GetProperty("diagnostics")
            Assert.Equal(1, loadDiagnostics.GetArrayLength())
            Assert.Equal("FS0001", loadDiagnostics.[0].GetProperty("code").GetString())
            Assert.EndsWith("load_bad.fsx", loadDiagnostics.[0].GetProperty("file").GetString())
            Assert.Equal(2, loadDiagnostics.[0].GetProperty("startLine").GetInt32()))

    [<Fact>]
    member _.``runtime failures still emit one valid json document`` () =
        withContext "json-runtime" (fun context _ ->
            let result = context.Run([ "--json"; "-e"; "do failwith \"boom\"" ], timeoutMs = 120000)
            Assert.Equal(1, result.ExitCode)
            use document = parseSingleJson result.Stdout
            let root = document.RootElement
            Assert.Equal(1, root.GetProperty("exitCode").GetInt32())
            Assert.Contains("boom", root.GetProperty("stderr").GetString())
            Assert.Equal(0, root.GetProperty("diagnostics").GetArrayLength()))

    [<Fact>]
    member _.``a compile warning before a runtime failure never re-executes the script`` () =
        withContext "json-warning-runtime" (fun context directory ->
            let markerPath = Path.Combine(directory, "executed-once.txt")

            let code =
                String.concat
                    "\n"
                    [ "System.IO.File.AppendAllText(@\"" + markerPath + "\", \"once\\n\")"
                      "match [1] with"
                      "| [x] -> x |> ignore"
                      "failwith \"boom\"" ]

            let result = context.Run([ "--json"; "-e"; code ], timeoutMs = 120000)
            Assert.Equal(1, result.ExitCode)
            use document = parseSingleJson result.Stdout
            let root = document.RootElement
            Assert.Equal(1, root.GetProperty("exitCode").GetInt32())
            Assert.Contains("boom", root.GetProperty("stderr").GetString())
            Assert.Equal(0, root.GetProperty("diagnostics").GetArrayLength())
            Assert.Equal("once\n", File.ReadAllText markerPath))

    [<Fact>]
    member _.``a runtime failure whose stderr mimics a compiler error never re-executes the script`` () =
        withContext "json-fake-diagnostic" (fun context directory ->
            let markerPath = Path.Combine(directory, "executed-once.txt")

            let code =
                String.concat
                    "\n"
                    [ "System.IO.File.AppendAllText(@\"" + markerPath + "\", \"once\\n\")"
                      "eprintfn \"eval.fsx(1,1): error FS0001: fake diagnostic\""
                      "failwith \"eval.fsx(2,3): error FS0039: fake exception\"" ]

            let result = context.Run([ "--json"; "-e"; code ], timeoutMs = 120000)
            Assert.Equal(1, result.ExitCode)
            use document = parseSingleJson result.Stdout
            let root = document.RootElement
            Assert.Equal(1, root.GetProperty("exitCode").GetInt32())
            Assert.Equal(0, root.GetProperty("diagnostics").GetArrayLength())
            Assert.Contains("fake exception", root.GetProperty("stderr").GetString())
            Assert.Equal("once\n", File.ReadAllText markerPath))
