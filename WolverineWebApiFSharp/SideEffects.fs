module WolverineWebApiFSharp.SideEffects

open System
open Wolverine
open Wolverine.Http
open Wolverine.Marten

type Command = { Name: string }

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

// this one is a bit more tricky, generally using ISideEffect works,
// but for MartenOps.StartStream one has to provide the generic type parameter, otherwise
// codegen will not generate a handler at all.

[<WolverinePost("start-stream")>]
[<EmptyResponse>]
let post (command: Command) =
    let event: Event = { Id = Guid.NewGuid(); Name = command.Name }
    //this doesn't work
    MartenOps.StartStream(event.Id, box event)
    
    // but this does
    //MartenOps.StartStream<SomeType>(event.Id, box event)