using IntegrationTests;
using JasperFx.CodeGeneration;
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
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Distribution;
using Wolverine.MessagePack;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Wolverine.Transports.SharedMemory;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

public class with_ancillary_stores : IAsyncLifetime
{
    private readonly List<IHost> _hosts = new();
    private readonly ITestOutputHelper _output;
    protected IHost theOriginalHost;
    internal EventSubscriptionAgentFamily theProjectionAgents;

    public with_ancillary_stores(ITestOutputHelper output)
    {
        _output = output;
    }

    private static async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await new Table(new DbObjectName("csp2", "wolverine_node_records")).DropAsync(conn);
        await new Table(new DbObjectName("csp2", "wolverine_nodes")).DropAsync(conn);
        await new Table(new DbObjectName("csp2", "wolverine_node_assignments")).DropAsync(conn);
        await conn.CloseAsync();
    }
    
    public async Task InitializeAsync()
    {
        await dropSchema();

        theOriginalHost = await startHostAsync();
    }

    public async Task DisposeAsync()
    {
        _hosts.Reverse();
        foreach (var host in _hosts.ToArray())
        {
            await shutdownHostAsync(host);
        }
    }
    
    private async Task shutdownHostAsync(IHost host)
    {
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        host.Dispose();
        _hosts.Remove(host);
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
                
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                opts.Services.AddMartenStore<ITripStore>(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "csp3";

                    m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                    m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                    m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine();

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        #endregion

        new XUnitEventObserver(host, _output);

        _hosts.Add(host);

        theProjectionAgents ??= host.Services.GetServices<IAgentFamily>().OfType<EventSubscriptionAgentFamily>().Single();

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
    public async Task find_all_known_agents()
    {
        var uris = await theProjectionAgents.AllKnownAgentsAsync();
        
        uris.Count.ShouldBe(6);
        
        uris.OrderBy(x => x.ToString()).ShouldBe([
            new Uri("event-subscriptions://marten/itripstore/localhost.postgres/day/all"),
            new Uri("event-subscriptions://marten/itripstore/localhost.postgres/distance/all"),
            new Uri("event-subscriptions://marten/itripstore/localhost.postgres/trip/all"),
            new Uri("event-subscriptions://marten/main/localhost.postgres/day/all"),
            new Uri("event-subscriptions://marten/main/localhost.postgres/distance/all"),
            new Uri("event-subscriptions://marten/main/localhost.postgres/trip/all"),

        
        ]);

    }
    
    
    [Fact]
    public async Task spread_out_over_multiple_hosts()
    {
        await theOriginalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        
        // Now, let's check that the load is redistributed!
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 2);
            w.ExpectRunningAgents(host2, 2);
            w.ExpectRunningAgents(host3, 2);
        }, 30.Seconds());
    }

}

public interface ITripStore : IDocumentStore;