module Fsiond.Prelude

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open Fsion
open Fsion.Protocol

[<NoComparison>]
type Spec =
    | Bare
    | Builtin
    | File of path: string

[<NoComparison>]
type Tag =
    | Bare
    | Builtin
    | File of path: string * contentHash: string

let private digest (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let private normalizePath (path: string) =
    try
        Path.GetFullPath path
    with _ ->
        path

let hashText (text: string) = digest (Encoding.UTF8.GetBytes text)

let escapeVerbatim (value: string) = value.Replace("\"", "\"\"")

module private PosixOpen =
    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int openFd(string pathname, int flags)

    [<DllImport("libc", EntryPoint = "close", SetLastError = true)>]
    extern int closeFd(int fd)

    [<DllImport("libc", EntryPoint = "fcntl", SetLastError = true)>]
    extern int fcntl(int fd, int cmd, int arg)

// On POSIX, opening a named FIFO with plain `FileStream(FileMode.Open)` blocks indefinitely inside
// `open(2)` when no writer is attached, which could stall background prelude tag polling or `creationGate`
// handoff. Open with `O_RDONLY | O_NONBLOCK | O_CLOEXEC`, reject non-regular files (`CanSeek = false`),
// and clear `O_NONBLOCK` before reading regular file bytes.
let private readAllBytesShared (path: string) : byte[] =
    if OperatingSystem.IsWindows() then
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
        if not stream.CanSeek then
            raise (IOException("prelude path is not a regular seekable file: " + path))
        let length = stream.Length
        if length = 0L then
            [||]
        else
            use ms = new MemoryStream(if length <= int64 Int32.MaxValue then int length else 4096)
            stream.CopyTo ms
            ms.ToArray()
    else
        let oNonBlock = if OperatingSystem.IsMacOS() then 0x0004 else 0x0800
        let oCloExec = if OperatingSystem.IsMacOS() then 0x01000000 else 0x00080000
        let fd = PosixOpen.openFd(path, oNonBlock ||| oCloExec)
        if fd < 0 then
            raise (IOException("could not open prelude file: " + path))
        use safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint fd, ownsHandle = true)
        use stream = new FileStream(safeHandle, FileAccess.Read)
        if not stream.CanSeek then
            raise (IOException("prelude path is not a regular seekable file: " + path))
        let currentFlags = PosixOpen.fcntl(fd, 3, 0)
        if currentFlags >= 0 then
            PosixOpen.fcntl(fd, 4, currentFlags &&& ~~~oNonBlock) |> ignore
        let length = stream.Length
        if length = 0L then
            [||]
        else
            use ms = new MemoryStream(if length <= int64 Int32.MaxValue then int length else 4096)
            stream.CopyTo ms
            ms.ToArray()
let hashFile (path: string) : string option =
    try
        Some(digest (readAllBytesShared path))
    with _ ->
        None

// Hash the raw bytes exactly like hashFile while decoding with BOM detection: hashing the
// decoded text would tag a BOM'd prelude differently from what tagOfSpec computes on claims.
let readAndHashFile (path: string) : (string * string) option =
    try
        let bytes = readAllBytesShared path
        use reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true)
        Some(reader.ReadToEnd(), digest bytes)
    with _ ->
        None

let tagOfSpec (spec: Spec) : Tag =
    match spec with
    | Spec.Bare -> Tag.Bare
    | Spec.Builtin -> Tag.Builtin
    | Spec.File path ->
        let path = normalizePath path

        match hashFile path with
        | Some contentHash -> Tag.File(path, contentHash)
        | None -> Tag.File(path, "unreadable")

let specOfRequest (request: ExecuteRequest) : Spec =
    if request.NoPrelude then
        Spec.Bare
    elif request.PreludePath.IsSome then
        Spec.File(normalizePath request.PreludePath.Value)
    else
        match PreludeConfig.userPreludePathForEnvironment request.WorkingDirectory request.Environment with
        | Some path -> Spec.File(normalizePath path)
        | None -> Spec.Builtin

let specOfOptions (preludePath: string option) (noPrelude: bool) : Spec =
    if noPrelude then
        Spec.Bare
    elif preludePath.IsSome then
        Spec.File(normalizePath preludePath.Value)
    else
        Spec.Builtin
