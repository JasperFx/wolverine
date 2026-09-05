using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Weasel.Core;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests.MultiTenancy;

/// <summary>
/// The wolverine_recurring_messages tracking table is MAIN-DATABASE-ONLY bookkeeping: the single
/// cluster-wide recurring agent publishes through the main store, so that is the one place the
/// table may ever exist. A tenant database that grew the table would invite split-brain tracking
/// rows nothing reads. Every "absent on a tenant" assertion here is paired with an "envelope
/// tables present on that same tenant" guard, so a tenant database that simply failed to migrate
/// cannot pass this test vacuously.
/// </summary>
public class recurring_table_exists_only_on_the_main_database : PostgresqlContext, IAsyncLifetime
{
    // Dedicated schema so the physical-table assertions cannot collide with any other suite's
    // leftovers in the shared "wolverine" schema — and so the pre-clean drops nothing shared.
    private const string SchemaName = "recurring_tenancy";

    private IHost _host = null!;
    private string _tenant1ConnectionString = null!;
    private string _tenant2ConnectionString = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        _tenant1ConnectionString = await createDatabaseIfNotExists(conn, "db1");
        _tenant2ConnectionString = await createDatabaseIfNotExists(conn, "db2");

        // Idempotence: a stale recurring table left on a tenant database by an older build must
        // fail THIS run's product code, not this run's leftovers.
        foreach (var connectionString in new[]
                     { Servers.PostgresConnectionString, _tenant1ConnectionString, _tenant2ConnectionString })
        {
            await dropSchema(connectionString);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(TenancyRecurringMessageHandler));
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName)
                    .RegisterStaticTenants(tenants =>
                    {
                        tenants.Register("one", _tenant1ConnectionString);
                        tenants.Register("two", _tenant2ConnectionString);
                    });

                // The registration is the feature's opt-in — this is what flips
                // EnableRecurringMessages before any migration runs.
                opts.Schedules.ScheduleRecurring<TenancyRecurringMessage>(
                    "tenancy-shape", "0 * * * *", _ => new TenancyRecurringMessage());
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_table_is_on_main_and_never_on_a_tenant_database()
    {
        // Main database: migrated, and carrying the recurring table.
        var mainTables = await tablesInSchema(Servers.PostgresConnectionString);
        mainTables.ShouldContain(DatabaseConstants.IncomingTable);
        mainTables.ShouldContain(DatabaseConstants.RecurringMessagesTableName);

        // Each tenant database: migrated (the guard that keeps this test honest) — and the
        // recurring table absent.
        foreach (var tenantConnectionString in new[] { _tenant1ConnectionString, _tenant2ConnectionString })
        {
            var tenantTables = await tablesInSchema(tenantConnectionString);
            tenantTables.ShouldContain(DatabaseConstants.IncomingTable,
                "tenant database was never migrated, so the absence assertion below would be vacuous");
            tenantTables.ShouldNotContain(DatabaseConstants.RecurringMessagesTableName);
        }

        // And the runtime wiring agrees with the physical shape, per role.
        var stores = await _host.GetRuntime().Stores.FindAllAsync();
        stores.ShouldNotBeEmpty();
        foreach (var store in stores)
        {
            if (store.Role == MessageStoreRole.Main)
            {
                store.RecurringMessages.Enabled.ShouldBeTrue();
            }
            else
            {
                store.RecurringMessages.Enabled.ShouldBeFalse();
            }
        }
    }

    private static async Task<string> createDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
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

    private static async Task dropSchema(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await conn.RunSqlAsync($"drop schema if exists {SchemaName} cascade");
    }

    private static async Task<string[]> tablesInSchema(string connectionString)
    {
        var list = new List<string>();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select table_name from information_schema.tables where table_schema = @schema";
        cmd.Parameters.AddWithValue("schema", SchemaName);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list.ToArray();
    }
}

public class TenancyRecurringMessage;

public static class TenancyRecurringMessageHandler
{
    public static void Handle(TenancyRecurringMessage message)
    {
    }
}
