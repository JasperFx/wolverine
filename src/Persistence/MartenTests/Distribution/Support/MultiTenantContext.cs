using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Marten;
using Marten.Events.Projections;
using Marten.Storage;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten.Distribution;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace MartenTests.Distribution.Support;

public class MultiTenantContext : IAsyncLifetime
{
    private readonly List<IHost> _hosts = new();
    private readonly ITestOutputHelper _output;

    private bool _hasStarted;
    private DocumentStore _tenancyStore;
    protected MasterTableTenancy tenancy;
    protected string tenant1ConnectionString;
    protected string tenant2ConnectionString;
    protected string tenant3ConnectionString;
    protected string tenant4ConnectionString;
    protected IHost theOriginalHost;
    internal ProjectionAgents theProjectionAgents;

    public MultiTenantContext(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await conn.DropSchemaAsync("tenants");


        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant2");
        tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant3");
        tenant4ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant4");

        await dropSchema();

        _tenancyStore = DocumentStore.For(opts =>
        {
            opts.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, "tenants");
            opts.DatabaseSchemaName = "multiple";
        });

        await _tenancyStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        tenancy = (MasterTableTenancy)_tenancyStore.Options.Tenancy;
        await tenancy.ClearAllDatabaseRecordsAsync();

        theOriginalHost = await startHostAsync();

        theProjectionAgents = theOriginalHost.Services.GetServices<IAgentFamily>().OfType<ProjectionAgents>().Single();
    }

    public async Task DisposeAsync()
    {
        await _tenancyStore.DisposeAsync();
        _hosts.Reverse();
        foreach (var host in _hosts.ToArray()) await shutdownHostAsync(host);
    }

    private async Task<string> CreateDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }

        builder.Database = databaseName;

        return builder.ConnectionString;
    }

    private static async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("csp");
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

                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithCritterStackPro(masterDatabaseConnectionString: Servers.PostgresConnectionString);

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        new XUnitEventObserver(host, _output);

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
                        m.DisableNpgsqlLogging = true;

                        m.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, "tenants");
                        m.DatabaseSchemaName = "csp";

                        m.Projections.Add<Trip2Projection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<StartingProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<EndingProjection>(ProjectionLifecycle.Async);
                    })

                    // TODO --derive this from Marten Tenancy
                    .IntegrateWithCritterStackPro(masterDatabaseConnectionString: Servers.PostgresConnectionString);

                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        new XUnitEventObserver(host, _output);

        _hosts.Add(host);

        return host;
    }

    private async Task shutdownHostAsync(IHost host)
    {
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        host.Dispose();
        _hosts.Remove(host);
    }

    protected Uri[] runningSubscriptions(IHost host)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        return runtime.Agents.AllRunningAgentUris().Where(x => x.Scheme == ProjectionAgents.SchemeName).ToArray();
    }
}