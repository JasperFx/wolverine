using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.Sqlite;

/// <summary>
///     GH-5016. SQLite's part in an externally triggered durability pass under
///     <see cref="DurabilityMode.ServerlessWithOutbox" />
/// </summary>
internal partial class SqliteMessageStore : IMessageStoreWithDurabilityPass, IStaleOutgoingSource,
    IDurabilityPassInboxSource
{
    // The same text format as the datetime('now') column defaults, so both sides of a comparison agree
    private const string SqliteTimestampFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    ///     The row in the shared wolverine_locks table that keeps two overlapping durability passes against the
    ///     same table set from sending the same rows twice. Derived from the table prefix, the same way the
    ///     advisory lock ids are, so several prefixed table sets in one file do not skip each other's passes
    /// </summary>
    internal int DurabilityPassLockId => $"{SchemaName}:durability-pass".GetDeterministicHashCode();

    public async Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token)
    {
        await using var conn = (SqliteConnection)await DataSource.OpenConnectionAsync(token);

        // SQLite has no advisory locks, so this is a row, like SqliteAdvisoryLock. The lock table is only part
        // of a Main store's schema, and a pass can run against any store, so make sure it is there. The
        // definition matches the one in AllObjects() exactly, so migrations see no difference
        await conn.CreateCommand(
                "CREATE TABLE IF NOT EXISTS wolverine_locks (lock_id INTEGER PRIMARY KEY, acquired_at TEXT NOT NULL)")
            .ExecuteNonQueryAsync(token);

        // A row is not tied to a session the way a server-side lock is, so a pass that died mid-way would leave
        // it behind forever. Reap it once it is older than the advisory lock TTL, which is far longer than a pass
        await conn.CreateCommand("DELETE FROM wolverine_locks WHERE lock_id = @lockId AND acquired_at < @cutoff")
            .With("lockId", DurabilityPassLockId)
            .With("cutoff", DateTime.UtcNow.Subtract(SqliteAdvisoryLock.DefaultLockTtl).ToString(SqliteTimestampFormat))
            .ExecuteNonQueryAsync(token);

        var inserted = await conn
            .CreateCommand("INSERT OR IGNORE INTO wolverine_locks (lock_id, acquired_at) VALUES (@lockId, datetime('now'))")
            .With("lockId", DurabilityPassLockId)
            .ExecuteNonQueryAsync(token);

        return inserted > 0 ? new DurabilityPassLock(this, DurabilityPassLockId) : null;
    }

    public IReadOnlyList<IDurabilityPassStep> BuildDurabilityPassSteps(IWolverineRuntime runtime)
    {
        return
        [
            new RecoverStaleOutgoingStep(this, this, runtime.LoggerFactory.CreateLogger<RecoverStaleOutgoingStep>()),
            new ReleaseScheduledMessagesStep(this, this, runtime.LoggerFactory.CreateLogger<ReleaseScheduledMessagesStep>()),
            new RecoverIncomingMessagesStep(this, this, runtime.LoggerFactory.CreateLogger<RecoverIncomingMessagesStep>()),
            new DeleteExpiredRowsStep(this, runtime.LoggerFactory.CreateLogger<DeleteExpiredRowsStep>())
        ];
    }

    public async Task<IReadOnlyList<Envelope>> LoadStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        int limit, CancellationToken token)
    {
        if (HasDisposed) return [];

        var builder = ToCommandBuilder();
        builder.Append(
            $"select {DatabaseConstants.OutgoingFields} from {this.TableNameFor(DatabaseConstants.OutgoingTable)}");
        writeStaleOutgoingPredicate(builder, staleBefore, excluded);

        // Oldest first wherever the stale clock exists
        if (Durability.OutboxStaleTime.HasValue)
        {
            builder.Append($" order by {DatabaseConstants.Timestamp}");
        }

        builder.Append($" limit {limit}");

        await using var conn = await DataSource.OpenConnectionAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        return await cmd.FetchListAsync(r => DatabasePersistence.ReadOutgoingAsync(r, token), token);
    }

    public Task<int> CountStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        var builder = ToCommandBuilder();
        builder.Append($"select count(*) from {this.TableNameFor(DatabaseConstants.OutgoingTable)}");
        writeStaleOutgoingPredicate(builder, staleBefore, excluded);

        return countAsync(builder, token);
    }

    // owner_id = 0 [and timestamp <= @stale] [and id not in (...)]
    private void writeStaleOutgoingPredicate(DbCommandBuilder builder, DateTimeOffset? staleBefore, Guid[] excluded)
    {
        builder.Append($" where {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode}");

        // The timestamp column only exists when OutboxStaleTime is set
        if (staleBefore.HasValue && Durability.OutboxStaleTime.HasValue)
        {
            appendTimestampNotAfter(builder, staleBefore.Value);
        }

        writeExcluded(builder, excluded);
    }

    public Task<IReadOnlyList<Envelope>> LoadDueScheduledAsync(DateTimeOffset now, Guid[] excluded, int limit,
        CancellationToken token)
    {
        var builder = ToCommandBuilder();
        builder.Append(
            $"select {DatabaseConstants.IncomingFields} from {this.TableNameFor(DatabaseConstants.IncomingTable)}");
        writeDueScheduledPredicate(builder, now, excluded);
        builder.Append($" order by {DatabaseConstants.ExecutionTime} limit {limit}");

        return loadIncomingAsync(builder, token);
    }

    public Task<int> CountDueScheduledAsync(DateTimeOffset now, Guid[] excluded, CancellationToken token)
    {
        var builder = ToCommandBuilder();
        builder.Append($"select count(*) from {this.TableNameFor(DatabaseConstants.IncomingTable)}");
        writeDueScheduledPredicate(builder, now, excluded);

        return countAsync(builder, token);
    }

    public Task<IReadOnlyList<Envelope>> LoadRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        int limit, CancellationToken token)
    {
        var builder = ToCommandBuilder();
        builder.Append(
            $"select {DatabaseConstants.IncomingFields} from {this.TableNameFor(DatabaseConstants.IncomingTable)}");
        writeRecoverableIncomingPredicate(builder, staleBefore, excluded);
        builder.Append($" limit {limit}");

        return loadIncomingAsync(builder, token);
    }

    public Task<int> CountRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        var builder = ToCommandBuilder();
        builder.Append($"select count(*) from {this.TableNameFor(DatabaseConstants.IncomingTable)}");
        writeRecoverableIncomingPredicate(builder, staleBefore, excluded);

        return countAsync(builder, token);
    }

    // status = 'Scheduled' and execution_time <= @now [and id not in (...)]
    private static void writeDueScheduledPredicate(DbCommandBuilder builder, DateTimeOffset now, Guid[] excluded)
    {
        builder.Append($" where {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}'");
        appendNotAfter(builder, DatabaseConstants.ExecutionTime, now);
        writeExcluded(builder, excluded);
    }

    // status = 'Incoming' and (timestamp <= @stale | owner_id = 0) [and id not in (...)]
    private void writeRecoverableIncomingPredicate(DbCommandBuilder builder, DateTimeOffset? staleBefore,
        Guid[] excluded)
    {
        builder.Append($" where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'");

        // The timestamp column only exists when InboxStaleTime is set
        if (staleBefore.HasValue && Durability.InboxStaleTime.HasValue)
        {
            appendTimestampNotAfter(builder, staleBefore.Value);
        }
        else
        {
            builder.Append($" and {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode}");
        }

        writeExcluded(builder, excluded);
    }

    // execution_time is TEXT written from a bound DateTimeOffset, so both sides go through datetime() the way
    // WriteLoadScheduledEnvelopeSql does, which compares instants rather than text formats
    private static void appendNotAfter(DbCommandBuilder builder, string column, DateTimeOffset value)
    {
        builder.Append($" and datetime({column}) <= datetime(");
        builder.AppendParameter(value.UtcDateTime.ToString(SqliteTimestampFormat));
        builder.Append(")");
    }

    // The timestamp columns are only ever written by datetime('now'), so they are already in
    // SqliteTimestampFormat and compare correctly as text. Leaving the column bare is what lets the opt-in
    // outgoing timestamp index serve the stale predicate
    private static void appendTimestampNotAfter(DbCommandBuilder builder, DateTimeOffset value)
    {
        builder.Append($" and {DatabaseConstants.Timestamp} <= ");
        builder.AppendParameter(value.UtcDateTime.ToString(SqliteTimestampFormat));
    }

    // Ids are TEXT and Microsoft.Data.Sqlite binds a Guid as a BLOB, so they go inline in the lower-case "D"
    // format the lower(id) comparisons in this store already rely on. Only rows that already failed in this
    // pass are excluded, so the list stays short
    private static void writeExcluded(DbCommandBuilder builder, Guid[] excluded)
    {
        if (excluded.Length == 0) return;

        builder.Append(
            $" and lower({DatabaseConstants.Id}) not in ({excluded.Select(x => $"'{x:D}'").Join(", ")})");
    }

    private async Task<IReadOnlyList<Envelope>> loadIncomingAsync(DbCommandBuilder builder, CancellationToken token)
    {
        if (HasDisposed) return [];

        await using var conn = await DataSource.OpenConnectionAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        var envelopes = await cmd.FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r, token), token);
        foreach (var envelope in envelopes) envelope.Store = this;

        return envelopes;
    }

    private async Task<int> countAsync(DbCommandBuilder builder, CancellationToken token)
    {
        if (HasDisposed) return 0;

        await using var conn = await DataSource.OpenConnectionAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    private sealed class DurabilityPassLock : IAsyncDisposable
    {
        private readonly SqliteMessageStore _store;
        private readonly int _lockId;

        public DurabilityPassLock(SqliteMessageStore store, int lockId)
        {
            _store = store;
            _lockId = lockId;
        }

        public async ValueTask DisposeAsync()
        {
            // If this fails the row is left to the TTL sweep in TryAcquireDurabilityPassLockAsync
            await using var conn = await _store.DataSource.OpenConnectionAsync(CancellationToken.None);
            await conn.CreateCommand("DELETE FROM wolverine_locks WHERE lock_id = @lockId")
                .With("lockId", _lockId)
                .ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
