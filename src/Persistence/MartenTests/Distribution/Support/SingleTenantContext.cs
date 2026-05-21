using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Marten;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Collections.Concurrent;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Distribution;
using Wolverine.MessagePack;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace MartenTests.Distribution.Support;

public abstract class SingleTenantContext(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly ConcurrentBag<IHost> _hosts = [];
    private readonly ConcurrentBag<XUnitEventObserver> _observers = [];
    protected IHost theOriginalHost = null!;

    public async Task InitializeAsync()
    {
        await DropSchemasAsync("csp");

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
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();
                
                opts.UseMessagePackSerialization();

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

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(output));
                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        _observers.Add(new XUnitEventObserver(host, output));
        _hosts.Add(host);

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

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(output));
                
                opts.UseMessagePackSerialization();
            }).StartAsync();

        _observers.Add(new XUnitEventObserver(host, output));
        _hosts.Add(host);

        return host;
    }

    private static async Task DropSchemasAsync(params string[] names)
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        foreach (var name in names)
            await conn.DropSchemaAsync(name);
        await conn.CloseAsync();
    }

    protected static async Task<string[]> GetAgentUrisAsync(IHost host)
    {
        var agentsFamily = host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();
        var agents = await agentsFamily.AllKnownAgentsAsync();
        return [.. agents.Select(x => x.AbsoluteUri)];
    }
}