using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PostgresqlTests;

/// <summary>
/// GH-5056. Under ServerlessWithOutbox the outbox stale clock keeps a durability pass away from a row the request
/// that wrote it may still be sending inline. PostgreSQL's now() is the start of the transaction, so a transaction
/// that ran for longer than OutboxStaleTime before it wrote its outbox row handed that row to a pass the moment it
/// committed: a duplicate send. The outgoing table's timestamp defaults to clock_timestamp() in this mode instead,
/// the time the row is written, like SQL Server's GETUTCDATE()
/// </summary>
public class serverless_with_outbox_outgoing_stale_clock_5056 : IAsyncLifetime
{
    private const string SchemaName = "gh5050_stale_clock";
    private IHost theHost = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Durability.OutboxStaleTime = 2.Seconds();
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
            }).StartAsync(Token);

        await theHost.RebuildAllEnvelopeStorageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static async Task<string?> timestampDefaultAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
                           select pg_get_expr(d.adbin, d.adrelid)
                           from pg_attrdef d
                           join pg_attribute a on a.attrelid = d.adrelid and a.attnum = d.adnum
                           where d.adrelid = to_regclass('{SchemaName}.{DatabaseConstants.OutgoingTable}')
                             and a.attname = '{DatabaseConstants.Timestamp}'
                           """;
        return (string?)await cmd.ExecuteScalarAsync(Token);
    }

    [Fact]
    public async Task a_row_committed_by_a_long_transaction_is_not_stale_the_moment_it_commits()
    {
        var store = (IMessageDatabase)theHost.GetRuntime().Storage;
        var envelope = ObjectMother.Envelope();
        envelope.Destination = "stub://clock".ToUri();
        envelope.DeliverBy = null;

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Token);
            await using var tx = await conn.BeginTransactionAsync(Token);

            // The transaction is open for longer than OutboxStaleTime before the cascade is persisted, like a slow
            // handler inside the transaction a lightweight saga or an EF Core Eager transaction opens
            await using (var sleep = conn.CreateCommand())
            {
                sleep.Transaction = tx;
                sleep.CommandText = "select pg_sleep(3)";
                await sleep.ExecuteNonQueryAsync(Token);
            }

            await store.StoreOutgoingAsync(tx, [envelope]);
            await tx.CommitAsync(Token);
        }

        // The request that committed is about to send the row inline right now. A pass started at the same moment
        // must leave it alone, or the message goes out twice
        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(0, "the stale clock started when the transaction began, not when the row was written");
        report.OutgoingFailed.ShouldBe(0);
    }

    [Fact]
    public async Task an_existing_outgoing_table_with_the_now_default_is_repaired_on_migration()
    {
        // As a database created before GH-5056, or by an application in another durability mode, has it
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"alter table {SchemaName}.{DatabaseConstants.OutgoingTable} alter column \"{DatabaseConstants.Timestamp}\" set default now()";
            await cmd.ExecuteNonQueryAsync(Token);
        }

        (await timestampDefaultAsync()).ShouldBe("now()");

        await theHost.GetRuntime().Storage.Admin.MigrateAsync();

        (await timestampDefaultAsync()).ShouldBe("clock_timestamp()");

        // And a second migration finds nothing to do
        await theHost.GetRuntime().Storage.Admin.MigrateAsync();
        (await timestampDefaultAsync()).ShouldBe("clock_timestamp()");
    }
}

/// <summary>
/// GH-5056. The stale outgoing release in the other modes has the same long-transaction race, so the outgoing table
/// defaults to clock_timestamp() whatever the durability mode, and an existing now() default is repaired
/// </summary>
public class outgoing_timestamp_default_in_every_mode_5056
{
    private const string SchemaName = "gh5056_every_mode";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string?> timestampDefaultAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
                           select pg_get_expr(d.adbin, d.adrelid)
                           from pg_attrdef d
                           join pg_attribute a on a.attrelid = d.adrelid and a.attnum = d.adnum
                           where d.adrelid = to_regclass('{SchemaName}.{DatabaseConstants.OutgoingTable}')
                             and a.attname = '{DatabaseConstants.Timestamp}'
                           """;
        return (string?)await cmd.ExecuteScalarAsync(Token);
    }

    [Theory]
    [InlineData(DurabilityMode.Solo)]
    [InlineData(DurabilityMode.Balanced)]
    public async Task the_outgoing_timestamp_defaults_to_clock_timestamp(DurabilityMode mode)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = mode;
                opts.Durability.OutboxStaleTime = 1.Minutes();
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
            }).StartAsync(Token);

        await host.RebuildAllEnvelopeStorageAsync();
        (await timestampDefaultAsync()).ShouldBe("clock_timestamp()");

        // A table created before GH-5056 is repaired on migration
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"alter table {SchemaName}.{DatabaseConstants.OutgoingTable} alter column \"{DatabaseConstants.Timestamp}\" set default now()";
            await cmd.ExecuteNonQueryAsync(Token);
        }

        await host.GetRuntime().Storage.Admin.MigrateAsync();
        (await timestampDefaultAsync()).ShouldBe("clock_timestamp()");

        await host.StopAsync(Token);
    }
}
