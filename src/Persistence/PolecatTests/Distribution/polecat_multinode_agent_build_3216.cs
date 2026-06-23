using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using PolecatTests.Distribution.TripDomain;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace PolecatTests.Distribution;

// Regression for GH-3216: the residual multi-node flakiness after #3133/#3168. The leader evaluates
// assignments — calling SupportedAgentsAsync, which is what populated EventStoreAgents._shardNames — and
// then tells *another* node to start the agent. That assigned node builds the agent on its OWN family
// instance, whose _shardNames was never populated, so BuildAgentAsync used to throw "Unable to find a
// shard with path ..." and the agent silently failed to start (non-deterministically, depending on which
// node won the assignment / survived a failover). Polecat exposed this more than Marten purely because
// its SQL Server descriptor/daemon timing widened the window.
//
// This reproduces the cross-node ordering deterministically with a second, cold family instance over the
// same store, with no flaky 3-node cluster timing.
public class polecat_multinode_agent_build_3216 : IAsyncLifetime
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
                        m.DatabaseSchemaName = "polecat_3216";

                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine(o => o.UseWolverineManagedEventSubscriptionDistribution = true)
                    .ApplyAllDatabaseChangesOnStartup();

                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        _host.GetRuntime().Agents.DisableHealthChecks();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task assigned_node_can_build_agent_without_having_enumerated_its_own_shards_first()
    {
        // The "leader" enumerates the known agents (this is the only thing that populated _shardNames).
        var leaderFamily = _host.Services.GetRequiredService<EventSubscriptionAgentFamily>();
        var uris = await leaderFamily.AllKnownAgentsAsync();
        uris.ShouldNotBeEmpty();
        var assignedUri = uris[0];

        // Simulate the *assigned* node: a brand-new family over the same store whose shard cache has never
        // been populated (it never ran SupportedAgentsAsync / AllKnownAgentsAsync).
        var store = _host.Services.GetRequiredService<IEventStore>();
        var coldFamily =
            new EventSubscriptionAgentFamily([store], Array.Empty<IObserver<ShardState>>());

        // Pre-3216 this threw ArgumentOutOfRangeException("Unable to find a shard with path ...").
        var agent = await coldFamily.BuildAgentAsync(assignedUri, _host.GetRuntime());

        agent.ShouldNotBeNull();
        agent.Uri.ShouldBe(assignedUri);

        await coldFamily.DisposeAsync();
    }

    [Fact]
    public async Task every_known_agent_is_buildable_from_a_cold_family()
    {
        var leaderFamily = _host.Services.GetRequiredService<EventSubscriptionAgentFamily>();
        var uris = await leaderFamily.AllKnownAgentsAsync();
        uris.Count.ShouldBe(3); // Trip, Day, Distance async projections

        var store = _host.Services.GetRequiredService<IEventStore>();

        foreach (var uri in uris)
        {
            // A fresh cold family per URI — the strongest version of "this node never enumerated".
            var coldFamily =
                new EventSubscriptionAgentFamily([store], Array.Empty<IObserver<ShardState>>());

            var agent = await coldFamily.BuildAgentAsync(uri, _host.GetRuntime());
            agent.Uri.ShouldBe(uri);

            await coldFamily.DisposeAsync();
        }
    }
}
