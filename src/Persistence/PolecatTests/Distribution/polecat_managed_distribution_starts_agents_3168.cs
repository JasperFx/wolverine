using IntegrationTests;
using JasperFx.Core;
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
using Wolverine.Tracking;

namespace PolecatTests.Distribution;

// Regression for GH-3168: under UseWolverineManagedEventSubscriptionDistribution a Polecat host must not
// only SURFACE the async projection shards as agent URIs (polecat_managed_event_subscription_distribution)
// but actually START them on the node. Previously every start threw "Unknown event projection or
// subscription": the agent URI carries the store Type ("SqlServer") in its authority, which System.Uri
// lowercases, while the EventSubscriptionAgentFamily store map was keyed by the original-cased
// Identity.ToString() ("Polecat:SqlServer") — so the reverse lookup missed and no agent ran (node N: []).
// Marten dodged it only because its Type is already lowercase ("marten").
public class polecat_managed_distribution_starts_agents_3168 : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Speed up the leader-election + assignment loops (mirrors the Marten harness).
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "polecat_3168";

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
    public async Task the_three_async_projection_agents_actually_run_on_one_node()
    {
        await _host.WaitUntilAssumesLeadershipAsync(5.Seconds());

        // All three async projection shards must actually be running on the single node (GH-3168).
        await _host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(_host, 3);
        }, 30.Seconds());

        var running = _host.RunningAgents()
            .Where(x => x.Scheme == EventSubscriptionAgentFamily.SchemeName)
            .ToArray();

        running.Length.ShouldBe(3);
    }
}
