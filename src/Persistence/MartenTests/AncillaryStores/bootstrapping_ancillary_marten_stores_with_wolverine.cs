using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Oakton.Resources;
using Shouldly;
using TestingSupport;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;

namespace MartenTests.AncillaryStores;

/*
 * TODO
 * Test w/o basic AddMarten registration
 *
 *
 * 
 */

public class bootstrapping_ancillary_marten_stores_with_wolverine : IAsyncLifetime
{
    private IHost theHost;
    private string tenant1ConnectionString;
    private string tenant2ConnectionString;
    private string tenant3ConnectionString;

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
        await conn.DropSchemaAsync("players");

        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant2");
        tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant3");
        
        await dropSchemaOnDatabase(tenant1ConnectionString, "things");
        await dropSchemaOnDatabase(tenant2ConnectionString, "things");
        await dropSchemaOnDatabase(tenant3ConnectionString, "things");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();


                opts.Services.AddMartenStore<IPlayerStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "players";
                }).IntegrateWithWolverine();
                
                opts.Services.AddMartenStore<IThingStore>(m =>
                {
                    m.MultiTenantedDatabases(tenancy =>
                    {
                        tenancy.AddSingleTenantDatabase(tenant1ConnectionString, "tenant1");
                        tenancy.AddSingleTenantDatabase(tenant2ConnectionString, "tenant2");
                        tenancy.AddSingleTenantDatabase(tenant3ConnectionString, "tenant3");
                    });
                    m.DatabaseSchemaName = "things";
                }).IntegrateWithWolverine(masterDatabaseConnectionString:Servers.PostgresConnectionString);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    private async Task dropSchemaOnDatabase(string connectionString, string schemaName)
    {
        using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(schemaName);
        await conn.CloseAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public void registers_the_single_tenant_ancillary_store()
    {
        theHost.DocumentStore<IPlayerStore>().ShouldNotBeNull();
        var ancillaries = theHost.Services.GetServices<IAncillaryMessageStore>();
        ancillaries.OfType<PostgresqlMessageStore<IPlayerStore>>().Any().ShouldBeTrue();
    }

    [Fact]
    public void registers_the_multiple_tenant_ancillary_store()
    {
        theHost.DocumentStore<IThingStore>().ShouldNotBeNull();
        var ancillaries = theHost.Services.GetServices<IAncillaryMessageStore>();
        ancillaries.OfType<MultiTenantedMessageDatabase<IThingStore>>().Any()
            .ShouldBeTrue();
            
    }

    [Fact]
    public void registers_the_outbox_factory_for_the_store()
    {
        theHost.Services.GetRequiredService<OutboxedSessionFactory<IPlayerStore>>()
            .ShouldNotBeNull();
        
        theHost.Services.GetRequiredService<OutboxedSessionFactory<IThingStore>>()
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task can_discover_all_the_databases()
    {
        var databaseSources = theHost.Services.GetServices<IDatabaseSource>();
        var source = databaseSources
            .OfType<MartenMessageDatabaseDiscovery>()
            .Single();

        var databases = await source.BuildDatabases();
        
        databases.OfType<PostgresqlMessageStore<IPlayerStore>>().Count().ShouldBe(1);
        
        // 3 tenant databases + 1 master database
        databases.Where(x => x is not PostgresqlMessageStore<IPlayerStore>).OfType<PostgresqlMessageStore>().Select(x => x.Name)
            .OrderBy(x => x)
            .ShouldHaveTheSameElementsAs("default", "tenant1", "tenant2", "tenant3");
    }

    [Fact]
    public async Task builds_out_envelope_schema()
    {
        await assertTablesExist(Servers.PostgresConnectionString, "public");
        await assertTablesExist(Servers.PostgresConnectionString, "players");
        await assertTablesExist(Servers.PostgresConnectionString, "things"); // master db
        await assertTablesExist(tenant1ConnectionString, "things");
        await assertTablesExist(tenant2ConnectionString, "things");
        await assertTablesExist(tenant3ConnectionString, "things");
    }

    private async Task assertTablesExist(string connectionString, string schemaName)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var tables = await conn.ExistingTablesAsync(schemas: [schemaName]);
        tables.ShouldContain(new DbObjectName(schemaName, DatabaseConstants.IncomingTable));
        tables.ShouldContain(new DbObjectName(schemaName, DatabaseConstants.OutgoingTable));

        await conn.CloseAsync();
    }
    
    
}

public interface IPlayerStore : IDocumentStore;

public class Player
{
    public string Id { get; set; }
}

public interface IThingStore : IDocumentStore;

public class Thing
{
    public string Id { get; set; }
}