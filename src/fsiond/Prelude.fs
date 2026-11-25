module Fsiond.Prelude

open System
open System.IO
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

let hashFile (path: string) : string option =
    try
        Some(digest (File.ReadAllBytes path))
    with _ ->
        None

// Hash the raw bytes exactly like hashFile while decoding with BOM detection: hashing the
// decoded text would tag a BOM'd prelude differently from what tagOfSpec computes on claims.
let readAndHashFile (path: string) : (string * string) option =
    try
        let bytes = File.ReadAllBytes path
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
