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

    /// <summary>
    ///     How old the pass lock row may get before another pass reaps it as abandoned. The holder renews it
    ///     every quarter of this while its pass runs, so only a pass that died is ever reaped
    /// </summary>
    internal TimeSpan DurabilityPassLockTtl { get; set; } = SqliteAdvisoryLock.DefaultLockTtl;

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
        // it behind forever. Reap it once it is older than the TTL. A live holder keeps renewing its row, so
        // this never takes the lock away from a pass that is still running. See GH-5035
        await conn.CreateCommand("DELETE FROM wolverine_locks WHERE lock_id = @lockId AND acquired_at < @cutoff")
            .With("lockId", DurabilityPassLockId)
            .With("cutoff", DateTime.UtcNow.Subtract(DurabilityPassLockTtl).ToString(SqliteTimestampFormat))
            .ExecuteNonQueryAsync(token);

        var passLock = new DurabilityPassLock(this, DurabilityPassLockId, DurabilityPassLockTtl);

        var inserted = await conn
            .CreateCommand("INSERT OR IGNORE INTO wolverine_locks (lock_id, acquired_at) VALUES (@lockId, @value)")
            .With("lockId", DurabilityPassLockId)
            .With("value", passLock.Value)
            .ExecuteNonQueryAsync(token);

        if (inserted == 0) return null;

        passLock.StartRenewing();
        return passLock;
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

    // status = 'Incoming' and (owner_id = 0 [or timestamp <= @stale]) [and id not in (...)]
    private void writeRecoverableIncomingPredicate(DbCommandBuilder builder, DateTimeOffset? staleBefore,
        Guid[] excluded)
    {
        builder.Append($" where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'");

        // The timestamp column only exists when InboxStaleTime is set. A row owned by any node was never claimed
        // by a live request, so it is recoverable at once, and a row a node claimed is once its stale clock runs out
        if (staleBefore.HasValue && Durability.InboxStaleTime.HasValue)
        {
            builder.Append($" and ({DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} or {DatabaseConstants.Timestamp} <= ");
            builder.AppendParameter(staleBefore.Value.UtcDateTime.ToString(SqliteTimestampFormat));
            builder.Append(")");
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
    // pass are excluded, at most DurabilityPassContext.MaxExcludedIds of them
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

    /// <summary>
    ///     GH-5035. The table has no owner column, so the owner is in <c>acquired_at</c>: the time in the same
    ///     text format the reap compares against, then a token unique to this acquisition. Renewal and release
    ///     only touch the row while it still carries this acquisition's value, so a holder whose row was reaped
    ///     can neither revive it nor delete the row of the pass that took the lock after it
    /// </summary>
    private sealed class DurabilityPassLock : IAsyncDisposable
    {
        private readonly SqliteMessageStore _store;
        private readonly int _lockId;
        private readonly TimeSpan _ttl;
        private readonly string _owner = Guid.NewGuid().ToString("N");
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _cancellation = new();
        private Task? _renewing;

        public DurabilityPassLock(SqliteMessageStore store, int lockId, TimeSpan ttl)
        {
            _store = store;
            _lockId = lockId;
            _ttl = ttl;
            Value = nextValue();
        }

        public string Value { get; private set; }

        private string nextValue()
        {
            return $"{DateTime.UtcNow.ToString(SqliteTimestampFormat)} {_owner}";
        }

        public void StartRenewing()
        {
            _renewing = Task.Run(renewAsync);
        }

        private async Task renewAsync()
        {
            var interval = _ttl / 4;
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, _cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await _gate.WaitAsync();
                try
                {
                    if (_cancellation.IsCancellationRequested) return;

                    var renewed = nextValue();
                    await using var conn = await _store.DataSource.OpenConnectionAsync(_cancellation.Token);
                    var updated = await conn
                        .CreateCommand(
                            "UPDATE wolverine_locks SET acquired_at = @renewed WHERE lock_id = @lockId AND acquired_at = @value")
                        .With("renewed", renewed)
                        .With("lockId", _lockId)
                        .With("value", Value)
                        .ExecuteNonQueryAsync(_cancellation.Token);

                    if (updated == 0)
                    {
                        // Reaped, so another pass may hold the lock now. Nothing more to renew
                        _store.Logger.LogWarning(
                            "Lost the durability pass lock for message store {Store} while the pass was still running",
                            _store.Uri);
                        return;
                    }

                    Value = renewed;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    // Try again on the next tick, well inside the TTL
                    _store.Logger.LogError(e, "Unable to renew the durability pass lock for message store {Store}",
                        _store.Uri);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();

            // The renewal loop catches everything, and it has to be done before the row goes
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            if (_renewing != null) await _renewing;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

            await _gate.WaitAsync();
            try
            {
                // If this fails the row is left to the TTL sweep in TryAcquireDurabilityPassLockAsync
                await using var conn = await _store.DataSource.OpenConnectionAsync(CancellationToken.None);
                await conn.CreateCommand("DELETE FROM wolverine_locks WHERE lock_id = @lockId AND acquired_at = @value")
                    .With("lockId", _lockId)
                    .With("value", Value)
                    .ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally
            {
                _gate.Release();
                _cancellation.Dispose();
            }
        }
    }
}
