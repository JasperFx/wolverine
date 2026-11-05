using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Schema;
using Wolverine.RDBMS;

namespace PostgresqlTests;

// GH-4316: the 5-second recovery poll and the per-listener recovery load ask for
// `owner_id = 0` — exactly the value the GH-3971 owner index excludes — and the expired-handled
// cleanup filters on `status = 'Handled' and keep_until <= now` with no index at all, so all of
// them were full scans of an inbox dominated by retained Handled rows. The envelope tables now
// provision partial indexes for the recoverable and expired-handled slices. These tests prove the
// indexes are created AND that their compound predicates round-trip through pg_get_indexdef so a
// subsequent migration reports no drift (a mismatched predicate would make Weasel drop+recreate
// the index on every startup).
public class EnvelopeTables_recovery_index_creation : IAsyncLifetime
{
    private NpgsqlConnection theConnection = null!;

    public async ValueTask InitializeAsync()
    {
        theConnection = new NpgsqlConnection(Servers.PostgresConnectionString);
        await theConnection.OpenAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theConnection.DisposeAsync();
    }

    [Fact]
    public async Task incoming_recovery_indexes_are_created_and_stable()
    {
        await theConnection.ResetSchemaAsync("env_idx_incoming", ct: TestContext.Current.CancellationToken);

        var table = new IncomingEnvelopeTable(new DurabilitySettings(), "env_idx_incoming");

        table.Indexes.ShouldContain(x => x.Name.Contains("recover"));
        table.Indexes.ShouldContain(x => x.Name.Contains("keep_until"));
        table.Indexes.ShouldContain(x => x.Name.Contains("scheduled"));

        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        // Re-reading the just-created schema must report NO difference. If a partial-index
        // predicate did not round-trip, this would come back as Update and thrash on every startup.
        var delta = await table.FindDeltaAsync(theConnection, TestContext.Current.CancellationToken);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);

        await assertScheduledIndexExistsAsync("env_idx_incoming");
    }

    [Fact]
    public async Task incoming_recovery_indexes_are_stable_with_inbox_partitioning()
    {
        await theConnection.ResetSchemaAsync("env_idx_part", ct: TestContext.Current.CancellationToken);

        var durability = new DurabilitySettings { EnableInboxPartitioning = true };
        var table = new IncomingEnvelopeTable(durability, "env_idx_part");

        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        var delta = await table.FindDeltaAsync(theConnection, TestContext.Current.CancellationToken);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);

        await assertScheduledIndexExistsAsync("env_idx_part");
    }

    // GH-XXXX: the scheduled-message poll's own statement, explained against an inbox that looks like
    // a busy one -- mostly retained Handled rows, a small Scheduled slice -- must range-scan the
    // scheduled index in execution_time order rather than scan the whole table and sort it.
    [Fact]
    public async Task the_scheduled_poll_uses_the_scheduled_index()
    {
        const string schemaName = "env_idx_sched_poll";
        await theConnection.ResetSchemaAsync(schemaName, ct: TestContext.Current.CancellationToken);

        var durability = new DurabilitySettings();
        var table = new IncomingEnvelopeTable(durability, schemaName);
        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        var inbox = $"{schemaName}.{DatabaseConstants.IncomingTable}";
        await theConnection.CreateCommand($"""
            insert into {inbox} (id, status, owner_id, body, message_type, received_at, keep_until)
            select gen_random_uuid(), 'Handled', 0, '\x00'::bytea, 'Handled.Message', 'local://one/',
                   now() + interval '5 minutes'
            from generate_series(1, 20000);

            insert into {inbox} (id, status, owner_id, execution_time, body, message_type, received_at)
            select gen_random_uuid(), 'Scheduled', 0, now() + (g - 100) * interval '1 second', '\x00'::bytea,
                   'Scheduled.Message', 'local://one/'
            from generate_series(1, 200) g;

            analyze {inbox};
            """).ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await using var store = new PostgresqlMessageStore(
            new DatabaseSettings { ConnectionString = Servers.PostgresConnectionString, SchemaName = schemaName },
            durability, NpgsqlDataSource.Create(Servers.PostgresConnectionString),
            NullLogger<PostgresqlMessageStore>.Instance);

        var builder = new DbCommandBuilder(theConnection);
        store.WriteLoadScheduledEnvelopeSql(builder, DateTimeOffset.UtcNow);
        await using var explain = builder.Compile();
        explain.Connection = theConnection;
        explain.CommandText = "explain " + explain.CommandText;

        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                plan.Add(reader.GetString(0));
            }
        }

        var planText = string.Join(Environment.NewLine, plan);
        planText.ShouldContain($"Index Scan using idx_{DatabaseConstants.IncomingTable}_scheduled");
        planText.ShouldNotContain("Seq Scan");
        planText.ShouldNotContain("Sort");
    }

    [Fact]
    public async Task outgoing_recovery_index_is_created_and_stable()
    {
        await theConnection.ResetSchemaAsync("env_idx_outgoing", ct: TestContext.Current.CancellationToken);

        var table = new OutgoingEnvelopeTable(new DurabilitySettings(), "env_idx_outgoing");

        table.Indexes.ShouldContain(x => x.Name.Contains("recover"));

        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        var delta = await table.FindDeltaAsync(theConnection, TestContext.Current.CancellationToken);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }

    private async Task assertScheduledIndexExistsAsync(string schemaName)
    {
        var existing = await new IncomingEnvelopeTable(new DurabilitySettings(), schemaName)
            .FetchExistingAsync(theConnection, TestContext.Current.CancellationToken);

        existing.ShouldNotBeNull();
        existing.Indexes.ShouldContain(x => x.Name == $"idx_{DatabaseConstants.IncomingTable}_scheduled");
    }
}
