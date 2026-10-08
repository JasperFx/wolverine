module WolverineWebApiFSharp.SideEffects

open System
open Wolverine
open Wolverine.Http
open Wolverine.Marten

// Not just "Command": TupleSupport has one too, and two endpoints whose request types share a name
// collide as Swashbuckle schema ids and fold into one Event Model slice.
type StartStreamCommand = { Name: string }

(* I cannot define a custom side effect here because code generation does not recognize the Execute method
Unhandled exception. Wolverine.InvalidSideEffectException: Invalid Wolverine side effect exception for Wolverine.ISideEffect, no public Execute/ExecuteAsync method found
   at Wolverine.SideEffectPolicy.applySideEffectExecution(Variable effect, IChain chain) in /Users/marcpiechura/RiderProjects/wolverine/src/Wolverine/ISideEffect.cs:line 93
   at Wolverine.SideEffectPolicy.lookForSingularSideEffects(GenerationRules rules, IServiceContainer container, IChain chain) in /Users/marcpiechura/RiderProjects/wolverine/src/Wolverine/ISideEffect.cs:line 62
   at Wolverine.SideEffectPolicy.Apply(IReadOnlyList`1 chains, GenerationRules rules, IServiceContainer container) in /Users/marcpiechura/RiderProjects/wolverine/src/Wolverine/ISideEffect.cs:line 42
   at Wolverine.Http.HttpGraph.DiscoverEndpoints(WolverineHttpOptions wolverineHttpOptions) in /Users/marcpiechura/RiderProjects/wolverine/src/Http/Wolverine.Http/HttpGraph.cs:line 107
   at Wolverine.Http.WolverineHttpEndpointRouteBuilderExtensions.MapWolverineEndpoints(IEndpointRouteBuilder endpoints, Action`1 configure) in /Users/marcpiechura/RiderProjects/wolverine/src/Http/Wolverine.Http/WolverineHttpEndpointRouteBuilderExtensions.cs:line 202

*)
type SomeSideEffect() =
    static member val WasExecuted = false with get, set
    
    interface ISideEffect
    
    member this.Execute() =
        SomeSideEffect.WasExecuted <- true

    
type Event = { Id: Guid; Name: string }
type SomeType = { Id: Guid }

// MartenOps.StartStream without a generic type parameter used to bind nothing usable from F#, so
// codegen generated no handler at all. GH-4892 added the untyped MartenOps.StartStream(id, events), a
// stream with no aggregate type, so both spellings work now.

[<WolverinePost("start-stream")>]
[<EmptyResponse>]
let post (command: StartStreamCommand) =
    let event: Event = { Id = Guid.NewGuid(); Name = command.Name }
    MartenOps.StartStream(event.Id, box event)
    
    // or, for a stream of a known aggregate type
    //MartenOps.StartStream<SomeType>(event.Id, box event)