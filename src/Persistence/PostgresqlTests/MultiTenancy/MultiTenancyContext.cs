using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.RDBMS;

namespace PostgresqlTests.MultiTenancy;

public abstract class MultiTenancyContext : PostgresqlContext, IAsyncLifetime
{
    protected IHost theHost;
    protected string tenant1ConnectionString;
    protected string tenant2ConnectionString;
    protected string tenant3ConnectionString;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "db1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "db2");
        tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "db3");

        await cleanItems(tenant1ConnectionString);
        await cleanItems(tenant2ConnectionString);
        await cleanItems(tenant3ConnectionString);
        
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                configureWolverine(opts);

            }).StartAsync();

        await onStartup();
    }

    public async Task SchemaTables(IDatabase<NpgsqlConnection> database)
    {
        using var conn = database.CreateConnection();
        await conn.OpenAsync();
    }

    protected abstract void configureWolverine(WolverineOptions opts);

    protected virtual Task onStartup() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
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
    
    private async Task cleanItems(string connectionString)
    {
        var table = new Table("items");
        table.AddColumn<Guid>("Id").AsPrimaryKey();
        table.AddColumn<string>("Name");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync();

        await table.ApplyChangesAsync(conn);

        await conn.RunSqlAsync("delete from items");
        await conn.CloseAsync();
    }

}