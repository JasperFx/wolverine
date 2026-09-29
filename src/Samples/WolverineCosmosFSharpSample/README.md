# WolverineCosmosFSharpSample

A small **F#** Wolverine application demonstrating the F# code-generation approach (issue
[GH-2969](https://github.com/JasperFx/wolverine/issues/2969)). This slice combines **two middleware
surfaces** in one handler:

- **FluentValidation** — `Domain.fs` defines a `CreateThingValidator : AbstractValidator<CreateThing>`;
  Wolverine's FluentValidation middleware runs it before the handler (and short-circuits on failure).
- **CosmosDB persistence** — `Handlers.fs` has a `[<Transactional>]` `CreateThingHandler` that returns
  an `ICosmosDbOp` (`CosmosDbOps.Store`); the CosmosDB transaction middleware enlists the outbox and
  the side effect is applied within it.

`Program.fs` wires `UseWolverine` + `UseFluentValidation()` + `UseCosmosDbPersistence(...)`, backed by
the Azure Cosmos DB emulator.

## Two things this proves

1. **It runs (dynamic codegen).** `dotnet run` boots the app, invokes a `CreateThing`, FluentValidation
   validates it, and the `ICosmosDbOp` stores the document inside the CosmosDB outbox transaction. Core
   Wolverine no longer ships the Roslyn compiler ([GH-2876](https://github.com/JasperFx/wolverine/issues/2876)),
   so the sample references `Wolverine.RuntimeCompilation` and calls `opts.UseRuntimeCompilation()`.

2. **It static-codegens to F# (compile-gate).** `src/Testing/Wolverine.Cosmos.FSharpTests` renders this
   handler's real chain to F# via Wolverine's static codegen path and `dotnet build`s the result,
   proving the FluentValidation `ExecuteOne` call, the CosmosDB `TransactionalFrame`, the `ISideEffect`
   `Execute`, and the outbox flush emit valid, compiling F#. (Only `TransactionalFrame` needed new F#
   emit; the rest were already covered by `MethodCall`/existing frames.)

## A note on F# + FluentValidation

`AbstractValidator<T>.RuleFor` takes a LINQ `Expression<Func<T, TProperty>>`. F# auto-converts the
property-selector lambdas (`fun x -> x.Name`) to those expression trees, so the validator reads
naturally.

## Running it

CosmosDB needs the Azure Cosmos DB emulator, so the runnable sample is **not** infra-free (the *static*
F# story in the compile-gate is). The emulator is **not** one of the repo's `docker-compose` services —
`CosmosDbTests` starts its own throwaway container through Testcontainers, which a sample cannot borrow —
so start one yourself. `Program.fs` connects to the emulator's well-known endpoint and key on
`localhost:8081`, so the defaults below need no configuration:

```bash
docker run -d --name cosmos-emulator -p 8081:8081 -p 10250-10255:10250-10255 \
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest

dotnet run --project src/Samples/WolverineCosmosFSharpSample --framework net9.0
```

The emulator takes a minute or two to become ready, and serves a self-signed certificate — the sample
already accepts it.

Expected output: `Stored a Thing through the F# Wolverine + CosmosDB handler (with FluentValidation).`
