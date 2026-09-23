using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Sqlite;

namespace SqliteTests;

/// <summary>
/// GH-4567. <c>PostgresqlMessageStore</c> and <c>SqlServerMessageStore</c> override both batched-delete
/// hooks; <c>SqliteMessageStore</c> overrode neither, so SQLite -- and therefore every Fisher-backed
/// application -- reaped in one unbounded statement.
/// </summary>
/// <remarks>
/// <para>The bound matters more on SQLite than anywhere else. A write takes a lock over the whole database
/// file and there is only one writer, so a long reap stalls every other write in the application rather
/// than merely contending with inbox traffic.</para>
///
/// <para>What is bounded is each STATEMENT, not the total work: <c>DeleteExpiredAsync</c> runs the bounded
/// delete in a loop until a short batch tells it nothing expired is left, so its return value is the same
/// either way and cannot distinguish the two paths. These therefore assert the statement — that the
/// provider hands one back at all rather than the null that means "cannot bound", and that SQLite both
/// accepts it and honours the limit. SQLite has no <c>DELETE ... LIMIT</c>, which is the whole reason the
/// bound has to go through a <c>rowid</c> subquery, so executing it is the part worth proving.</para>
/// </remarks>
public class bounded_reaping_4567 : IAsyncLifetime
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase("bounded_reap_4567");
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Durability.EnableMessageDeduplication = true;
                opts.Durability.DeduplicationWindow = 1.Hours();
            }).StartAsync();

        await _host.RebuildAllEnvelopeStorageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _database.Dispose();
    }

    private IMessageDatabase theDatabase => (IMessageDatabase)_host.GetRuntime().Storage;

    private async Task<SqliteConnection> openAsync()
    {
        var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    private static async Task<int> executeAsync(SqliteConnection conn, string sql, DateTimeOffset? now = null)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        if (now.HasValue) command.Parameters.AddWithValue("now", now.Value);

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void the_deduplication_reap_is_bounded()
    {
        // Null is the "this provider cannot bound the delete" signal RdbmsDeduplicationStore falls back on,
        // and null is what SqliteMessageStore used to inherit
        var sql = theDatabase.BatchedDeleteExpiredDeduplicationClaimsSql(2);

        sql.ShouldNotBeNull();
        sql.ShouldContain("limit 2");
        sql.ShouldContain(DatabaseConstants.DeduplicationTableName);
    }

    [Fact]
    public void the_handled_envelope_reap_is_bounded()
    {
        var sql = theDatabase.BatchedDeleteExpiredHandledEnvelopesSql(2);

        sql.ShouldNotBeNull();
        sql.ShouldContain("limit 2");
        sql.ShouldContain(DatabaseConstants.IncomingTable);
    }

    [Fact]
    public async Task one_bounded_deduplication_statement_deletes_at_most_the_batch_size()
    {
        var store = _host.GetRuntime().Storage.Deduplication;
        var expired = DateTimeOffset.UtcNow.Subtract(1.Hours());

        for (var i = 0; i < 5; i++)
        {
            (await store.TryClaimAsync($"expired-{i}", expired, TestContext.Current.CancellationToken))
                .ShouldBeTrue();
        }

        await using var conn = await openAsync();

        // Five expired rows, one statement bounded at two
        (await executeAsync(conn, theDatabase.BatchedDeleteExpiredDeduplicationClaimsSql(2)!, DateTimeOffset.UtcNow))
            .ShouldBe(2);
    }

    [Fact]
    public async Task one_bounded_handled_envelope_statement_deletes_at_most_the_batch_size()
    {
        var table = TablePrefixing.Apply(theDatabase.SchemaName, DatabaseConstants.IncomingTable);

        await using var conn = await openAsync();

        for (var i = 0; i < 5; i++)
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText =
                $"insert into {table} (id, status, owner_id, body, message_type, received_at, keep_until) " +
                $"values ('{Guid.NewGuid()}', '{EnvelopeStatus.Handled}', 0, @body, 'thing', 'local://one', @keep)";
            insert.Parameters.AddWithValue("body", new byte[] { 1 });
            insert.Parameters.AddWithValue("keep", DateTimeOffset.UtcNow.Subtract(1.Hours()));
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // A row that is NOT expired, and one that is not Handled: the bound must not widen the predicate
        await using (var live = conn.CreateCommand())
        {
            live.CommandText =
                $"insert into {table} (id, status, owner_id, body, message_type, received_at, keep_until) " +
                $"values ('{Guid.NewGuid()}', '{EnvelopeStatus.Handled}', 0, @body, 'thing', 'local://one', @keep); " +
                $"insert into {table} (id, status, owner_id, body, message_type, received_at, keep_until) " +
                $"values ('{Guid.NewGuid()}', '{EnvelopeStatus.Incoming}', 0, @body, 'thing', 'local://one', @stale);";
            live.Parameters.AddWithValue("body", new byte[] { 1 });
            live.Parameters.AddWithValue("keep", DateTimeOffset.UtcNow.Add(1.Hours()));
            live.Parameters.AddWithValue("stale", DateTimeOffset.UtcNow.Subtract(1.Hours()));
            await live.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var sql = theDatabase.BatchedDeleteExpiredHandledEnvelopesSql(2)!;

        (await executeAsync(conn, sql, DateTimeOffset.UtcNow)).ShouldBe(2);
        (await executeAsync(conn, sql, DateTimeOffset.UtcNow)).ShouldBe(2);

        // The fifth expired row, and then nothing: the loop's short batch is what stops the reaper
        (await executeAsync(conn, sql, DateTimeOffset.UtcNow)).ShouldBe(1);
        (await executeAsync(conn, sql, DateTimeOffset.UtcNow)).ShouldBe(0);

        // The un-expired Handled row and the Incoming one are both still there
        await using var count = conn.CreateCommand();
        count.CommandText = $"select count(*) from {table}";
        (await count.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(2L);
    }

    [Fact]
    public async Task the_reaper_still_drains_every_expired_claim()
    {
        // The bound is per statement; DeleteExpiredAsync loops until a short batch. Nothing is left behind.
        var store = _host.GetRuntime().Storage.Deduplication;
        var expired = DateTimeOffset.UtcNow.Subtract(1.Hours());

        for (var i = 0; i < 5; i++)
        {
            await store.TryClaimAsync($"drain-{i}", expired, TestContext.Current.CancellationToken);
        }

        (await store.TryClaimAsync("drain-live", DateTimeOffset.UtcNow.Add(1.Hours()),
            TestContext.Current.CancellationToken)).ShouldBeTrue();

        (await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).ShouldBe(5);

        // ...and the live claim survived every pass
        (await store.TryClaimAsync("drain-live", DateTimeOffset.UtcNow.Add(1.Hours()),
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }
}
