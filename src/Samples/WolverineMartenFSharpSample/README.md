# WolverineMartenFSharpSample

A small **F#** Wolverine application demonstrating the F# code-generation approach (issue
[GH-2969](https://github.com/JasperFx/wolverine/issues/2969)). This slice exercises **Marten document
persistence** (the EF Core slice lives in `WolverineFSharpSample`):

- `Domain.fs` — a `Product` document, a `CreateProductCommand`, and a `ProductCreated` event.
- `Handlers.fs` — `CreateProductHandler.Handle`, a `[<Transactional>]` handler that takes the command +
  an injected `IDocumentSession`, stores a `Product`, and returns a `ProductCreated` (cascaded).
- `Program.fs` — `UseWolverine` + `AddMarten(...).IntegrateWithWolverine()`, so Marten is both the
  document store and the durable message store backing the transactional outbox.

## Two things this proves

1. **It runs (dynamic codegen).** `dotnet run` boots the app, invokes a `CreateProductCommand`, and the
   F# handler stores a document through Marten inside Wolverine's transactional outbox. Core Wolverine
   no longer ships the Roslyn compiler ([GH-2876](https://github.com/JasperFx/wolverine/issues/2876)),
   so the sample references `Wolverine.RuntimeCompilation` and calls `opts.UseRuntimeCompilation()`.

2. **It static-codegens to F# (compile-gate).** `src/Testing/Wolverine.Marten.FSharpTests` renders this
   handler's real chain to F# via Wolverine's static codegen path and `dotnet build`s the result,
   proving the Marten document frames (`OpenMartenSessionFrame`, `CreateDocumentSessionFrame`,
   `SaveChangesAsync`, outbox flush) emit valid, compiling F#.

## Running it

Marten needs Postgres, so the runnable sample is **not** infra-free (the *static* F# story in the
compile-gate is). Start the repo's docker-compose infrastructure first:

```bash
docker compose up -d          # Postgres on :5433
dotnet run --project src/Samples/WolverineMartenFSharpSample --framework net9.0
```

Expected output: `Created a Product through the F# Wolverine + Marten handler.`

The sample uses a dedicated `wolverine_fsharp_marten` schema in the shared `postgres` database; Marten
provisions its document tables and `IntegrateWithWolverine` + `UseResourceSetupOnStartup()` provision
the Wolverine message-store tables.

## Later slices

Per #2969, the next slice is the **Marten event-sourced aggregate** (`MartenOps.StartStream`,
`[Aggregate]` loading) — the heaviest frame set — followed by richer HTTP and the behavioural run-step.
