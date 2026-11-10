using IntegrationTests;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine.Marten;

namespace Wolverine.Bobcat.Tests.CatchingUpAViewsStore;

// AfterAsyncDaemonsCatchUpAsync() waits on the default store and on every store the specification touched, "a view was
// pinned to it" among them. On a WolverineSpec the view is a WolverineScenario, whose createView must count its store as
// touched just as CritterStackScenario's does, or the unpinned wait skips the store an In<T>() step wrote to.

public interface ILedgerStore : IDocumentStore;

public record LedgerOpened(string Owner);
public record LedgerCredited(decimal Amount);

public class Ledger
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = "";
    public decimal Credits { get; set; }

    public void Apply(LedgerOpened e) => Owner = e.Owner;
    public void Apply(LedgerCredited e) => Credits += e.Amount;
}

public sealed class LedgerHost : IAsyncLifetime
{
    private const string Schema = "bobcat_view_catch_up";

    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            foreach (var schema in new[] { Schema, Schema + "_ledger", Schema + "_ledger_wolverine" })
            {
                await using var drop = new NpgsqlCommand($"drop schema if exists {schema} cascade", conn);
                await drop.ExecuteNonQueryAsync();
            }
        }

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = Schema;
                        m.DisableNpgsqlLogging = true;
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .IntegrateWithWolverine();

                // The ledger is projected asynchronously, so a read straight after the wait sees it only if the wait
                // waited on this store
                opts.Services.AddMartenStore<ILedgerStore>(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = Schema + "_ledger";
                        m.DisableNpgsqlLogging = true;
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Projections.Snapshot<Ledger>(SnapshotLifecycle.Async);
                    })
                    .IntegrateWithWolverine(x => x.SchemaName = Schema + "_ledger_wolverine")
                    .AddAsyncDaemon(DaemonMode.Solo);
            })
            .Build();

        // The schema first, then the daemon: a daemon starting with the host would build the same tables alongside
        await Host.Services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await Host.Services.GetRequiredService<ILedgerStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await Host.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is null) return;
        await Host.StopAsync();
        Host.Dispose();
    }
}

public class catching_up_a_store_a_view_was_pinned_to(LedgerHost app) : WolverineSpec(app.Host), IClassFixture<LedgerHost>
{
    [Fact]
    public async Task a_given_through_a_view_makes_the_unpinned_catch_up_wait_on_the_views_store()
    {
        var ledger = Guid.CreateVersion7();
        await In<ILedgerStore>().GivenEvents<Ledger>(ledger, new LedgerOpened("Acme"), new LedgerCredited(5m));

        await AfterAsyncDaemonsCatchUpAsync();

        // Read straight from the store, with no wait of its own: its daemon has caught up
        await using var query = app.Host.Services.GetRequiredService<ILedgerStore>().QuerySession();
        var projected = await query.LoadAsync<Ledger>(ledger, TestContext.Current.CancellationToken);
        projected.ShouldNotBeNull().Credits.ShouldBe(5m);
    }
}
