module Fsiond.Log

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Threading

module private PosixLogOpen =
    // Use `open(pathname, flags)` (no O_CREAT/mode variadic arg) when probing an existing path,
    // and `creat` (fixed 2-arg non-variadic POSIX function) only when the file does not yet exist,
    // avoiding variadic P/Invoke ABI mismatches on Apple arm64 (`open(..., mode)` / `fcntl(..., arg)`).
    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int openExisting(string pathname, int flags)

    [<DllImport("libc", EntryPoint = "creat", SetLastError = true)>]
    extern int creatFile(string pathname, uint32 mode)

let private openAppendStream (fullPath: string) : FileStream option =
    if OperatingSystem.IsWindows() then
        let stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete)
        if stream.CanSeek then
            Some stream
        else
            stream.Dispose()
            None
    else
        // Open existing path with `O_WRONLY | O_APPEND | O_NONBLOCK | O_CLOEXEC` (2 fixed args, no mode)
        // so a readerless POSIX FIFO fails immediately with ENXIO instead of blocking `open(2)`.
        // If the path does not yet exist (`File.Exists` false and open fails), create it with non-variadic
        // `creat(path, 0o644)` and reopen. Seek to End explicitly because .NET `FileStream` over an existing
        // `SafeFileHandle` starts at offset 0 and uses `pwrite(2)`, which ignores `O_APPEND` on macOS.
        let oWrOnly = 0x0001
        let oAppend = if OperatingSystem.IsMacOS() then 0x0008 else 0x0400
        let oNonBlock = if OperatingSystem.IsMacOS() then 0x0004 else 0x0800
        let oCloExec = if OperatingSystem.IsMacOS() then 0x01000000 else 0x00080000
        let flags = oWrOnly ||| oAppend ||| oNonBlock ||| oCloExec
        let mutable fd = PosixLogOpen.openExisting(fullPath, flags)
        if fd < 0 && not (File.Exists fullPath) then
            let created = PosixLogOpen.creatFile(fullPath, 0o644u)
            if created >= 0 then
                use _createdHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint created, ownsHandle = true)
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
