using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Daemon.Coordination;
using MartenTests.Distribution.Support;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using System.Collections.Concurrent;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Distribution;
using Wolverine.MessagePack;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Wolverine.Transports.SharedMemory;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

public class with_ancillary_stores(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly ConcurrentBag<IHost> _hosts = [];
    private readonly ConcurrentBag<XUnitEventObserver> _observers = [];
    protected IHost theOriginalHost = null!;

    public async Task InitializeAsync()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("csp2");
        await conn.DropSchemaAsync("csp3");
        await conn.CloseAsync();

        theOriginalHost = await startHostAsync();
    }

    public async Task DisposeAsync()
    {
        _observers.Each(x => x.Dispose());
        await Task.WhenAll(_hosts.Select(ShutdownHostAsync));
        _hosts.Clear();
    }

    private async Task ShutdownHostAsync(IHost host)
    {
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        host.Dispose();
    }

    protected async Task<IHost> startHostAsync()
    {
        #region sample_using_distributed_projections_with_ancillary_stores
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

                opts.UseMessagePackSerialization();

                opts.UseSharedMemoryQueueing();

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "csp2";

                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine(m =>
                    {
                        // This makes Wolverine distribute the registered projections
                        // and event subscriptions evenly across a running application
                        // cluster
                        m.UseWolverineManagedEventSubscriptionDistribution = true;
                    });

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(output));

                opts.Services.AddMartenStore<ITripStore>(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "csp3";

                    m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                    m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                    m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine();

            }).StartAsync();

        #endregion

        _observers.Add(new XUnitEventObserver(host, output));
        _hosts.Add(host);

        return host;
    }

    [Fact]
    public void projection_coordinators_for_ancillary_stores_are_wolverine_versions()
    {
        theOriginalHost.Services.GetRequiredService<IProjectionCoordinator<ITripStore>>()
            .ShouldBeOfType<WolverineProjectionCoordinator<ITripStore>>();
    }

    [Fact]
    public async Task can_do_the_full_marten_reset_all_data_call()
    {
        // It's a smoke test to fix GH-1057
        await theOriginalHost.ResetAllMartenDataAsync();

        await theOriginalHost.ResetAllMartenDataAsync<ITripStore>();
    }

    [Fact]
    public async Task spread_out_over_multiple_hosts()
    {
        await theOriginalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());
        await AssertAgentUrisAsync(theOriginalHost);

        var extraHosts = await Task.WhenAll<IHost>([startHostAsync(), startHostAsync()]);

        // Now, let's check that the load is redistributed!
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 2);
            w.ExpectRunningAgents(extraHosts[0], 2);
            w.ExpectRunningAgents(extraHosts[1], 2);
        }, 30.Seconds());

        await AssertAgentUrisAsync(theOriginalHost);
        await AssertAgentUrisAsync(extraHosts[0]);
        await AssertAgentUrisAsync(extraHosts[1]);
    }

    private async static Task AssertAgentUrisAsync(IHost host)
    {
        var agentsFamily = host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();
        var agents = await agentsFamily.AllKnownAgentsAsync();
        var uris = agents.Select(x => x.AbsoluteUri);
        uris.ShouldBe([
            "event-subscriptions://marten/itripstore/localhost.postgres/day/all",
            "event-subscriptions://marten/itripstore/localhost.postgres/distance/all",
            "event-subscriptions://marten/itripstore/localhost.postgres/trip/all",
            "event-subscriptions://marten/main/localhost.postgres/day/all",
            "event-subscriptions://marten/main/localhost.postgres/distance/all",
            "event-subscriptions://marten/main/localhost.postgres/trip/all"
        ], ignoreOrder: true);
    }
}

public interface ITripStore : IDocumentStore;