using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RDBMS;
using Wolverine.SqlServer;
using Wolverine.Persistence.Durability;

namespace SqlServerTests.Persistence;

/// <summary>
/// GH-5580. The whole message store compliance suite against a database with a case-sensitive collation, where an
/// identifier must be spelled exactly as it was declared: <c>id</c> is not <c>ID</c>, and the catalog views are
/// <c>INFORMATION_SCHEMA.TABLES</c>, not <c>information_schema.tables</c>.
/// </summary>
public class case_sensitive_collation_5580 : MessageStoreCompliance
{
    public const string DatabaseName = "wolverine_case_sensitive_5580";

    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static bool _created;

    public static string ConnectionString => new SqlConnectionStringBuilder(Servers.SqlServerConnectionString)
    {
        InitialCatalog = DatabaseName
    }.ConnectionString;

    public override async Task<IHost> BuildCleanHost()
    {
        await ensureDatabaseAsync();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(ConnectionString, "cs5580");
                opts.Durability.EnableDynamicListeners = true;
            })
            .StartAsync();

        var persistence = (IMessageDatabase)host.Services.GetRequiredService<IMessageStore>();
        await persistence.Admin.ClearAllAsync();

        return host;
    }

    private static async Task ensureDatabaseAsync()
    {
        if (_created) return;

        await _lock.WaitAsync();
        try
        {
            if (_created) return;

            await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Recreated once per test run, so every run migrates a brand new database. A database left over
            // from an earlier run already holds the stored procedures, and the bug is in creating them.
            cmd.CommandText = $"""
                               IF DB_ID('{DatabaseName}') IS NOT NULL
                               BEGIN
                                   ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                                   DROP DATABASE [{DatabaseName}];
                               END
                               CREATE DATABASE [{DatabaseName}] COLLATE SQL_Latin1_General_CP1_CS_AS;
                               """;
            await cmd.ExecuteNonQueryAsync();

            _created = true;
        }
        finally
        {
            _lock.Release();
        }
    }
}
