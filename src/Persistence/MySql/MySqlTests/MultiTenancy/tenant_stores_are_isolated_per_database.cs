using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MySqlTests.MultiTenancy;

/// <summary>
/// GH-3860. A MySQL schema IS a database, so giving every tenant store the one configured envelope
/// storage schema name discarded the tenant's own database and collapsed all of them onto a single
/// physical set of tables: <c>wolverine_incoming_envelopes</c>, <c>wolverine_outgoing_envelopes</c>,
/// the node and dead letter tables, and the saga tables, all shared across tenants with no isolation
/// whatsoever. PostgreSQL is immune because its schema nests *inside* the tenant database.
///
/// The pre-existing static_multi_tenancy suite could not catch this: it only asserts that the tenancy
/// source resolves the expected connection strings, never that anything is stored separately.
/// </summary>
[Collection("mysql")]
public class tenant_stores_are_isolated_per_database : MySqlMultiTenancyContext
{
    private const string SchemaName = "tenant_store_isolation";

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, SchemaName)
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        opts.Services.AddResourceSetupOnStartup();
    }

    /// <summary>
    /// The tenant databases are shared with every other suite in this collection and survive between runs,
    /// so start from a known-empty inbox. Without this the row counts below accumulate and the test only
    /// passes the first time it is ever run against a given MySQL instance.
    /// </summary>
    protected override async Task onStartup()
    {
        foreach (var connectionString in
                 new[] { tenant1ConnectionString, tenant2ConnectionString, tenant3ConnectionString })
        {
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"delete from {DatabaseConstants.IncomingTable}";
                await cmd.ExecuteNonQueryAsync();
            }
            catch (MySqlException)
            {
                // Nothing provisioned in this database yet, nothing to clean
            }
            finally
            {
                await conn.CloseAsync();
            }
        }
    }

    private async Task<IMessageStore> storeForAsync(string tenantId)
    {
        var stores = (MultiTenantedMessageStore)theHost.GetRuntime().Storage;
        return await stores.GetDatabaseAsync(tenantId);
    }

    /// <summary>
    /// The structural claim: every tenant database physically owns the envelope tables. Before the fix
    /// the tenant databases were completely empty.
    /// </summary>
    [Theory]
    [InlineData("red", "tenant_db1")]
    [InlineData("blue", "tenant_db2")]
    [InlineData("green", "tenant_db3")]
    public async Task each_tenant_database_owns_its_own_envelope_tables(string tenantId, string expectedDatabase)
    {
        // Materialize and migrate the tenant store
        await storeForAsync(tenantId);

        var connectionString = connectionStringFor(expectedDatabase);

        foreach (var table in new[] { DatabaseConstants.IncomingTable, DatabaseConstants.OutgoingTable })
        {
            (await tableExistsAsync(connectionString, expectedDatabase, table))
                .ShouldBeTrue($"Expected {expectedDatabase}.{table} to exist");
        }
    }

    /// <summary>
    /// And the tenant store really is pointed at that database rather than the shared configured schema.
    /// </summary>
    [Fact]
    public async Task tenant_store_schema_is_the_tenants_own_database()
    {
        var red = (IMessageDatabase)await storeForAsync("red");
        var blue = (IMessageDatabase)await storeForAsync("blue");

        red.SchemaName.ShouldBe("tenant_db1");
        blue.SchemaName.ShouldBe("tenant_db2");

        red.SchemaName.ShouldNotBe(blue.SchemaName);
        red.SchemaName.ShouldNotBe(SchemaName);
    }

    /// <summary>
    /// The behavioural claim: a tenant's envelope is visible in that tenant's database and nowhere else.
    /// This is the one that actually matters -- shared tables meant cross-tenant reads.
    /// </summary>
    [Fact]
    public async Task an_envelope_stored_for_one_tenant_is_invisible_to_the_others()
    {
        var red = await storeForAsync("red");

        // Materialize the other tenants too, so their tables exist and a "no change" reading below is a
        // real measurement rather than a missing table.
        await storeForAsync("blue");
        await storeForAsync("green");

        // These databases are shared with every other suite in the collection, so measure the DELTA this
        // store causes rather than absolute counts.
        var before = await countsByDatabaseAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = 0;

        await red.Inbox.StoreIncomingAsync(envelope);

        var after = await countsByDatabaseAsync();

        (after["tenant_db1"] - before["tenant_db1"]).ShouldBe(1);

        foreach (var database in new[] { "tenant_db2", "tenant_db3" })
        {
            (after[database] - before[database])
                .ShouldBe(0, $"{database} must not receive another tenant's inbox row");
        }

        (after[SchemaName] - before[SchemaName])
            .ShouldBe(0, "The shared configured schema must not be collecting tenant rows");
    }

    private async Task<Dictionary<string, long>> countsByDatabaseAsync()
    {
        return new Dictionary<string, long>
        {
            ["tenant_db1"] = await incomingCountAsync(tenant1ConnectionString),
            ["tenant_db2"] = await incomingCountAsync(tenant2ConnectionString),
            ["tenant_db3"] = await incomingCountAsync(tenant3ConnectionString),
            [SchemaName] = await incomingCountAsync(Servers.MySqlConnectionString, SchemaName)
        };
    }

    private string connectionStringFor(string database)
    {
        return database switch
        {
            "tenant_db1" => tenant1ConnectionString,
            "tenant_db2" => tenant2ConnectionString,
            "tenant_db3" => tenant3ConnectionString,
            _ => throw new ArgumentOutOfRangeException(nameof(database))
        };
    }

    private static async Task<bool> tableExistsAsync(string connectionString, string database, string tableName)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "select count(*) from information_schema.tables where table_schema = @schema and table_name = @table";
            cmd.Parameters.AddWithValue("schema", database);
            cmd.Parameters.AddWithValue("table", tableName);

            return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static async Task<long> incomingCountAsync(string connectionString, string? schema = null)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        try
        {
            var table = schema.IsEmpty()
                ? DatabaseConstants.IncomingTable
                : $"{schema}.{DatabaseConstants.IncomingTable}";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"select count(*) from {table}";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        catch (MySqlException)
        {
            // The table not existing at all is the strongest possible form of "no rows here"
            return 0;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
