# WolverineMartenAggregateFSharpSample

A small **F#** Wolverine application demonstrating the F# code-generation approach (issue
[GH-2969](https://github.com/JasperFx/wolverine/issues/2969)). This slice exercises **Marten event
sourcing** — the heaviest frame set (the document slice lives in `WolverineMartenFSharpSample`):

- `Domain.fs` — `CounterStarted`/`Incremented` events, a `Counter` aggregate, a `CounterProjection`,
  and an `IncrementCounter` command.
- `Handlers.fs` — `IncrementHandler.Handle`, an `[<AggregateHandler>]` that receives the loaded
  `Counter` aggregate + the command and returns an `Incremented` event (appended to the stream).
- `Program.fs` — `UseWolverine` + `AddMarten(...).IntegrateWithWolverine()`, seeds a Counter stream,
  then invokes the increment command.

## Two things this proves

1. **It runs (dynamic codegen).** `dotnet run` boots the app, seeds a stream, invokes
   `IncrementCounter`, and the F# `[<AggregateHandler>]` loads the aggregate via `FetchForWriting`,
   runs the decision method, and appends the returned event. Core Wolverine no longer ships the Roslyn
   compiler ([GH-2876](https://github.com/JasperFx/wolverine/issues/2876)), so the sample references
   `Wolverine.RuntimeCompilation` and calls `opts.UseRuntimeCompilation()`.

2. **It static-codegens to F# (compile-gate).** `src/Testing/Wolverine.MartenAggregate.FSharpTests`
   renders this handler's real chain to F# via Wolverine's static codegen path and `dotnet build`s the
   result, proving the Marten aggregate frames (`LoadAggregateFrame` → `FetchForWriting`,
   `TagAggregateOtelFrame`, `MissingAggregateCheckFrame`, `RegisterEvents`, `SaveChanges`) emit valid F#.

## F# + Marten event sourcing: a key constraint

Marten's convention-based aggregation (`Create`/`Apply` methods) is dispatched by the **C#-only**
`JasperFx.Events` source generator, which does **not** run for F# assemblies. So `CounterProjection`
**overrides `Evolve` directly** (an explicit per-event fold) instead of using convention methods. This
is the supported escape hatch for self-aggregating types defined in F#.

## Running it

Marten needs Postgres, so the runnable sample is **not** infra-free (the *static* F# story in the
compile-gate is). Start the repo's docker-compose infrastructure first:

```bash
docker compose up -d          # Postgres on :5433
dotnet run --project src/Samples/WolverineMartenAggregateFSharpSample --framework net9.0
```

Expected output: `Incremented the Counter through the F# Wolverine + Marten aggregate handler.`

The sample uses a dedicated `wolverine_fsharp_aggregate` schema in the shared `postgres` database.
