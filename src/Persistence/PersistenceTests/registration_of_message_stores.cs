using IntegrationTests;
using JasperFx.Descriptors;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core.MultiTenancy;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace PersistenceTests;

/*
 * TODO -- register multiple postgresql outside of Marten
 * TODO -- register multiple Sql Server
 * TODO -- make all main, see assertion, postgresql
 * TODO -- make all main, see assertion, sql server
 * TODO -- make none, "Service" should be Nullo
 * TODO -- make one main, other ancillary, postgresql
 * TODO -- make one main, other ancillary, sql server
 * Move on to the durability agent family
 *
 *
 * 
 */

public class registration_of_message_stores(ITestOutputHelper Output) : IAsyncLifetime
{
    private IHost _host;
    private string connectionString1;
    private string connectionString2;
    private string connectionString3;
    private string connectionString4;

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
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        connectionString1 = await CreateDatabaseIfNotExists(conn, "database1");
        connectionString2 = await CreateDatabaseIfNotExists(conn, "database2");
        connectionString3 = await CreateDatabaseIfNotExists(conn, "database3");
        connectionString4 = await CreateDatabaseIfNotExists(conn, "database4");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
        }
    }


    private async Task<MessageStoreCollection> startHost(Action<WolverineOptions> configure)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(configure).ConfigureServices(services => services.AddResourceSetupOnStartup()).StartAsync();

        var collection = new MessageStoreCollection(_host.GetRuntime(), _host.Services.GetServices<IMessageStore>(), _host.Services.GetServices<IAncillaryMessageStore>());

        await collection.InitializeAsync();

        return collection;
    }

    [Fact]
    public async Task for_single_database()
    {
        // Not much to it
        var collection = await startHost(opts =>
        {
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
        });

        var expected = new Uri("wolverinedb://postgresql/localhost/postgres/wolverine");
        (await collection.FindAllAsync()).Single().Uri.ShouldBe(expected);

        (await collection.FindDatabasesAsync([expected])).Single().Uri.ShouldBe(expected);
        
        (await collection.FindDatabaseAsync(expected)).Uri.ShouldBe(expected);

        collection.Cardinality().ShouldBe(DatabaseCardinality.Single);
    }

    [Fact]
    public async Task for_several_ancillary_marten_databases()
    {
        var collection = await startHost(opts =>
        {
            opts.Durability.MessageStorageSchemaName = "wolverine";
            
            opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
            }).IntegrateWithWolverine();

            opts.Services.AddMartenStore<IFirstStore>(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "first";
            }).IntegrateWithWolverine();

            opts.Services.AddMartenStore<ISecondStore>(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "second";
            }).IntegrateWithWolverine();
        });
        
        // STILL only one expected message store here
        var expected = new Uri("wolverinedb://postgresql/localhost/postgres/wolverine");
        var services = await collection.FindAllAsync();

        services.Single().Uri.ShouldBe(expected);

        (await collection.FindDatabasesAsync([expected])).Single().Uri.ShouldBe(expected);
        
        (await collection.FindDatabaseAsync(expected)).Uri.ShouldBe(expected);
        
        collection.Cardinality().ShouldBe(DatabaseCardinality.Single);
    }

    [Fact]
    public async Task ancillary_marten_databases_using_different_databases()
    {
        var collection = await startHost(opts =>
        {
            opts.Durability.MessageStorageSchemaName = "wolverine";
            
            opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
            }).IntegrateWithWolverine();

            opts.Services.AddMartenStore<IFirstStore>(m =>
            {
                m.Connection(connectionString1);
                m.DatabaseSchemaName = "first";
            }).IntegrateWithWolverine();

            opts.Services.AddMartenStore<ISecondStore>(m =>
            {
                m.Connection(connectionString2);
                m.DatabaseSchemaName = "second";
            }).IntegrateWithWolverine();
        });
        
        // STILL only one expected message store here
        var main = new Uri("wolverinedb://postgresql/localhost/postgres/wolverine");
        var first = new Uri("wolverinedb://postgresql/localhost/database1/wolverine");
        var second = new Uri("wolverinedb://postgresql/localhost/database2/wolverine");
        
        var services = await collection.FindAllAsync();
        
        services.Select(x => x.Uri).OrderBy(x => x.ToString())
            .ShouldBe([first, second, main]);

        

        (await collection.FindDatabasesAsync([first, main])).Select(x => x.Uri).ShouldBe([first, main]);
        
        (await collection.FindDatabaseAsync(second)).Uri.ShouldBe(second);
        
        collection.Cardinality().ShouldBe(DatabaseCardinality.Single);
    }

    [Fact]
    public async Task using_static_multi_tenancy()
    {
        var collection = await startHost(opts =>
        {
            opts.Durability.MessageStorageSchemaName = "wolverine";
            opts.Services.AddMarten(m =>
            {
                m.DisableNpgsqlLogging = true;
                m.MultiTenantedDatabases(t =>
                {
                    t.AddSingleTenantDatabase(connectionString1, "t1");
                    t.AddSingleTenantDatabase(connectionString2, "t2");
                    t.AddSingleTenantDatabase(connectionString3, "t3");
                    t.AddSingleTenantDatabase(connectionString4, "t4");
                });
            }).IntegrateWithWolverine(w => w.MasterDatabaseConnectionString = Servers.PostgresConnectionString);
        });
        
        var main = new Uri("wolverinedb://postgresql/localhost/postgres/wolverine");
        var db1 = new Uri("wolverinedb://postgresql/localhost/database1/wolverine");
        var db2 = new Uri("wolverinedb://postgresql/localhost/database2/wolverine");
        var db3 = new Uri("wolverinedb://postgresql/localhost/database3/wolverine");
        var db4 = new Uri("wolverinedb://postgresql/localhost/database4/wolverine");
        
        
        var services = await collection.FindAllAsync();
        
        services.Select(x => x.Uri).OrderBy(x => x.ToString())
            .ShouldBe([db1, db2, db3, db4, main]);
        
        (await collection.FindDatabaseAsync(db3)).Uri.ShouldBe(db3);
        (await collection.FindDatabaseAsync(main)).Uri.ShouldBe(main);

        (await collection.FindDatabasesAsync([db1, main])).Select(x => x.Uri).ShouldBe([db1, main]);
        
        collection.Cardinality().ShouldBe(DatabaseCardinality.StaticMultiple);
    }
    
    [Fact]
    public async Task using_dynamic_multi_tenancy()
    {
        // THIS SHOULD TAKE CARE OF THE TEST RERUNS, but for some reason doesn't
        await dropWolverineSchema();

        var collection = await startHost(opts =>
        {
            opts.Durability.MessageStorageSchemaName = "wolverine";
            opts.Services.AddMarten(m =>
            {
                m.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString);
                

            }).IntegrateWithWolverine(w => w.MasterDatabaseConnectionString = Servers.PostgresConnectionString);
        });

        await _host.ClearAllTenantDatabaseRecordsAsync();

        await _host.AddTenantDatabaseAsync("t1", connectionString1);
        await _host.AddTenantDatabaseAsync("t2", connectionString2);
        
        
        var main = new Uri("wolverinedb://postgresql/localhost/postgres/wolverine");
        var db1 = new Uri("wolverinedb://postgresql/localhost/database1/wolverine");
        var db2 = new Uri("wolverinedb://postgresql/localhost/database2/wolverine");
        var db3 = new Uri("wolverinedb://postgresql/localhost/database3/wolverine");
        var db4 = new Uri("wolverinedb://postgresql/localhost/database4/wolverine");
        
        
        var services = await collection.FindAllAsync();
        
        services.Select(x => x.Uri).OrderBy(x => x.ToString())
            .ShouldBe([db1, db2, main]);
        
        (await collection.FindDatabaseAsync(db2)).Uri.ShouldBe(db2);
        (await collection.FindDatabaseAsync(main)).Uri.ShouldBe(main);
        
        await _host.AddTenantDatabaseAsync("t3", connectionString3);
        await _host.AddTenantDatabaseAsync("t4", connectionString4);
        
        services = await collection.FindAllAsync();
        
        services.Select(x => x.Uri).OrderBy(x => x.ToString())
            .ShouldBe([db1, db2, db3, db4, main]);

        (await collection.FindDatabasesAsync([db1, main])).Select(x => x.Uri).ShouldBe([db1, main]);
        
        collection.Cardinality().ShouldBe(DatabaseCardinality.DynamicMultiple);
    }

    private static async Task dropWolverineSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("wolverine");
        await conn.CloseAsync();
    }

    [Fact]
    public async Task no_message_stores_still_works()
    {
        var collection = await startHost(opts => { });
        collection.Main.ShouldBeOfType<NullMessageStore>();
        
        (await collection.FindAllAsync()).Any().ShouldBeFalse();
        
        collection.Cardinality().ShouldBe(DatabaseCardinality.None);
    }
}



public interface IFirstStore : IDocumentStore{}
public interface ISecondStore : IDocumentStore{}

