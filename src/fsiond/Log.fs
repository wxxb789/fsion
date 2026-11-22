module Fsiond.Log

open System
open System.IO

let private write (logPath: string option) (level: string) (message: string) =
    let line = DateTime.UtcNow.ToString("o") + " [" + level + "] " + message + Environment.NewLine
    match logPath with
    | Some path ->
        try
            File.AppendAllText(path, line)
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
