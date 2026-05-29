module WolverineCosmosFSharpSample.Program

open System
open System.Net.Http
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Azure.Cosmos
open FluentValidation
open JasperFx.Resources
open Wolverine
open Wolverine.CosmosDb
open Wolverine.FluentValidation
open WolverineCosmosFSharpSample

// The well-known Azure Cosmos DB emulator endpoint + key (the emulator is in docker-compose).
[<Literal>]
let connectionString =
    "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="

[<Literal>]
let databaseName = "wolverine_fsharp_cosmos"

// CosmosClient configured for the local emulator (Gateway mode + accept its self-signed cert).
let private buildClient () =
    let options = CosmosClientOptions(ConnectionMode = ConnectionMode.Gateway)
    options.HttpClientFactory <-
        Func<HttpClient>(fun () ->
            new HttpClient(
                new HttpClientHandler(
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator)))
    new CosmosClient(connectionString, options)

[<EntryPoint>]
let main args =
    // Wolverine's CosmosDB resource setup creates containers but not the database itself; pre-create it.
    (use client = buildClient ()
     client.CreateDatabaseIfNotExistsAsync(databaseName).GetAwaiter().GetResult() |> ignore)

    let host =
        Host
            .CreateDefaultBuilder(args)
            .UseWolverine(fun opts ->
                opts.Services.AddSingleton<CosmosClient>(fun _ -> buildClient ()) |> ignore

                // FluentValidation middleware + the validator.
                opts.Services.AddScoped<IValidator<CreateThing>, CreateThingValidator>() |> ignore
                opts.UseFluentValidation() |> ignore

                // CosmosDB as the durable message store backing the transactional outbox.
                opts.UseCosmosDbPersistence(databaseName) |> ignore

                opts.Policies.AutoApplyTransactions() |> ignore
                opts.Discovery.IncludeType<CreateThingHandler>() |> ignore

                // Core Wolverine dropped the in-box Roslyn compiler (GH-2876); enable it so this demo
                // runs via dynamic codegen. (The static F# story is proven by the compile-gate test.)
                opts.UseRuntimeCompilation() |> ignore)
            .UseResourceSetupOnStartup()
            .Build()

    host.Start()

    // Demonstrate the F# Wolverine + CosmosDB handler end-to-end (dynamic codegen): FluentValidation
    // runs, then the ICosmosDbOp side effect stores the document inside the outbox transaction.
    let bus = host.Services.GetRequiredService<IMessageBus>()
    bus.InvokeAsync({ Id = Guid.NewGuid().ToString(); Name = "Sample" }).GetAwaiter().GetResult()
    printfn "Stored a Thing through the F# Wolverine + CosmosDB handler (with FluentValidation)."

    host.StopAsync().GetAwaiter().GetResult()
    0
