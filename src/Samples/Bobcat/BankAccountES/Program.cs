using BankAccountES;
using Fisher;
using JasperFx;
using Wolverine.CritterWatch;
using Wolverine.RabbitMQ;
using JasperFx.Events.EventModeling;
using JasperFx.Events.Projections;
using Marten;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

// The event store is a deployment choice, not a domain one. Every handler, projection and read
// endpoint in this sample is written against the store-agnostic vocabulary — Wolverine's
// [DeciderFunction] / [Entity] / Storage.StartStream() and JasperFx.Events' IEventStoreOperations /
// IDocumentReadOperations — so the only file that names a store is this one. `EventStore=Fisher` in
// configuration (or the environment) runs the same application on an embedded SQLite file with no
// Postgres at all; the default is Marten. See README.md and CLAUDE.md, "Bobcat.CritterStack is
// store-agnostic" (issue #103).
var eventStore = builder.Configuration["EventStore"] ?? "Marten";

switch (eventStore.ToLowerInvariant())
{
    case "marten":
        builder.Services.AddMarten(opts =>
            {
                var connectionString = builder.Configuration.GetConnectionString("Marten")
                    ?? "Host=localhost;Port=5433;Database=bank_account;Username=postgres;Password=postgres";

                opts.Connection(connectionString);
                opts.DatabaseSchemaName = "bank";

                // Inline snapshots — aggregates and the transaction-history read model are always up to date.
                opts.Projections.Snapshot<Account>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<Client>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<AccountTransactions>(SnapshotLifecycle.Inline);
            })
            .IntegrateWithWolverine()
            .UseLightweightSessions();
        break;

    case "fisher":
        builder.Services.AddFisher(opts =>
            {
                // An embedded SQLite file. Relative paths resolve against the working directory, so the
                // default lands next to the host's appsettings.json; a spec run points it somewhere else.
                var connectionString = builder.Configuration.GetConnectionString("Fisher")
                    ?? "Data Source=bank_account.db";

                opts.Connection(connectionString);

                // Identical registrations — Snapshot<T> is the one shape of projection every store spells
                // the same way, because a self-aggregating document needs no store-specific base class.
                opts.Projections.Snapshot<Account>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<Client>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<AccountTransactions>(SnapshotLifecycle.Inline);
            })
            // Not optional on a fresh database. Fisher builds its schema lazily, and the first append
            // on an empty file reaches AppendPlanner.ReadCurrentVersionAsync before anything has created
            // fi_streams / fi_events — "no such table" on the first three scenarios, then a pass once
            // something else has built the tables. Marten creates them on the way in; Fisher wants the
            // schema applied at startup. See docs/sample-wiring.md footgun 13.
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
        break;

    default:
        throw new InvalidOperationException(
            $"Unknown EventStore '{eventStore}'. This sample runs on 'Marten' (Postgres, the default) or 'Fisher' (SQLite).");
}

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.UseFluentValidation();
    opts.ServiceName = "BankAccount";

    // bobcat#172, the fourth rung: with CritterWatch:Uri configured, this sample becomes a
    // MONITORED service — its event-model manifest (chains + overlay) pushes to the console on
    // its own hash-gated message, and every event append inside a handler or endpoint is
    // observed and attributed. Unconfigured (specs, CI, a bare dotnet run), nothing changes.
    if (builder.Configuration["CritterWatch:Uri"] is { Length: > 0 } critterWatchUri)
    {
        opts.UseRabbitMq(new Uri(builder.Configuration["CritterWatch:Broker"] ?? "amqp://localhost:5673"))
            .AutoProvision();
        opts.AddCritterWatchMonitoring(
            critterWatchUri: new Uri(critterWatchUri),
            systemControlUri: new Uri(builder.Configuration["CritterWatch:ControlUri"] ?? "rabbitmq://queue/bank-account-control"));
    }
});

// The Event Model OVERLAY (jasperfx#687, decision D5): naming, grouping and trigger labels only —
// never a factual role. Roles (commands, emitted events, aggregates, read models) are DERIVED from
// the Wolverine chains and DECLARED by the Bobcat specs; the overlay contributes exactly the things
// no code can express. The model name matches opts.ServiceName above because upstream,
// EventModelDiscovery.Assemble folds descriptors together by model name — a mismatch here and the
// overlay floats off as its own model instead of merging (bobcat#172).
builder.Services.AddEventModel("BankAccount", model =>
{
    model.InDomain("Banking");

    // Human trigger labels on every command slice. wolverine#4181 (fixed in 6.31.0: the
    // HTTP-derived source stopped claiming TriggerLabel with the verb+route, which had made
    // every label here lose the merge) — the verb+route stays available on TriggerOrigin, and
    // these Declared labels now win because nothing else claims the role.
    model.Slice("EnrollClient").TriggeredBy("New customer walks in");
    model.Slice("UpdateClient").TriggeredBy("Customer corrects their details");
    model.Slice("OpenAccount").TriggeredBy("Enrolled client asks for an account");
    model.Slice("DepositFunds").TriggeredBy("Customer at the teller");
    model.Slice("WithdrawFunds").TriggeredBy("Customer at the ATM");
    model.Slice("FreezeAccount").TriggeredBy("The fraud desk");
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapWolverineEndpoints(opts =>
{
    opts.UseFluentValidationProblemDetailMiddleware();
});

// The fraud desk's HTTP door: forwards straight to the FreezeAccount message handler, which
// stays the only decider. Wolverine folds this route and the handler into ONE event-model
// slice, because both key on the FreezeAccount type.
app.MapPostToWolverine<FreezeAccount>("/api/accounts/freeze");

// RunJasperFxCommands rather than RunAsync so `dotnet run -- event-model --url …` works against
// this host — the export command builds the host without starting it and PUTs the assembled,
// provenance-stamped descriptor to a Bobcat console (wolverine#3990, bobcat#171). A bare
// `dotnet run` still runs the app exactly as before.
return await app.RunJasperFxCommands(args);
