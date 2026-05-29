# WolverineFSharpSample

A small **F#** Wolverine application demonstrating the F# code-generation approach (issue
[GH-2969](https://github.com/JasperFx/wolverine/issues/2969)). This is the first slice of the F#
sample and exercises **EF Core**:

- `Domain.fs` — an `Item` entity, an `ItemsDbContext`, a `CreateItemCommand`, and an `ItemCreated` event.
- `Handlers.fs` — `CreateItemHandler.Handle`, a `[<Transactional>]` handler that takes the command +
  the `ItemsDbContext`, adds an `Item`, and returns an `ItemCreated` (cascaded through the outbox).
- `Program.fs` — a generic host wiring `UseWolverine` + `AddDbContextWithWolverineIntegration` +
  `UseEntityFrameworkCoreTransactions` + `AutoApplyTransactions`, backed by a Postgres message store.

## Two things this proves

1. **It runs (dynamic codegen).** `dotnet run` boots the app, invokes a `CreateItemCommand`, and the
   F# handler writes through EF Core inside Wolverine's transactional outbox. Core Wolverine no longer
   ships the Roslyn compiler ([GH-2876](https://github.com/JasperFx/wolverine/issues/2876)), so the
   sample references `Wolverine.RuntimeCompilation` and calls `opts.UseRuntimeCompilation()`.

2. **It static-codegens to F# (compile-gate).** `src/Testing/Wolverine.EfCore.FSharpTests` renders this
   handler's real chain to F# via Wolverine's static codegen path and `dotnet build`s the result,
   proving the EF Core transactional frames (scoped-DI resolution, `EnrollDbContextInTransaction`,
   `SaveChangesAsync`, `CommitEfCoreEnvelopeTransaction`) emit valid, compiling F#.

## Running it

Wolverine's EF Core outbox needs a durable message store, so the runnable sample is **not** infra-free
(the *static* F# story in the compile-gate is). Start the repo's docker-compose infrastructure first:

```bash
docker compose up -d          # Postgres on :5433
dotnet run --project src/Samples/WolverineFSharpSample --framework net9.0
```

Expected output: `Created an Item through the F# Wolverine + EF Core handler.`

The sample uses a dedicated `wolverine_fsharp_sample` database (EF Core `EnsureCreated()` provisions
the `Item` table; `UseResourceSetupOnStartup()` provisions the Wolverine message-store tables).

## Later slices

Per #2969, the sample grows to mix Marten (document + event-sourced aggregate) and richer HTTP, each
driving the remaining store-specific frames' F# emit toward complete coverage.
