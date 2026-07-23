using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using JasperFx.Resources;
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
/// The multi-tenanted half of GH-3592. <c>PurgeAsync</c> / <c>SetupAsync</c> on a database queue
/// fan out through <c>forEveryDatabase</c>, so one call to <c>ClearAllWolverineStorageAsync()</c>
/// has to reach the queue tables in the main database *and* in every tenant database. The reverted
/// store-side reset hook only ever ran against whichever store you happened to call it on.
/// </summary>
public class clear_all_wolverine_storage_across_tenant_databases : MultiTenancyContext
{
    private const string SchemaName = "clear_all_tenanted";

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

        // Subscriber only -- no listener, so nothing drains the queue out from under the
        // assertions before the reset runs.
        opts.PublishAllMessages().ToPostgresqlQueue("resetone");

        opts.Services.AddResourceSetupOnStartup();
    }

    protected override async Task onStartup()
    {
        // Start from a known-empty state. Deliberately raw SQL rather than the method under test:
        // a fixture that bootstraps itself with ClearAllWolverineStorageAsync() would mask a
        // regression in it. Rows surviving between runs is exactly what this feature exists to
        // prevent, so leaving the tenant queue tables dirty here would make the test order-dependent.
        foreach (var connectionString in new[]
                 {
                     Servers.PostgresConnectionString, tenant1ConnectionString, tenant2ConnectionString,
                     tenant3ConnectionString
                 })
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            try
            {
                foreach (var table in new[] { "wolverine_queue_resetone", "wolverine_queue_resetone_scheduled" })
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

    [Fact]
    public async Task purges_the_queue_tables_in_every_tenant_database()
    {
        var runtime = theHost.GetRuntime();
        var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["resetone"];

        ((MultiTenantedMessageStore)runtime.Storage).ActiveDatabases().Count.ShouldBe(4);

        // A tenant id is what routes the send at a specific tenant database — without one the
        // multi-tenanted sender falls through to the main database and the tenant tables stay
        // empty, which would make every assertion below vacuous.
        foreach (var tenantId in new[] { "red", "blue", "green" })
        {
            var immediate = ObjectMother.Envelope();
            immediate.TenantId = tenantId;
            immediate.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
            await queue.SendAsync(immediate);

            var scheduled = ObjectMother.Envelope();
            scheduled.TenantId = tenantId;
            scheduled.ScheduleDelay = 1.Hours();
            scheduled.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
            await queue.SendAsync(scheduled);
        }

        // Precondition: each tenant database really has a row, so the purge has something to miss.
        foreach (var connectionString in new[]
                     { tenant1ConnectionString, tenant2ConnectionString, tenant3ConnectionString })
        {
            (await queueRowCountAsync(connectionString, "wolverine_queue_resetone")).ShouldBe(1);
            (await queueRowCountAsync(connectionString, "wolverine_queue_resetone_scheduled")).ShouldBe(1);
        }

        await theHost.ClearAllWolverineStorageAsync();

        // A purge that only reached the main store would leave every one of these non-zero.
        foreach (var connectionString in new[]
                     { tenant1ConnectionString, tenant2ConnectionString, tenant3ConnectionString })
        {
            (await queueRowCountAsync(connectionString, "wolverine_queue_resetone")).ShouldBe(0);
            (await queueRowCountAsync(connectionString, "wolverine_queue_resetone_scheduled")).ShouldBe(0);
        }

        // CountAsync sums across main + every tenant.
        (await queue.CountAsync()).ShouldBe(0);
        (await queue.ScheduledCountAsync()).ShouldBe(0);
    }

    private static async Task<long> queueRowCountAsync(string connectionString, string tableName)
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
