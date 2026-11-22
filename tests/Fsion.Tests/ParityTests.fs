module Fsion.Tests.ParityTests

open System
open Xunit
open Fsion.Tests.TestInfra

let private assertSameOutput (expected: ProcessResult) (actual: ProcessResult) =
    Assert.Equal(expected.ExitCode, actual.ExitCode)
    Assert.Equal(expected.Stdout, actual.Stdout)
    Assert.Equal(expected.Stderr, actual.Stderr)

let private contextScript =
    "printfn \"OUT1\"\n"
    + "eprintfn \"ERR1\"\n"
    + "printfn \"args=%A\" fsi.CommandLineArgs\n"
    + "printfn \"src=%s\" __SOURCE_DIRECTORY__\n"
    + "printfn \"cwd=%s\" (System.IO.Directory.GetCurrentDirectory())\n"

[<Fact>]
let ``file mode matches dotnet fsi byte for byte`` () =
    withContext "parity-file" (fun context directory ->
        writeFile directory "probe.fsx" contextScript
        let expected = runFsi directory [ "probe.fsx"; "one"; "two words" ] None
        let actual = context.Run [ "probe.fsx"; "one"; "two words" ]
        assertSameOutput expected actual)

[<Fact>]
let ``file mode in a subdirectory matches dotnet fsi and resolves nested loads`` () =
    withContext "parity-subdir" (fun context directory ->
        writeFile directory "sub/inner.fsx" "let innerValue = 41\nprintfn \"inner loaded\"\n"
        writeFile directory "sub/outer.fsx" ("#load \"inner.fsx\"\n" + contextScript)
        let expected = runFsi directory [ "sub/outer.fsx" ] None
        let actual = context.Run [ "sub/outer.fsx" ]
        assertSameOutput expected actual)

[<Fact>]
let ``eval mode matches the same code evaluated as a script file`` () =
    withContext "parity-eval" (fun context directory ->
        writeFile directory "eval.fsx" contextScript
        let expected = runFsi directory [ "eval.fsx" ] None
        let actual = context.Run [ "-e"; contextScript ]
        assertSameOutput expected actual)

[<Fact>]
let ``stdin mode matches the same code evaluated as a script file`` () =
    withContext "parity-stdin" (fun context directory ->
        writeFile directory "stdin.fsx" contextScript
        let expected = runFsi directory [ "stdin.fsx" ] None
        let actual = context.Run([], stdin = contextScript)
        assertSameOutput expected actual)

[<Fact>]
let ``syntax errors match dotnet fsi diagnostics and exit code`` () =
    withContext "parity-syntax" (fun context directory ->
        writeFile directory "syntaxerr.fsx" "let x =\nprintfn \"unreachable\"\n"
        let expected = runFsi directory [ "syntaxerr.fsx" ] None
        let actual = context.Run [ "syntaxerr.fsx" ]
        assertSameOutput expected actual)

[<Fact>]
let ``type errors match dotnet fsi diagnostics and exit code`` () =
    withContext "parity-type" (fun context directory ->
        writeFile directory "typeerr.fsx" "let x : int = \"nope\"\nprintfn \"after\"\n"
        let expected = runFsi directory [ "typeerr.fsx" ] None
        let actual = context.Run [ "typeerr.fsx" ]
        assertSameOutput expected actual)

[<Fact>]
let ``eval type errors match the script-file diagnostics`` () =
    withContext "parity-eval-type" (fun context directory ->
        let code = "let x : int = \"nope\"\n"
        writeFile directory "eval.fsx" code
        let expected = runFsi directory [ "eval.fsx" ] None
        let actual = context.Run [ "-e"; code ]
        assertSameOutput expected actual)

[<Fact>]
let ``exit inside a file script returns its code with flushed output`` () =
    withContext "parity-exit-file" (fun context directory ->
        writeFile directory "exit42.fsx" "printfn \"before\"\neprintfn \"errbefore\"\nexit 42\nprintfn \"after\"\n"
        let expected = runFsi directory [ "exit42.fsx" ] None
        let actual = context.Run [ "exit42.fsx" ]
        assertSameOutput expected actual)

[<Fact>]
let ``exit at the end of a file script returns its code`` () =
    withContext "parity-exit-last" (fun context directory ->
        writeFile directory "exitlast.fsx" "printfn \"before\"\nexit 9\n"
        let expected = runFsi directory [ "exitlast.fsx" ] None
        let actual = context.Run [ "exitlast.fsx" ]
        assertSameOutput expected actual)

[<Fact>]
let ``exit in eval mode returns its code`` () =
    withContext "parity-exit-eval" (fun context directory ->
        let code = "printfn \"pre\"\nexit 7\n"
        writeFile directory "eval.fsx" code
        let expected = runFsi directory [ "eval.fsx" ] None
        let actual = context.Run [ "-e"; code ]
        assertSameOutput expected actual)

[<Fact>]
let ``uncaught runtime exceptions match dotnet fsi after normalizing generated frames`` () =
    withContext "parity-runtime" (fun context directory ->
        writeFile directory "boom.fsx" "printfn \"before\"\ndo failwith \"boom\"\n"
        let expected = runFsi directory [ "boom.fsx" ] None
        let actual = context.Run [ "boom.fsx" ]
        Assert.Equal(expected.ExitCode, actual.ExitCode)
        Assert.Equal(expected.Stdout, actual.Stdout)
        Assert.Equal(normalizeGeneratedNames expected.Stderr, normalizeGeneratedNames actual.Stderr))

[<Fact>]
let ``large output passes through unchanged`` () =
    withContext "parity-large" (fun context directory ->
        writeFile
            directory
            "large.fsx"
            "for i in 1 .. 4000 do\n    printfn \"line-%d-%s\" i (String.replicate 40 \"x\")\n"

        let expected = runFsi directory [ "large.fsx" ] None
        let actual = context.Run [ "large.fsx" ]
        assertSameOutput expected actual)

[<Fact>]
let ``unicode output matches dotnet fsi`` () =
    withContext "parity-unicode" (fun context directory ->
        writeFile directory "unicode.fsx" "printfn \"h\u00e9llo \u4e16\u754c \u00fcn\u00efcode\"\n"
        let expected = runFsi directory [ "unicode.fsx" ] None
        let actual = context.Run [ "unicode.fsx" ]
        assertSameOutput expected actual)
