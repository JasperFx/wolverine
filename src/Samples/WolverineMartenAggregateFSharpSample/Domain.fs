namespace WolverineMartenAggregateFSharpSample

open System
open JasperFx.Events
open Marten.Events.Aggregation

/// Events for the Counter stream.
type CounterStarted = { Id: Guid }
type Incremented = { By: int }

/// The Marten event-sourced aggregate, built by single-stream aggregation from the Counter stream.
/// [<CLIMutable>] gives Marten the parameterless constructor + settable properties it needs.
[<CLIMutable>]
type Counter = { Id: Guid; Count: int }

/// A single-stream projection that folds the Counter events into the aggregate. It overrides Evolve
/// directly (an explicit per-event fold) rather than using convention Create/Apply methods: those are
/// dispatched by the C#-only JasperFx.Events source generator, which does not run for F# assemblies.
// begin-snippet: sample_fsharp_aggregate_projection
type CounterProjection() =
    inherit SingleStreamProjection<Counter, Guid>()

    override _.Evolve(snapshot: Counter, _id: Guid, e: IEvent) : Counter =
        match e.Data with
        | :? CounterStarted as started -> { Id = started.Id; Count = 0 }
        | :? Incremented as inc -> { snapshot with Count = snapshot.Count + inc.By }
        | _ -> snapshot
// end-snippet

/// The command handled by IncrementHandler. CounterId names the aggregate stream to load.
type IncrementCounter = { CounterId: Guid; By: int }
