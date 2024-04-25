using IntegrationTests;
using JasperFx.Core;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RDBMS;
using Xunit.Abstractions;

namespace MartenTests.MultiTenancy;

public class dynamically_spin_up_new_durability_agents_for_new_tenant_databases_with_multiple_hosts : IAsyncLifetime
{
    private readonly ITestOutputHelper _testOutputHelper;
    private IHost _host1;
    private IHost _host2;
    private IDocumentStore theStore;
    private string tenant1ConnectionString;
    private string tenant2ConnectionString;
    private string tenant3ConnectionString;
    private string tenant4ConnectionString;

    public dynamically_spin_up_new_durability_agents_for_new_tenant_databases_with_multiple_hosts(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
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

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await conn.DropSchemaAsync("tenants");


        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant2");
        tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant3");
        tenant4ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant4");

        await conn.CloseAsync();

        // Setting up Hosts with Multi-tenancy
        _host1 = await CreateHostAsync();
        _host2 = await CreateHostAsync();

        theStore = _host1.Services.GetRequiredService<IDocumentStore>();

        var tenancy = (MasterTableTenancy)theStore.Options.Tenancy;
        await tenancy.ClearAllDatabaseRecordsAsync();

        async Task<IHost> CreateHostAsync()
        {
            return await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // This is too extreme for real usage, but helps tests to run faster
                    opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
                    opts.Durability.HealthCheckPollingTime = 1.Seconds();

                    opts.Services.AddMarten(o =>
                        {
                            // This is a new strategy for configuring tenant databases with Marten
                            // In this usage, Marten is tracking the tenant databases in a single table in the "master"
                            // database by tenant
                            o.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, "tenants");
                        })
                        .IntegrateWithWolverine("mt", masterDatabaseConnectionString: Servers.PostgresConnectionString)

                        // All detected changes will be applied to all
                        // the configured tenant databases on startup
                        .ApplyAllDatabaseChangesOnStartup();
                })
                .StartAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _host1.StopAsync();
        _host1.Dispose();
        await _host2.StopAsync();
        _host2.Dispose();
    }

    [Fact]
    public async Task add_databases_and_see_durability_agents_start()
    {
        await _host1.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = DurabilityAgent.AgentScheme;
            w.CountTotal = true;

            // 1 for the master
            w.ExpectRunningAgents(_host1, 1);
            w.ExpectRunningAgents(_host2, 0);
        }, 10.Seconds());


        var tenancy = (MasterTableTenancy)theStore.Options.Tenancy;
        await tenancy.AddDatabaseRecordAsync("tenant1", tenant1ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant2", tenant2ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant3", tenant3ConnectionString);

        await _host1.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = DurabilityAgent.AgentScheme;
            w.CountTotal = true;

            // 1 for the master, 3 for the tenant databases
            w.ExpectRunningAgents(_host1, 4);
            w.ExpectRunningAgents(_host2, 0);
        }, 1.Minutes());
        
    }
}