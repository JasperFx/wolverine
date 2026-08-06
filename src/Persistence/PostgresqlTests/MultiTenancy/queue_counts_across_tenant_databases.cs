using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests.MultiTenancy;

/// <summary>
/// GH-3815. <c>forEveryDatabase</c> walked <c>Parent.Store</c> and then every entry of
/// <c>Parent.Databases.ActiveDatabases()</c> — but <c>MultiTenantedMessageStore.ActiveDatabases()</c>
/// yields <c>Main</c> first, and <c>Databases</c> is only ever assigned alongside <c>Store = mt.Main</c>.
/// The main database was therefore visited twice, so <c>CountAsync()</c> and <c>ScheduledCountAsync()</c>
/// double counted every row living in it. Those two feed <c>GetAttributesAsync()</c>, so this was
/// user visible queue depth, not just a test concern.
///
/// The existing multi-tenant coverage misses it because it only ever asserts a count of <c>0</c>, and
/// zero doubled is still zero.
/// </summary>
public class queue_counts_across_tenant_databases : MultiTenancyContext
{
    private const string SchemaName = "queue_counts_tenanted";
    private const string QueueName = "countone";

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName)
            .EnableMessageTransport(transport => transport.TransportSchemaName(SchemaName))
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        // Subscriber only -- no listener, so nothing drains the queue out from under the assertions.
        opts.PublishAllMessages().ToPostgresqlQueue(QueueName);

        opts.Services.AddResourceSetupOnStartup();
    }

    protected override async Task onStartup()
    {
        foreach (var connectionString in allConnectionStrings())
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            try
            {
                foreach (var table in new[] { $"wolverine_queue_{QueueName}", $"wolverine_queue_{QueueName}_scheduled" })
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"delete from {SchemaName}.{table}";
                    try
                    {
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable ||
                                                      e.SqlState == PostgresErrorCodes.InvalidSchemaName)
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

    private string[] allConnectionStrings() =>
    [
        Servers.PostgresConnectionString, tenant1ConnectionString, tenant2ConnectionString, tenant3ConnectionString
    ];

    private PostgresqlQueue theQueue =>
        theHost.GetRuntime().Options.Transports.GetOrCreate<PostgresqlTransport>().Queues[QueueName];

    /// <summary>
    /// A row in the *main* database is the one that gets double counted — an untenanted send lands there.
    /// </summary>
    [Fact]
    public async Task does_not_double_count_rows_in_the_main_database()
    {
        var runtime = theHost.GetRuntime();
        ((MultiTenantedMessageStore)runtime.Storage).ActiveDatabases().Count.ShouldBe(4);

        var immediate = ObjectMother.Envelope();
        immediate.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(immediate);

        var scheduled = ObjectMother.Envelope();
        scheduled.ScheduleDelay = 1.Hours();
        scheduled.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(scheduled);

        // Precondition: exactly one row physically exists, in the main database only
        (await rowCountAsync(Servers.PostgresConnectionString, $"wolverine_queue_{QueueName}")).ShouldBe(1);
        (await rowCountAsync(Servers.PostgresConnectionString, $"wolverine_queue_{QueueName}_scheduled")).ShouldBe(1);

        (await theQueue.CountAsync()).ShouldBe(1);
        (await theQueue.ScheduledCountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// And the sum still reaches every tenant database -- the fix must not trade the double count for a
    /// missed database.
    /// </summary>
    [Fact]
    public async Task still_sums_across_main_and_every_tenant_database()
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
            (await rowCountAsync(connectionString, $"wolverine_queue_{QueueName}")).ShouldBe(1);
        }

        // One row in each of the four databases, counted once apiece
        (await theQueue.CountAsync()).ShouldBe(4);
    }

    /// <summary>
    /// GetAttributesAsync() is the user visible surface -- it reports whatever CountAsync() returns.
    /// </summary>
    [Fact]
    public async Task reported_attributes_match_the_physical_row_count()
    {
        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(envelope);

        var attributes = await theQueue.GetAttributesAsync();

        attributes["Count"].ShouldBe("1");
    }

    private static async Task<long> rowCountAsync(string connectionString, string tableName)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"select count(*) from {SchemaName}.{tableName}";
            return (long)(await cmd.ExecuteScalarAsync())!;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
