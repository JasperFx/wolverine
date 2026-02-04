using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.RDBMS;

namespace SqlServerTests.MultiTenancy;

public abstract class MultiTenancyContext : SqlServerContext, IAsyncLifetime
{
    protected IHost theHost;
    protected string tenant1ConnectionString;
    protected string tenant2ConnectionString;
    protected string tenant3ConnectionString;

    public new async Task InitializeAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "db1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "db2");
        tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "db3");

        await cleanItems(tenant1ConnectionString);
        await cleanItems(tenant2ConnectionString);
        await cleanItems(tenant3ConnectionString);

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { configureWolverine(opts); }).StartAsync();

        await onStartup();
    }

    protected abstract void configureWolverine(WolverineOptions opts);

    protected virtual Task onStartup() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await theHost.StopAsync();
    }

    private async Task<string> CreateDatabaseIfNotExists(SqlConnection conn, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(Servers.SqlServerConnectionString);

        var exists = await DatabaseExistsAsync(conn, databaseName);
        if (!exists)
        {
            await conn.CreateCommand($"CREATE DATABASE [{databaseName}]").ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = databaseName;

        return builder.ConnectionString;
    }

    private static async Task<bool> DatabaseExistsAsync(SqlConnection conn, string databaseName)
    {
        var cmd = conn.CreateCommand($"SELECT DB_ID(@databaseName)");
        cmd.Parameters.AddWithValue("@databaseName", databaseName);
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value;
    }

    private async Task cleanItems(string connectionString)
    {
        var table = new Table("items");
        table.AddColumn<Guid>("Id").AsPrimaryKey();
        table.AddColumn<string>("Name");

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await table.ApplyChangesAsync(conn);

        await conn.CreateCommand("delete from items").ExecuteNonQueryAsync();
        await conn.CloseAsync();
    }
}
