using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Marten;
using Marten.Events.Projections;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Distribution;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace MartenTests.Distribution.Support;

public abstract class SingleTenantContext : IAsyncLifetime
{
    private readonly List<IHost> _hosts = new();
    private readonly ITestOutputHelper _output;
    protected IHost theOriginalHost;
    internal ProjectionAgents theProjectionAgents;

    public SingleTenantContext(ITestOutputHelper output)
    {
        _output = output;
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
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

                #region sample_opt_into_wolverine_managed_subscription_distribution

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "csp";

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

                #endregion

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        new XUnitEventObserver(host, _output);

        _hosts.Add(host);

        theProjectionAgents ??= host.Services.GetServices<IAgentFamily>().OfType<ProjectionAgents>().Single();

        return host;
    }

    protected async Task<IHost> startGreenHostAsync()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "csp";
                        m.DisableNpgsqlLogging = true;

                        m.Projections.Add<Trip2Projection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<StartingProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<EndingProjection>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine(m => m.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        new XUnitEventObserver(host, _output);

        _hosts.Add(host);

        theProjectionAgents ??= host.Services.GetServices<IAgentFamily>().OfType<ProjectionAgents>().Single();

        return host;
    }

    private static async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await new Table(new DbObjectName("csp", "wolverine_node_records")).DropAsync(conn);
        await new Table(new DbObjectName("csp", "wolverine_nodes")).DropAsync(conn);
        await new Table(new DbObjectName("csp", "wolverine_node_assignments")).DropAsync(conn);
        await conn.CloseAsync();
    }
}