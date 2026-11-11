using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Schema;
using Wolverine.RDBMS;

namespace PostgresqlTests;

/// <summary>
/// GH-5519. <see cref="DurabilitySettings.EnableInboxPartitioning" /> is on by default in 7.0, so every PostgreSQL
/// application's existing, unpartitioned inbox is rebuilt as a partitioned one on its first 7.0 start. Weasel does that
/// by copying the table aside, dropping it, creating it again and copying the rows back, in one transaction that holds
/// an exclusive lock on the inbox throughout. These pin that the rows survive it, and that a row another node commits
/// while it runs is not lost.
/// </summary>
[Collection("marten")]
public class inbox_partitioning_migration_5519 : IAsyncLifetime
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    private readonly ITestOutputHelper _output;

    public inbox_partitioning_migration_5519(ITestOutputHelper output)
    {
        _output = output;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task an_existing_6x_inbox_is_partitioned_on_the_first_7_0_start_and_keeps_its_rows()
    {
        const string schema = "inbox_part_upgrade";
        await dropSchemaAsync(schema);

        // The 6.x layout: unpartitioned, keyed by id alone
        using (var v6 = await hostAsync(schema, opts => opts.RestoreV6Defaults()))
        {
        }

        (await isPartitionedAsync(schema)).ShouldBeFalse();

        var incoming = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var scheduled = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var handled = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in incoming) await insertRowAsync(schema, id, "Incoming");
        foreach (var id in scheduled) await insertRowAsync(schema, id, "Scheduled");
        foreach (var id in handled) await insertRowAsync(schema, id, "Handled");

        // The 7.0 defaults, nothing set explicitly
        using (var v7 = await hostAsync(schema, _ => { }))
        {
        }

        (await isPartitionedAsync(schema)).ShouldBeTrue();
        (await countAsync(schema, "Incoming")).ShouldBe(incoming.Length);
        (await countAsync(schema, "Scheduled")).ShouldBe(scheduled.Length);
        (await countAsync(schema, "Handled")).ShouldBe(handled.Length);
        (await tableExistsAsync(schema, DatabaseConstants.IncomingTable + "_temp")).ShouldBeFalse();
    }

    [Fact]
    public async Task an_explicit_false_keeps_the_existing_inbox_unpartitioned()
    {
        const string schema = "inbox_part_explicit_off";
        await dropSchemaAsync(schema);

        using (var first = await hostAsync(schema, opts => opts.Durability.EnableInboxPartitioning = false))
        {
        }

        using (var second = await hostAsync(schema, opts => opts.Durability.EnableInboxPartitioning = false))
        {
        }

        (await isPartitionedAsync(schema)).ShouldBeFalse();
    }

    [Fact]
    public async Task the_rebuild_holds_an_exclusive_lock_on_the_inbox_from_before_the_copy_until_the_copy_back()
    {
        const string schema = "inbox_part_lock_sql";
        await dropSchemaAsync(schema);
        await applyAsync(new IncomingEnvelopeTable(new DurabilitySettings { EnableInboxPartitioning = false }, schema));

        var script = await migrationScriptAsync(new IncomingEnvelopeTable(new DurabilitySettings(), schema));
        _output.WriteLine(script);

        var table = $"{schema}.{DatabaseConstants.IncomingTable}";

        // Partitioning puts status in the primary key, which an unpartitioned inbox never has, so Weasel's script
        // starts by changing the primary key. ALTER TABLE takes ACCESS EXCLUSIVE, and the rest of the script runs in
        // the same transaction, so the copy aside sees every committed row and nothing can write until the commit.
        var alterAt = script.IndexOf($"alter table {table} drop constraint", StringComparison.Ordinal);
        var copyAt = script.IndexOf($"create table {table}_temp as select * from {table};", StringComparison.Ordinal);
        var copyBackAt = script.IndexOf($"from {table}_temp;", StringComparison.Ordinal);

        alterAt.ShouldBeGreaterThanOrEqualTo(0);
        copyAt.ShouldBeGreaterThan(alterAt);
        copyBackAt.ShouldBeGreaterThan(copyAt);

        // The migrator sends the script as one command -- one transaction -- except where it is split around a
        // concurrent index build. Nothing may split the lock from the copy back, or the lock is released while the
        // rows are still in the temporary table.
        script.Substring(0, copyBackAt).ShouldNotContain(IndexDefinition.IndexCreationBeginComment);
    }

    [Fact]
    public async Task an_inbox_that_is_already_partitioned_is_not_rebuilt()
    {
        const string schema = "inbox_part_no_rebuild";
        await dropSchemaAsync(schema);
        await applyAsync(new IncomingEnvelopeTable(new DurabilitySettings(), schema));

        var script = await migrationScriptAsync(new IncomingEnvelopeTable(new DurabilitySettings(), schema));

        script.ShouldNotContain("_temp");
    }

    [Fact]
    public async Task a_row_another_node_commits_during_the_rebuild_is_not_lost()
    {
        const string schema = "inbox_part_race";
        await dropSchemaAsync(schema);
        await applyAsync(new IncomingEnvelopeTable(new DurabilitySettings { EnableInboxPartitioning = false }, schema));

        var before = Guid.NewGuid();
        await insertRowAsync(schema, before, "Incoming");

        // Another node is part way through writing a row: its transaction holds the inbox open
        await using var otherNode = new NpgsqlConnection(Servers.PostgresConnectionString);
        await otherNode.OpenAsync(ct);
        await using var tx = await otherNode.BeginTransactionAsync(ct);
        var during = Guid.NewGuid();
        await insertRowAsync(otherNode, schema, during, "Incoming");

        // The 7.0 node migrates meanwhile. Were the copy aside not behind an exclusive lock, it would read a snapshot
        // without the other node's row, then wait for that transaction only to drop the table holding it
        var migration = applyAsync(new IncomingEnvelopeTable(new DurabilitySettings(), schema));

        await waitUntilAsync(() => someoneIsWaitingForTheInboxAsync(schema), "the migration to wait for the inbox lock");

        await tx.CommitAsync(ct);
        await migration;

        (await isPartitionedAsync(schema)).ShouldBeTrue();
        (await rowExistsAsync(schema, before)).ShouldBeTrue();
        (await rowExistsAsync(schema, during)).ShouldBeTrue();
    }

    [Fact]
    public async Task one_message_can_be_both_incoming_and_scheduled_in_the_partitioned_inbox()
    {
        // The documented trade-off of partitioning: status joins the primary key, so uniqueness only holds within a
        // partition. Pinned here because the migration guide says so
        const string schema = "inbox_part_pair";
        await dropSchemaAsync(schema);
        await applyAsync(new IncomingEnvelopeTable(new DurabilitySettings(), schema));

        var id = Guid.NewGuid();
        await insertRowAsync(schema, id, "Incoming");
        await insertRowAsync(schema, id, "Scheduled");

        (await countAsync(schema, "Incoming")).ShouldBe(1);
        (await countAsync(schema, "Scheduled")).ShouldBe(1);
    }

    [Fact]
    public async Task how_long_the_rebuild_holds_the_inbox()
    {
        // Not an assertion: the number the migration guide's lock duration statement comes from. The lock is held for
        // the copy aside and the copy back, both proportional to the row count
        const string schema = "inbox_part_timing";
        const int rows = 100_000;
        await dropSchemaAsync(schema);
        await applyAsync(new IncomingEnvelopeTable(new DurabilitySettings { EnableInboxPartitioning = false }, schema));

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(ct);
            await conn.CreateCommand(
                    $"insert into {schema}.{DatabaseConstants.IncomingTable} (id, status, owner_id, body, message_type, received_at) " +
                    $"select gen_random_uuid(), 'Handled', 0, decode(repeat('ab', 500), 'hex'), 'Some.Message', 'local://one/' " +
                    $"from generate_series(1, {rows})")
                .ExecuteNonQueryAsync(ct);
        }

        var stopwatch = Stopwatch.StartNew();
        await applyAsync(new IncomingEnvelopeTable(new DurabilitySettings(), schema));
        stopwatch.Stop();

        _output.WriteLine($"Rebuilt an inbox of {rows} rows of 500 bytes into partitions in {stopwatch.ElapsedMilliseconds} ms");
        (await countAsync(schema, "Handled")).ShouldBe(rows);
    }

    private static async Task<IHost> hostAsync(string schema, Action<WolverineOptions> configure)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                configure(opts);
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(ct);

        await host.StopAsync(ct);
        return host;
    }

    private static async Task applyAsync(Table table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await conn.EnsureSchemaExists(table.Identifier.Schema!, ct);

        // The path Wolverine's own migration takes: SchemaMigration reads the delta through CreateDeltaAsync
        var migration = await SchemaMigration.DetermineAsync(conn, ct, table);
        await new PostgresqlMigrator().ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate, ct: ct);
    }

    private static async Task<string> migrationScriptAsync(Table table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        var migration = await SchemaMigration.DetermineAsync(conn, ct, table);
        using var writer = new StringWriter();
        migration.WriteAllUpdates(writer, new PostgresqlMigrator(), AutoCreate.CreateOrUpdate);
        return writer.ToString();
    }

    private static async Task insertRowAsync(string schema, Guid id, string status)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await insertRowAsync(conn, schema, id, status);
    }

    private static async Task insertRowAsync(NpgsqlConnection conn, string schema, Guid id, string status)
    {
        await using var cmd = conn.CreateCommand(
            $"insert into {schema}.{DatabaseConstants.IncomingTable} (id, status, owner_id, body, message_type, received_at) " +
            "values (:id, :status, 0, :body, 'Some.Message', 'local://one/')");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("body", new byte[] { 1, 2, 3 });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> countAsync(string schema, string status)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand(
            $"select count(*) from {schema}.{DatabaseConstants.IncomingTable} where status = :status");
        cmd.Parameters.AddWithValue("status", status);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> rowExistsAsync(string schema, Guid id)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand(
            $"select count(*) from {schema}.{DatabaseConstants.IncomingTable} where id = :id");
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private static async Task<bool> isPartitionedAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand(
            "select count(*) from pg_partitioned_table p join pg_class c on c.oid = p.partrelid " +
            "join pg_namespace n on n.oid = c.relnamespace where n.nspname = :schema and c.relname = :table");
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", DatabaseConstants.IncomingTable);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private static async Task<bool> tableExistsAsync(string schema, string table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand(
            "select count(*) from information_schema.tables where table_schema = :schema and table_name = :table");
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private static async Task<bool> someoneIsWaitingForTheInboxAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand(
            "select count(*) from pg_locks l join pg_class c on c.oid = l.relation " +
            "join pg_namespace n on n.oid = c.relnamespace " +
            "where n.nspname = :schema and c.relname = :table and not l.granted");
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", DatabaseConstants.IncomingTable);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task waitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(50, ct);
        }
    }

    private static async Task dropSchemaAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await conn.DropSchemaAsync(schema, ct);
    }
}
