using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using PolecatTests.Distribution.TripDomain;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Runtime.Agents;

namespace PolecatTests.Distribution;

// Regression for #3133: under Wolverine-managed event-subscription distribution a Polecat host must
//  (Gap 1) register IEventSubscriptionAgentFamily for shard->agent-URI resolution, and
//  (Gap 2) register the store-agnostic IEventStore so the agent family enumerates the async
//          projection shards as distributed event-subscription agents (parity with Marten).
public class polecat_managed_event_subscription_distribution : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "polecat_distribution";

                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine(o => o.UseWolverineManagedEventSubscriptionDistribution = true)
                    .ApplyAllDatabaseChangesOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void registers_event_store_and_subscription_agent_family()
    {
        // Gap 2: the store-agnostic IEventStore the agent family resolves
        _host.Services.GetServices<IEventStore>().ShouldNotBeEmpty();

        // Gap 1: IEventSubscriptionAgentFamily so tooling can map a shard identity to an agent URI
        _host.Services.GetServices<IEventSubscriptionAgentFamily>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task projection_shards_surface_as_distributed_agents()
    {
        var family = _host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();

        var uris = await family.AllKnownAgentsAsync();
        var paths = uris.Select(u => u.AbsolutePath.TrimEnd('/')).ToArray();

        uris.ShouldAllBe(u => u.Scheme == EventSubscriptionAgentFamily.SchemeName);
        paths.ShouldContain(p => p.EndsWith("/trip/all"));
        paths.ShouldContain(p => p.EndsWith("/day/all"));
        paths.ShouldContain(p => p.EndsWith("/distance/all"));
    }

    [Fact]
    public async Task find_agent_uri_resolves_a_registered_shard()
    {
        var family = _host.Services.GetServices<IEventSubscriptionAgentFamily>().First();

        var uri = await family.FindAgentUriAsync("Trip:All", null);

        uri.ShouldNotBeNull();
        uri!.AbsolutePath.TrimEnd('/').ShouldEndWith("/trip/all");
    }
}
