module Fsiond.Ambient

open System
open System.IO
open System.Text
open System.Threading

[<NoComparison; NoEquality>]
type RunStreams =
    { Out: TextWriter
      Error: TextWriter
      In: TextReader }

let private current = AsyncLocal<RunStreams option>()

let set (streams: RunStreams) = current.Value <- Some streams

let clear () = current.Value <- None

type internal RoutingWriter(select: RunStreams -> TextWriter) =
    inherit TextWriter()

    override _.Encoding = UTF8Encoding(false)

    override _.Write(value: char) =
        match current.Value with
        | Some streams -> (select streams).Write value
        | None -> ()

    override _.Write(value: string) =
        match current.Value with
        | Some streams -> (select streams).Write value
        | None -> ()

    override _.Write(buffer: char[], index: int, count: int) =
        match current.Value with
        | Some streams -> (select streams).Write(buffer, index, count)
        | None -> ()

    override _.Flush() =
        match current.Value with
        | Some streams -> (select streams).Flush()
        | None -> ()

type internal RoutingReader() =
    inherit TextReader()

    override _.Peek() =
        match current.Value with
        | Some streams -> streams.In.Peek()
        | None -> -1

    override _.Read() =
        match current.Value with
        | Some streams -> streams.In.Read()
        | None -> -1

    override _.Read(buffer: char[], index: int, count: int) =
        match current.Value with
        | Some streams -> streams.In.Read(buffer, index, count)
        | None -> 0

    override _.ReadLine() =
        match current.Value with
        | Some streams -> streams.In.ReadLine()
        | None -> null

    override _.ReadToEnd() =
        match current.Value with
        | Some streams -> streams.In.ReadToEnd()
        | None -> String.Empty

let outWriter: TextWriter = new RoutingWriter(fun streams -> streams.Out)

let errorWriter: TextWriter = new RoutingWriter(fun streams -> streams.Error)

let install () =
    Console.SetOut outWriter
    Console.SetError errorWriter
    Console.SetIn(new RoutingReader())
