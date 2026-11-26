module Fsiond.Log

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Threading

module private PosixLogOpen =
    // 2-argument `open(pathname, flags)` (no `O_CREAT`, no variadic `mode` argument; safe on both x64 and Apple arm64).
    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int openExisting(string pathname, int flags)

let private openAppendStream (fullPath: string) : FileStream option =
    if OperatingSystem.IsWindows() then
        let stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete)
        if stream.CanSeek then
            Some stream
        else
            stream.Dispose()
            None
    else
        // Fast path: open existing path with `O_WRONLY | O_APPEND | O_NONBLOCK | O_CLOEXEC` (2 fixed args)
        // so a readerless POSIX FIFO fails immediately with ENXIO instead of blocking `open(2)`.
        // Creation path: only when `openExisting` fails with ENOENT (2) (missing file or dangling symlink),
        // use .NET's `FileStream(FileMode.CreateNew)` which calls `open(O_CREAT | O_EXCL, 0666)` via
        // CoreCLR's C PAL (ABI-safe on Apple arm64, atomic, non-truncating, never blocks on an existing FIFO).
        // Seek to End explicitly because .NET `FileStream(SafeFileHandle, ...)` starts at offset 0 and uses
        // `pwrite(2)`, which ignores `O_APPEND` on macOS.
        let oWrOnly = 0x0001
        let oAppend = if OperatingSystem.IsMacOS() then 0x0008 else 0x0400
        let oNonBlock = if OperatingSystem.IsMacOS() then 0x0004 else 0x0800
        let oCloExec = if OperatingSystem.IsMacOS() then 0x01000000 else 0x00080000
        let flags = oWrOnly ||| oAppend ||| oNonBlock ||| oCloExec
        let mutable fd = PosixLogOpen.openExisting(fullPath, flags)
        if fd < 0 && Marshal.GetLastPInvokeError() = 2 then // ENOENT on both Linux and macOS
            try
                use _created = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete)
                ()
            with :? IOException ->
                // Another process created the file concurrently (EEXIST); retry openExisting below.
                ()
            fd <- PosixLogOpen.openExisting(fullPath, flags)
        if fd < 0 then
            None
        else
            let safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint fd, ownsHandle = true)
            let stream = new FileStream(safeHandle, FileAccess.Write)
            if not stream.CanSeek then
                stream.Dispose()
                None
            else
                stream.Seek(0L, SeekOrigin.End) |> ignore
                Some stream
let private append (path: string) (line: string) =
    let fullPath = Path.GetFullPath path
    let key = if OperatingSystem.IsWindows() then fullPath.ToUpperInvariant() else fullPath
    let name = "fsion-log-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes key))
    use gate = new Mutex(false, name)

    let acquired =
        try
            gate.WaitOne(2000)
        with :? AbandonedMutexException ->
            true

    if acquired then
        try
            match openAppendStream fullPath with
            | Some stream ->
                use _stream = stream
                use writer = new StreamWriter(stream)
                writer.Write line
            | None -> ()
        finally
            gate.ReleaseMutex()
let private write (logPath: string option) (level: string) (message: string) =
    let line = DateTime.UtcNow.ToString("o") + " [" + level + "] " + message + Environment.NewLine
    match logPath with
    | Some path ->
        try
            append path line
        with _ ->
            ()
    | None ->
        try
            Console.Error.Write line
            Console.Error.Flush()
        with _ ->
            ()

let info (logPath: string option) (message: string) = write logPath "info" message

let error (logPath: string option) (message: string) = write logPath "error" message
