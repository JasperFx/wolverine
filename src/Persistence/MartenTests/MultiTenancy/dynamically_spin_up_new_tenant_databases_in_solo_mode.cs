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
using Wolverine.Persistence;
using Wolverine.RDBMS;

namespace MartenTests.MultiTenancy;

public class dynamically_spin_up_new_tenant_databases_in_solo_mode : IAsyncLifetime
{
    private IHost _host;
    private IDocumentStore theStore;
    private string tenant1ConnectionString;
    private string tenant2ConnectionString;
    private string tenant3ConnectionString;
    private string tenant4ConnectionString;

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

        // Setting up a Host with Multi-tenancy
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // This is too extreme for real usage, but helps tests to run faster
                opts.Durability.NodeReassignmentPollingTime = 1.Seconds();

                opts.Services.AddMarten(o =>
                    {
                        // This is a new strategy for configuring tenant databases with Marten
                        // In this usage, Marten is tracking the tenant databases in a single table in the "master"
                        // database by tenant
                        o.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, "tenants");
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "mt")

                    // All detected changes will be applied to all
                    // the configured tenant databases on startup
                    .ApplyAllDatabaseChangesOnStartup();
            })
            .StartAsync();

        theStore = _host.Services.GetRequiredService<IDocumentStore>();

        var tenancy = (MasterTableTenancy)theStore.Options.Tenancy;
        await tenancy.ClearAllDatabaseRecordsAsync();
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

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task add_databases_in_solo_mode_and_see_durability_agents_start()
    {
        await _host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = PersistenceConstants.AgentScheme;

            // 1 for the master
            w.ExpectRunningAgents(_host, 1);
        }, 10.Seconds());


        var tenancy = (MasterTableTenancy)theStore.Options.Tenancy;
        await tenancy.AddDatabaseRecordAsync("tenant1", tenant1ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant2", tenant2ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant3", tenant3ConnectionString);

        await _host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = PersistenceConstants.AgentScheme;

            // 1 for the master, 3 for the tenant databases
            w.ExpectRunningAgents(_host, 4);
        }, 1.Minutes());
    }
}