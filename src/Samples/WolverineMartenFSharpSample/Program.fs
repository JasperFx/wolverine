module WolverineMartenFSharpSample.Program

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Marten
open JasperFx.Resources
open Wolverine
open Wolverine.Marten
open WolverineMartenFSharpSample

// The same Postgres instance the rest of the Wolverine test suite uses (docker-compose, :5433).
// Marten creates its own schema objects on the fly, and IntegrateWithWolverine provisions the
// Wolverine message-store tables, so a dedicated schema keeps the sample's tables tidy in the
// shared `postgres` database (no separate database to create).
[<Literal>]
let connectionString =
    "Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres"

[<EntryPoint>]
let main args =
    let host =
        Host
            .CreateDefaultBuilder(args)
            .UseWolverine(fun opts ->
                // Marten as both the document store and (via IntegrateWithWolverine) the durable
                // message store backing Wolverine's transactional outbox.
                opts.Services
                    .AddMarten(fun (m: StoreOptions) ->
                        m.Connection(connectionString)
                        m.DatabaseSchemaName <- "wolverine_fsharp_marten")
                    .IntegrateWithWolverine()
                |> ignore

                opts.Policies.AutoApplyTransactions() |> ignore
                opts.Discovery.IncludeType<CreateProductHandler>() |> ignore

                // Core Wolverine dropped the in-box Roslyn compiler (GH-2876); enable it so this demo
                // runs via dynamic codegen. (The static F# story is proven by the compile-gate test.)
                opts.UseRuntimeCompilation() |> ignore)
            // Provision the Marten + Wolverine message-store schema on startup.
            .UseResourceSetupOnStartup()
            .Build()

    host.Start()

    // Demonstrate the F# Marten document handler end-to-end (dynamic codegen).
    let bus = host.Services.GetRequiredService<IMessageBus>()
    bus.InvokeAsync({ Name = "Sample" }).GetAwaiter().GetResult()
    printfn "Created a Product through the F# Wolverine + Marten handler."

    host.StopAsync().GetAwaiter().GetResult()
    0
