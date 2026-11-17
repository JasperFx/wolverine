using IntegrationTests;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using PolecatTests.Distribution.TripDomain;
using Shouldly;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace PolecatTests.Distribution;

// Mirrors the Marten check in sharded_two_databases_affine_colocation: runtime.LocallyOwnedDatabases() is asked
// with the Polecat database itself, and the id it compares is the one the projection agent URIs carry
public class locally_owned_databases_on_polecat : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "polecat_locally_owned";

                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine(o => o.UseWolverineManagedEventSubscriptionDistribution = true)
                    .ApplyAllDatabaseChangesOnStartup();

                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _host.GetRuntime().Agents.DisableHealthChecks();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_polecat_database_is_owned_by_its_id()
    {
        await _host.WaitUntilAssumesLeadershipAsync(5.Seconds());
        await _host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(_host, 1);
        }, 30.Seconds());

        var databases = await ((IEventStore)_host.Services.GetRequiredService<IDocumentStore>()).AllDatabases();
        var database = databases.ShouldHaveSingleItem().ShouldBeAssignableTo<IDatabase>()!;

        // The running projection agent's URI carries the server and database name Polecat describes
        var descriptor = database.Describe();
        var runtime = _host.GetRuntime();
        runtime.Agents.AllLocallyOwnedDatabaseIds()
            .ShouldBe([new DatabaseId(descriptor.ServerName, descriptor.DatabaseName)]);

        var owned = runtime.LocallyOwnedDatabases();
        owned.IsEvery.ShouldBeFalse();
        owned.Contains(database).ShouldBeTrue();
        owned.Contains(new DatabaseId("another-server", descriptor.DatabaseName)).ShouldBeFalse();
    }
}
