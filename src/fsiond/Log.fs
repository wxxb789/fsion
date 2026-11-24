module Fsiond.Log

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading

let private append (path: string) (line: string) =
    let fullPath = Path.GetFullPath path
    let key = if OperatingSystem.IsWindows() then fullPath.ToUpperInvariant() else fullPath
    let name = "fsion-log-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes key))
    use gate = new Mutex(false, name)

    try
        gate.WaitOne() |> ignore
    with :? AbandonedMutexException ->
        ()

    try
        use stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete)
        use writer = new StreamWriter(stream)
        writer.Write line
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
