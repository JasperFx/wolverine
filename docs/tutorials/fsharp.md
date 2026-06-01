# Using Wolverine with F#

Wolverine works with F#, not just C#. You can author message handlers as F# functions/methods, use the
EF Core, Marten, CosmosDB, and FluentValidation integrations, and — the focus of this tutorial — ship an
F# application that runs entirely on **pre-generated F# code** with no runtime code generation.

::: tip
All of the F# in this tutorial comes from runnable samples under
[`src/Samples`](https://github.com/JasperFx/wolverine/tree/main/src/Samples) (the `Wolverine*FSharpSample`
projects) and the behavioural test under
[`src/Testing/Wolverine.Behavioural.FSharp*`](https://github.com/JasperFx/wolverine/tree/main/src/Testing).
:::

## How Wolverine code generation works with F#

For every message handler, Wolverine generates a small adapter class (a `MessageHandler`) that pulls the
message off the envelope, resolves dependencies, runs your handler, applies middleware (transactions,
validation, the outbox, OpenTelemetry tagging, cascading messages…), and saves changes. By default that
adapter is generated and compiled **at startup** with Roslyn (`TypeLoadMode.Dynamic`). Wolverine can also
emit that adapter as **F#** and load it from your already-compiled assembly at runtime
(`TypeLoadMode.Static`) — so an F# app can ship with zero runtime compilation.

There are therefore two ways to run an F# Wolverine app:

1. **Dynamic code generation** — quickest to get going; needs the Roslyn runtime compiler.
2. **Pre-generated (static) code** — commit the generated F# and load it under `TypeLoadMode.Static`.

## Writing a handler in F#

A handler is just a type with a `Handle`/`Consume` method. Here is an EF Core transactional handler — it
takes the command and an injected `DbContext`, writes an entity, and returns an event that Wolverine
cascades as an outgoing message:

<!-- snippet: sample_fsharp_efcore_handler -->
<a id='snippet-sample_fsharp_efcore_handler'></a>
```fs
type CreateItemHandler =
    [<Transactional>]
    static member Handle(command: CreateItemCommand, db: ItemsDbContext) : ItemCreated =
        let item = { Id = Guid.NewGuid(); Name = command.Name }
        db.Items.Add(item) |> ignore
        { Id = item.Id }
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineFSharpSample/Handlers.fs#L10-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fsharp_efcore_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Records work well for messages and events, and `[<CLIMutable>]` gives EF Core / Marten / CosmosDB the
parameterless constructor and settable properties they need for entities and documents.

Bootstrapping looks just like C#, through the `UseWolverine` lambda:

<!-- snippet: sample_fsharp_efcore_bootstrap -->
<a id='snippet-sample_fsharp_efcore_bootstrap'></a>
```fs
let host =
    Host
        .CreateDefaultBuilder(args)
        .UseWolverine(fun opts ->
            // Register the DbContext with Wolverine's EF Core outbox integration.
            opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(fun o ->
                o.UseNpgsql(connectionString) |> ignore)
            |> ignore

            // Durable message store backing the transactional outbox.
            opts.PersistMessagesWithPostgresql(connectionString) |> ignore

            opts.UseEntityFrameworkCoreTransactions() |> ignore
            opts.Policies.AutoApplyTransactions() |> ignore
            opts.Discovery.IncludeType<CreateItemHandler>() |> ignore

            // Core Wolverine dropped the in-box Roslyn compiler (GH-2876); enable it so this demo
            // runs via dynamic codegen. (The static F# story is proven by the compile-gate test.)
            opts.UseRuntimeCompilation() |> ignore)
        // Provision the Wolverine message-store tables on startup.
        .UseResourceSetupOnStartup()
        .Build()
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineFSharpSample/Program.fs#L23-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fsharp_efcore_bootstrap' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning Dynamic code generation needs the runtime compiler
Core Wolverine no longer ships the Roslyn compiler ([GH-2876](https://github.com/JasperFx/wolverine/issues/2876)).
To run via **dynamic** code generation, reference the `WolverineFx.RuntimeCompilation` package and call
`opts.UseRuntimeCompilation()` as shown above. Apps that run on pre-generated code (below) do not need it.
:::

## What Wolverine generates — as F#

Take this minimal F# handler:

<!-- snippet: sample_fsharp_behavioural_handler -->
<a id='snippet-sample_fsharp_behavioural_handler'></a>
```fs
type BehaviouralPingHandler =
    static member Handle(ping: BehaviouralPing) = BehaviouralSink.record ping.Value
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/Wolverine.Behavioural.FSharpApp/Domain.fs#L25-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fsharp_behavioural_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine emits the following F# adapter for it. Notice the idiomatic F#: the message downcast with `:?>`,
the `task { }` computation expression, the OpenTelemetry guard (F# has no `?.`), and the trailing
`Task.CompletedTask`:

```fsharp
namespace Internal.Generated.WolverineHandlers

open System
open System.Threading
open System.Threading.Tasks
open Wolverine.Runtime
open Wolverine.Runtime.Handlers

type BehaviouralPingHandler1244766258() =
    inherit Wolverine.Runtime.Handlers.MessageHandler()
    override this.HandleAsync(context: Wolverine.Runtime.MessageContext, cancellation: System.Threading.CancellationToken) : System.Threading.Tasks.Task =
        // The actual message body
        let behaviouralPing = context.Envelope.Message :?> WolverineBehaviouralFSharpApp.BehaviouralPing

        if not (isNull System.Diagnostics.Activity.Current) then
            System.Diagnostics.Activity.Current.SetTag("message.handler", "WolverineBehaviouralFSharpApp.BehaviouralPingHandler") |> ignore
            System.Diagnostics.Activity.Current.SetTag("handler.type", "WolverineBehaviouralFSharpApp.BehaviouralPingHandler") |> ignore

        // The actual message execution
        WolverineBehaviouralFSharpApp.BehaviouralPingHandler.Handle(behaviouralPing)

        System.Threading.Tasks.Task.CompletedTask
```

## Generating the F# code from the command line

Wolverine apps answer the JasperFx command line (the final `RunJasperFxCommands(args)` in your
`Program`). As of JasperFx 2.4.1 the `codegen` command takes a `--language` flag, so you can write the
pre-generated code out as **F#** instead of C#:

```bash
dotnet run -- codegen write --language fsharp
```

This emits one `.fs` file per generated type into your code-generation output directory
(`Internal/Generated/…` by default) — the handler adapters plus Wolverine's static
`GeneratedHandlerRegistry`. Because F# requires explicit, ordered compilation, add the generated files
to your `.fsproj` `<Compile>` list (the registry and adapters depend on your handler/message types, so
list them after those):

```xml
<ItemGroup>
  <Compile Include="Domain.fs" />
  <!-- generated by: dotnet run -- codegen write --language fsharp -->
  <Compile Include="Internal/Generated/WolverineHandlers/GeneratedHandlerRegistry.fs" />
  <Compile Include="Internal/Generated/WolverineHandlers/MyMessageHandlerNNNNN.fs" />
</ItemGroup>
```

Re-run the command (and commit the regenerated files) whenever your handler graph changes; the generated
type names are deterministic for a given handler graph.

## Running on pre-generated F# code

To ship an F# app that runs on pre-generated code rather than compiling at startup:

1. Generate the F# adapters with `codegen write --language fsharp` (above) and commit them into your
   application, compiled into the app assembly. The generated type names are deterministic for a given
   handler graph.
2. Boot the host in `TypeLoadMode.Static` and point Wolverine's `ApplicationAssembly` at the assembly that
   contains the pre-generated F# — Wolverine then loads each handler adapter **by name** out of that
   assembly's exported types, with no Roslyn at runtime:

<!-- snippet: sample_fsharp_static_host -->
<a id='snippet-sample_fsharp_static_host'></a>
```cs
var appAssembly = typeof(BehaviouralPingHandler).Assembly;

using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Shared with the generation step so the generated type-name hash matches:
        // DisableConventionalDiscovery() + IncludeType<BehaviouralPingHandler>().
        BehaviouralCodegen.Configure(opts);

        // Load the pre-generated F# handler adapter out of the app assembly instead of
        // compiling at runtime. Setting ApplicationAssembly (which cascades to
        // CodeGeneration.ApplicationAssembly) pins the assembly Wolverine scans for pre-built
        // types to the F# app, and TypeLoadMode.Static means no Roslyn at runtime.
        opts.ApplicationAssembly = appAssembly;
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
    })
    .StartAsync();

// The pre-generated F# MessageHandler is loaded by name and executed — no runtime compilation.
var bus = host.MessageBus();
await bus.InvokeAsync(new BehaviouralPing(42));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/Wolverine.Behavioural.FSharpTests/BehaviouralRunStep.cs#L33-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fsharp_static_host' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
Setting `opts.ApplicationAssembly` cascades to `opts.CodeGeneration.ApplicationAssembly` and pins the
assembly Wolverine scans for pre-built types. If the committed F# drifts from the configured handler graph
the generated type name won't match and the host throws `ExpectedTypeMissingException` at startup — a loud,
useful signal to regenerate.
:::

## Persistence and middleware in F#

The same handler authoring style works across Wolverine's integrations. Each of these is exercised by a
runnable sample and a code-generation compile gate.

### Marten documents

A `[<Transactional>]` handler that stores a document through an injected `IDocumentSession`:

<!-- snippet: sample_fsharp_marten_document_handler -->
<a id='snippet-sample_fsharp_marten_document_handler'></a>
```fs
type CreateProductHandler =
    [<Transactional>]
    static member Handle(command: CreateProductCommand, session: IDocumentSession) : ProductCreated =
        let product = { Id = Guid.NewGuid(); Name = command.Name }
        session.Store<Product>(product)
        { Id = product.Id }
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineMartenFSharpSample/Handlers.fs#L11-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fsharp_marten_document_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Marten event sourcing

An `[<AggregateHandler>]` receives the loaded aggregate (via `FetchForWriting`) plus the command and returns
the event(s) to append:

<!-- snippet: sample_fsharp_aggregate_handler -->
<a id='snippet-sample_fsharp_aggregate_handler'></a>
```fs
type IncrementHandler =
    [<AggregateHandler>]
    static member Handle(command: IncrementCounter, counter: Counter) : Incremented =
        { By = command.By }
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineMartenAggregateFSharpSample/Handlers.fs#L9-L14' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fsharp_aggregate_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning F# aggregates: override `Evolve`
Marten's convention-based aggregation (`Create`/`Apply` methods) is dispatched by the **C#-only**
`JasperFx.Events` source generator, which does not run for F# assemblies. For an F# aggregate, override
`Evolve` directly — an explicit per-event fold — instead of using convention methods:
:::

<!-- snippet: sample_fsharp_aggregate_projection -->
<a id='snippet-sample_fsharp_aggregate_projection'></a>
```fs
type CounterProjection() =
    inherit SingleStreamProjection<Counter, Guid>()

    override _.Evolve(snapshot: Counter, _id: Guid, e: IEvent) : Counter =
        match e.Data with
        | :? CounterStarted as started -> { Id = started.Id; Count = 0 }
        | :? Incremented as inc -> { snapshot with Count = snapshot.Count + inc.By }
        | _ -> snapshot
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineMartenAggregateFSharpSample/Domain.fs#L19-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fsharp_aggregate_projection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### FluentValidation

Register `opts.UseFluentValidation()` and a validator; the validation middleware runs before the handler.
F# automatically converts the property-selector lambdas (`fun x -> x.Name`) into the LINQ expression trees
`RuleFor` expects, so an F# `AbstractValidator` reads naturally:

<!-- snippet: sample_fsharp_fluentvalidation_validator -->
<a id='snippet-sample_fsharp_fluentvalidation_validator'></a>
```fs
type CreateThingValidator() as self =
    inherit AbstractValidator<CreateThing>()

    do
        self.RuleFor(fun x -> x.Id).NotEmpty() |> ignore
        self.RuleFor(fun x -> x.Name).NotEmpty() |> ignore
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineCosmosFSharpSample/Domain.fs#L18-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fsharp_fluentvalidation_validator' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### CosmosDB

A `[<Transactional>]` handler that returns an `ICosmosDbOp` side effect; Wolverine applies it inside the
CosmosDB outbox transaction:

<!-- snippet: sample_fsharp_cosmos_handler -->
<a id='snippet-sample_fsharp_cosmos_handler'></a>
```fs
type CreateThingHandler =
    [<Transactional>]
    static member Handle(command: CreateThing) : ICosmosDbOp =
        CosmosDbOps.Store<Thing>({ id = command.Id; Name = command.Name })
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WolverineCosmosFSharpSample/Handlers.fs#L10-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fsharp_cosmos_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## F# gotchas, summarized

- **Dynamic codegen** needs `WolverineFx.RuntimeCompilation` + `opts.UseRuntimeCompilation()` (GH-2876).
  Pre-generated/static apps don't.
- **Entities & documents**: use `[<CLIMutable>]` records so the persistence tooling can construct/populate them.
- **Marten aggregates**: override `Evolve` rather than using convention `Create`/`Apply` (the source
  generator is C#-only).
- **FluentValidation**: F# auto-quotes `RuleFor` lambdas to expression trees — no special handling needed.
- **Static loading**: set `opts.ApplicationAssembly` to the assembly carrying the committed F# adapters.
