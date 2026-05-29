module WolverineMartenAggregateFSharpSample.Program

open System
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Marten
open Marten.Events.Projections
open JasperFx.Events.Projections
open JasperFx.Resources
open Wolverine
open Wolverine.Marten
open WolverineMartenAggregateFSharpSample

[<Literal>]
let connectionString =
    "Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres"

[<EntryPoint>]
let main args =
    let host =
        Host
            .CreateDefaultBuilder(args)
            .UseWolverine(fun opts ->
                opts.Services
                    .AddMarten(fun (m: StoreOptions) ->
                        m.Connection(connectionString)
                        m.DatabaseSchemaName <- "wolverine_fsharp_aggregate"
                        // Inline single-stream projection so FetchForWriting can rebuild the Counter.
                        m.Projections.Add(CounterProjection(), ProjectionLifecycle.Inline))
                    .IntegrateWithWolverine()
                |> ignore

                opts.Policies.AutoApplyTransactions() |> ignore
                opts.Discovery.IncludeType<IncrementHandler>() |> ignore
                opts.UseRuntimeCompilation() |> ignore)
            .UseResourceSetupOnStartup()
            .Build()

    host.Start()

    // Seed a Counter stream so the aggregate handler has something to load + append to.
    let counterId = Guid.NewGuid()

    (use scope = host.Services.CreateScope()
     let store = scope.ServiceProvider.GetRequiredService<IDocumentStore>()
     use session = store.LightweightSession()
     session.Events.StartStream<Counter>(counterId, [| box ({ Id = counterId }: CounterStarted) |]) |> ignore
     session.SaveChangesAsync().GetAwaiter().GetResult())

    // Demonstrate the F# Marten aggregate handler end-to-end (dynamic codegen): load the stream,
    // run Handle, append the returned Incremented event.
    let bus = host.Services.GetRequiredService<IMessageBus>()
    bus.InvokeAsync({ CounterId = counterId; By = 5 }).GetAwaiter().GetResult()
    printfn "Incremented the Counter through the F# Wolverine + Marten aggregate handler."

    host.StopAsync().GetAwaiter().GetResult()
    0
