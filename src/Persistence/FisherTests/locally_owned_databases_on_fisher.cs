using Fisher;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace FisherTests;

// Mirrors the Marten check in sharded_two_databases_affine_colocation: runtime.LocallyOwnedDatabases() is asked
// with the Fisher database itself, and the id it compares is the one the projection agent URIs carry
public class locally_owned_databases_on_fisher : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("locally_owned_databases");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Projections.Snapshot<ManagedTally>(SnapshotLifecycle.Async);
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task the_fisher_database_is_owned_by_its_id()
    {
        var runtime = _host.GetRuntime();
        var databases = await ((IEventStore)_host.Services.GetRequiredService<IDocumentStore>()).AllDatabases();
        var database = databases.ShouldHaveSingleItem().ShouldBeAssignableTo<IDatabase>()!;

        // The running projection agent's URI carries this exact database id
        await waitFor(() => runtime.Agents.AllLocallyOwnedDatabaseIds().Contains(database.Id),
            "the projection agent to start against the Fisher database");

        var owned = runtime.LocallyOwnedDatabases();
        owned.IsEvery.ShouldBeTrue();
        owned.Contains(database).ShouldBeTrue();
    }

    private static async Task waitFor(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for {what}");
    }
}
