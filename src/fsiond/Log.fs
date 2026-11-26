module Fsiond.Log

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Threading

module private PosixLogOpen =
    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int openFd(string pathname, int flags, int mode)

    [<DllImport("libc", EntryPoint = "fcntl", SetLastError = true)>]
    extern int fcntl(int fd, int cmd, int arg)

let private openAppendStream (fullPath: string) : FileStream option =
    if OperatingSystem.IsWindows() then
        let stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete)
        if stream.CanSeek then
            Some stream
        else
            stream.Dispose()
            None
    else
        // Open with O_WRONLY | O_CREAT | O_APPEND | O_NONBLOCK | O_CLOEXEC so a readerless POSIX FIFO
        // fails immediately with ENXIO instead of blocking `open(2)` on the response path, then verify
        // `CanSeek` (regular file) and clear `O_NONBLOCK` before writing.
        let oWrOnly = 0x0001
        let oCreat = if OperatingSystem.IsMacOS() then 0x0200 else 0x0040
        let oAppend = if OperatingSystem.IsMacOS() then 0x0008 else 0x0400
        let oNonBlock = if OperatingSystem.IsMacOS() then 0x0004 else 0x0800
        let oCloExec = if OperatingSystem.IsMacOS() then 0x01000000 else 0x00080000
        let fd = PosixLogOpen.openFd(fullPath, oWrOnly ||| oCreat ||| oAppend ||| oNonBlock ||| oCloExec, 0o644)
        if fd < 0 then
            None
        else
            let safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint fd, ownsHandle = true)
            let stream = new FileStream(safeHandle, FileAccess.Write)
            if not stream.CanSeek then
                stream.Dispose()
                None
            else
                let currentFlags = PosixLogOpen.fcntl(fd, 3, 0)
                if currentFlags >= 0 then
                    PosixLogOpen.fcntl(fd, 4, currentFlags &&& ~~~oNonBlock) |> ignore
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
