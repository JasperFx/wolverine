module WolverineFSharpSample.Program

open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open JasperFx.Resources
open Wolverine
open Wolverine.EntityFrameworkCore
open Wolverine.Postgresql
open WolverineFSharpSample

// The same Postgres instance the rest of the Wolverine test suite uses (docker-compose, :5433).
// Wolverine's EF Core outbox requires a durable message store, so the sample is not infra-free; the
// *static* F# story (no DB, no host) is what the compile-gate test proves.
// A dedicated database so EF Core's EnsureCreated() provisions the Item table on a fresh DB (it
// no-ops against the shared `postgres` DB, which already has tables).
[<Literal>]
let connectionString =
    "Host=localhost;Port=5433;Database=wolverine_fsharp_sample;Username=postgres;password=postgres"

[<EntryPoint>]
let main args =
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

    // Create the database + the DbContext's Item table BEFORE starting the host, so Wolverine's
    // message-store resource setup (on Start) finds an existing database to provision into.
    (use scope = host.Services.CreateScope()
     scope.ServiceProvider.GetRequiredService<ItemsDbContext>().Database.EnsureCreated() |> ignore)

    host.Start()

    // Demonstrate the F# EF Core handler end-to-end (dynamic codegen).
    let bus = host.Services.GetRequiredService<IMessageBus>()
    bus.InvokeAsync({ Name = "Sample" }).GetAwaiter().GetResult()
    printfn "Created an Item through the F# Wolverine + EF Core handler."

    host.StopAsync().GetAwaiter().GetResult()
    0
