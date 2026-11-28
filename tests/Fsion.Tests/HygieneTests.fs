module Fsion.Tests.HygieneTests

open System
open System.IO
open System.Reflection.PortableExecutable
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

            let scriptDirectoryDllFiles = Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories)

            Assert.True(
                scriptDirectoryDllFiles.Length = 0,
                "unexpected dll files in the script directory: "
                + String.Join(", ", scriptDirectoryDllFiles)
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

[<Fact>]
let ``hot-path Zig shim executes warm eval, file, and stdin without FSION_CONFIG_DIR and stays zero-advapi32 on Windows`` () =
    if OperatingSystem.IsWindows() then
        // Guard against accidentally pulling advapi32/ucrt/shell32 into the zero-CRT Zig client.
        use stream = File.OpenRead fsionBinary
        use pe = new PEReader(stream)
        let headers = pe.PEHeaders
        let importDir = headers.PEHeader.ImportTableDirectory
        Assert.True(importDir.Size > 0, "expected PE import table in fsion.exe")
        let mutable offset = 0
        let importedDlls = ResizeArray<string>()
        let readCString (rva: int) =
            let section = pe.GetSectionData(rva)
            let bytes = section.GetContent()
            let mutable len = 0
            while len < bytes.Length && bytes.[len] <> 0uy do
                len <- len + 1
            System.Text.Encoding.ASCII.GetString(bytes.AsSpan(0, len))
        let tableBytes = pe.GetSectionData(importDir.RelativeVirtualAddress).GetContent()
        while offset + 20 <= tableBytes.Length do
            let nameRva = BitConverter.ToInt32(tableBytes.AsSpan(offset + 12, 4))
            if nameRva = 0 then
                offset <- tableBytes.Length
            else
                importedDlls.Add((readCString nameRva).ToLowerInvariant())
                offset <- offset + 20
        Assert.NotEmpty(importedDlls)
        for dll in importedDlls do
            Assert.True(
                dll = "kernel32.dll" || dll = "ntdll.dll",
                $"unexpected DLL import in fsion.exe (must stay kernel32/ntdll only): {dll}"
            )

    withContext "zig-hot-path" (fun context directory ->
        let warm = context.Run([ "warmup" ], timeoutMs = 120000)
        Assert.True(warm.ExitCode = 0, warm.Stderr)

        // Clear FSION_CONFIG_DIR and FSION_PRELUDE so the Zig shim takes the direct IPC fast path
        // (never falling back to fsion-cli).
        let hotEnv = [ "FSION_CONFIG_DIR", null; "FSION_PRELUDE", null ]

        let evalRes = context.Run([ "-e"; "printfn \"%s\" (String.concat \"|\" fsi.CommandLineArgs)"; "arg1"; "arg2" ], timeoutMs = 60000, extraEnv = hotEnv)
        Assert.True(evalRes.ExitCode = 0, evalRes.Stderr)
        Assert.Equal("eval.fsx|arg1|arg2" + Environment.NewLine, evalRes.Stdout)

        writeFile directory "hot.fsx" "printfn \"%s\" (String.concat \"|\" fsi.CommandLineArgs)\n"
        let fileRes = context.Run([ "hot.fsx"; "x"; "y" ], timeoutMs = 60000, extraEnv = hotEnv)
        Assert.True(fileRes.ExitCode = 0, fileRes.Stderr)
        Assert.Equal("hot.fsx|x|y" + Environment.NewLine, fileRes.Stdout)

        // Exercise multi-read stdin on the Zig hot path (validates single serialized StdinWorker on Windows).
        let stdinScript =
            "let a = System.Console.ReadLine()\n"
            + "let b = System.Console.ReadLine()\n"
            + "printfn \"%s:%s\" a b\n"
        let stdinRes = context.Run([ "-e"; stdinScript ], stdin = "first\nsecond\n", timeoutMs = 60000, extraEnv = hotEnv)
        Assert.True(stdinRes.ExitCode = 0, stdinRes.Stderr)
        Assert.Equal("first:second" + Environment.NewLine, stdinRes.Stdout))
