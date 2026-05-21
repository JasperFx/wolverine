using IntegrationTests;
using JasperFx.Core;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Collections.Concurrent;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Distribution;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace MartenTests.Distribution.Support;

public abstract class MultiTenantContext(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly ConcurrentBag<IHost> _hosts = [];
    private readonly ConcurrentBag<XUnitEventObserver> _observers = [];
    private readonly ConcurrentDictionary<string, string> _tenantConnectionStrings = [];
    private DocumentStore _tenancyStore = null!;
    protected MasterTableTenancy _tenancy = null!;
    protected IHost theOriginalHost = null!;

    public async Task InitializeAsync()
    {
        await DropSchemasAsync("tenants", "multiple", "csp");
        await Task.WhenAll([
            CreateDatabaseIfNotExists("tenant1"),
            CreateDatabaseIfNotExists("tenant2"),
            CreateDatabaseIfNotExists("tenant3"),
        ]);

        _tenancyStore = DocumentStore.For(opts =>
        {
            opts.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, "tenants");
            opts.DatabaseSchemaName = "multiple";
        });

        await _tenancyStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        _tenancy = (MasterTableTenancy)_tenancyStore.Options.Tenancy;
        await _tenancy.ClearAllDatabaseRecordsAsync();

        theOriginalHost = await startHostAsync();
    }

    public async Task DisposeAsync()
    {
        _observers.Each(x => x.Dispose());
        await _tenancyStore.DisposeAsync();
        await Task.WhenAll(_hosts.Select(ShutdownHostAsync));
        _hosts.Clear();
    }

    private async Task CreateDatabaseIfNotExists(string databaseName)
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);

        builder.Database = databaseName;

        _tenantConnectionStrings.TryAdd(databaseName, builder.ConnectionString);
    }

    private static async Task DropSchemasAsync(params string[] names)
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        foreach (var name in names)
            await conn.DropSchemaAsync(name);
        await conn.CloseAsync();
    }

    protected async Task<IHost> startHostAsync()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.CheckAssignmentPeriod = 5.Seconds();
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.FirstHealthCheckExecution = 5.Seconds();

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;

                        m.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, "tenants");
                        m.DatabaseSchemaName = "csp";

                        SetupProjections(m);
                    })
                    .IntegrateWithWolverine(m =>
                    {
                        m.MainDatabaseConnectionString = Servers.PostgresConnectionString;
                        m.UseWolverineManagedEventSubscriptionDistribution = true;
                    });
                opts.Discovery.DisableConventionalDiscovery();
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(output));
            }).StartAsync();

        _observers.Add(new XUnitEventObserver(host, output));
        _hosts.Add(host);

        return host;
    }

    protected abstract void SetupProjections(StoreOptions storeOptions);

    private async Task ShutdownHostAsync(IHost host)
    {
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        host.Dispose();
    }

    protected static async Task<string[]> GetAgentUrisAsync(IHost host)
    {
        var agentsFamily = host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();
        var agents = await agentsFamily.AllKnownAgentsAsync();
        return [.. agents.Select(x => x.AbsoluteUri)];
    }

    protected async Task AddTenantsAsync(params string[] tenants)
    {
        var records = tenants.Select(x =>
            _tenancy.AddDatabaseRecordAsync(x, _tenantConnectionStrings[x]));
        await Task.WhenAll(records);
    }
}