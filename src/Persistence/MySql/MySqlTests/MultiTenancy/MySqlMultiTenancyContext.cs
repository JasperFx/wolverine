using IntegrationTests;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Wolverine;

namespace MySqlTests.MultiTenancy;

[Collection("mysql")]
public abstract class MySqlMultiTenancyContext : IAsyncLifetime
{
    protected IHost theHost = null!;
    protected string tenant1ConnectionString = string.Empty;
    protected string tenant2ConnectionString = string.Empty;
    protected string tenant3ConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();

        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant_db1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant_db2");
        tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant_db3");

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

    private async Task<string> CreateDatabaseIfNotExists(MySqlConnection conn, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(Servers.MySqlConnectionString);

        // Check if database exists
        var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{databaseName}'";
        var exists = await checkCmd.ExecuteScalarAsync() != null;

        if (!exists)
        {
            var createCmd = conn.CreateCommand();
            createCmd.CommandText = $"CREATE DATABASE {databaseName}";
            await createCmd.ExecuteNonQueryAsync();
        }

        builder.Database = databaseName;
        return builder.ConnectionString;
    }

    private async Task cleanItems(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();

        // Create items table if not exists
        var createTableCmd = conn.CreateCommand();
        createTableCmd.CommandText = @"
CREATE TABLE IF NOT EXISTS items (
    Id BINARY(16) PRIMARY KEY,
    Name VARCHAR(255)
)";
        await createTableCmd.ExecuteNonQueryAsync();

        // Clean the table
        var deleteCmd = conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM items";
        await deleteCmd.ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }
}
