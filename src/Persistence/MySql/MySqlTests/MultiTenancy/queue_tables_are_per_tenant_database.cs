using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MySqlTests.MultiTenancy;

/// <summary>
/// GH-3859. A MySQL schema IS a database, so qualifying the queue tables with the single transport-wide
/// <c>TransportSchemaName</c> resolved every tenant's data source to the *same* physical table: no tenant
/// isolation, and a <c>CountAsync()</c> that multiplied the true row count by the number of tenants.
/// On a multi-tenanted host each data source now resolves its queue tables inside its own database.
/// </summary>
[Collection("mysql")]
public class queue_tables_are_per_tenant_database : MySqlMultiTenancyContext
{
    private const string SchemaName = "queue_per_tenant";
    private const string QueueName = "pertenant";

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, SchemaName)
            .EnableMessageTransport()
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        // Subscriber only -- no listener, so nothing drains the queue out from under the assertions.
        opts.PublishAllMessages().ToMySqlQueue(QueueName);

        opts.Services.AddResourceSetupOnStartup();
    }

    protected override async Task onStartup()
    {
        foreach (var connectionString in allConnectionStrings())
        {
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync();
            try
            {
                foreach (var table in new[] { QueueTableName, ScheduledTableName })
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"delete from {table}";
                    try
                    {
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (MySqlException)
                    {
                        // Nothing provisioned in this database yet, nothing to clean
                    }
                }
            }
            finally
            {
                await conn.CloseAsync();
            }
        }
    }

    private const string QueueTableName = $"wolverine_queue_{QueueName}";
    private const string ScheduledTableName = $"wolverine_queue_{QueueName}_scheduled";

    private string[] allConnectionStrings() =>
    [
        Servers.MySqlConnectionString, tenant1ConnectionString, tenant2ConnectionString, tenant3ConnectionString
    ];

    private MySqlQueue theQueue =>
        theHost.GetRuntime().Options.Transports.GetOrCreate<MySqlTransport>().Queues[QueueName];

    /// <summary>
    /// The structural half: every tenant database must physically own its queue tables. Before the fix
    /// they existed only in the single "wolverine_queues" database.
    /// </summary>
    [Fact]
    public async Task every_tenant_database_owns_its_own_queue_tables()
    {
        ((MultiTenantedMessageStore)theHost.GetRuntime().Storage).ActiveDatabases().Count.ShouldBe(4);

        foreach (var connectionString in allConnectionStrings())
        {
            var database = new MySqlConnectionStringBuilder(connectionString).Database;

            (await tableExistsAsync(connectionString, database, QueueTableName))
                .ShouldBeTrue($"Expected {database}.{QueueTableName} to exist");
            (await tableExistsAsync(connectionString, database, ScheduledTableName))
                .ShouldBeTrue($"Expected {database}.{ScheduledTableName} to exist");
        }
    }

    /// <summary>
    /// The behavioural half: a tenant's message lands in that tenant's database and nowhere else.
    /// </summary>
    [Fact]
    public async Task a_tenants_message_lands_only_in_that_tenants_database()
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = "red";
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(envelope);

        (await rowCountAsync(tenant1ConnectionString)).ShouldBe(1);

        foreach (var other in new[] { Servers.MySqlConnectionString, tenant2ConnectionString, tenant3ConnectionString })
        {
            (await rowCountAsync(other)).ShouldBe(0);
        }
    }

    /// <summary>
    /// And with one row per database the sum is the true total, not a multiple of it. This is what
    /// GetAttributesAsync() reports as queue depth.
    /// </summary>
    [Fact]
    public async Task counts_sum_across_databases_instead_of_multiplying()
    {
        var untenanted = ObjectMother.Envelope();
        untenanted.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(untenanted);

        foreach (var tenantId in new[] { "red", "blue", "green" })
        {
            var envelope = ObjectMother.Envelope();
            envelope.TenantId = tenantId;
            envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
            await theQueue.SendAsync(envelope);
        }

        foreach (var connectionString in allConnectionStrings())
        {
            (await rowCountAsync(connectionString)).ShouldBe(1);
        }

        (await theQueue.CountAsync()).ShouldBe(4);
        (await theQueue.GetAttributesAsync())["Count"].ShouldBe("4");
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

    private static async Task<long> rowCountAsync(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"select count(*) from {QueueTableName}";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
