module WolverineWebApiFSharp.DiscoverFSharp

open Wolverine.Http


[<WolverinePost("/discovered-fsharp-unit")>]
let myHandler() =
    task {
       () 
    }