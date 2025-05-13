using System.Diagnostics;
using IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace EfCoreTests.MultiTenancy;

// TODO -- extension methods to build this from IHost or IServiceProvider

public abstract class MultiTenancyContext : IAsyncLifetime
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

        await cleanItems(Servers.PostgresConnectionString);
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
        var table = new Table(new DbObjectName("sample", "items"));
        table.AddColumn<Guid>("id").AsPrimaryKey();
        table.AddColumn<string>("name");
        table.AddColumn<bool>("approved");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync();

        await table.ApplyChangesAsync(conn);

        await conn.RunSqlAsync("delete from sample.items");
        await conn.CloseAsync();
    }

}