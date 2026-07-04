using IntegrationTests;
using JasperFx.CodeGeneration;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Xunit;

namespace MartenTests.Distribution;

/// <summary>
/// JasperFx/marten#4806: the database-affine agent-assignment settings can be configured on
/// IntegrateWithWolverine (next to UseWolverineManagedEventSubscriptionDistribution), and they must flow
/// through to the Marten event store — which is where Wolverine's distribution reads them via
/// IEventStore.GroupAgentAssignmentsByDatabase / MaxNodesPerDatabaseForAgents. This guards the bridge
/// (MartenOverrides resolving the MartenIntegration and applying the settings) against a DI-resolution
/// regression that would silently leave the app on the default even distribution.
/// </summary>
public class integrate_with_wolverine_affine_settings
{
    private static async Task<IHost> StartHostAsync(Action<MartenIntegration> configure, string schema)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = schema;
                    })
                    .IntegrateWithWolverine(configure);

                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();
    }

    [Fact]
    public async Task affine_settings_flow_to_the_event_store()
    {
        using var host = await StartHostAsync(m =>
        {
            m.UseWolverineManagedEventSubscriptionDistribution = true;
            m.UseDatabaseAffineAgentAssignment = true;
            m.DatabaseAffineAgentFanout = 3;
        }, "affine_on");

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        store.Options.Events.UseDatabaseAffineAgentAssignment.ShouldBeTrue();
        store.Options.Events.DatabaseAffineAgentFanout.ShouldBe(3);
    }

    [Fact]
    public async Task defaults_are_unchanged_when_not_configured()
    {
        using var host = await StartHostAsync(
            m => m.UseWolverineManagedEventSubscriptionDistribution = true, "affine_off");

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        store.Options.Events.UseDatabaseAffineAgentAssignment.ShouldBeFalse();
        store.Options.Events.DatabaseAffineAgentFanout.ShouldBe(1);
    }
}
