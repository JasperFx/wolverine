using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using System.Transactions;
using Npgsql;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql.Util;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.Postgresql;

/// <summary>
///     GH-5016. PostgreSQL's part in an externally triggered durability pass under
///     <see cref="DurabilityMode.ServerlessWithOutbox" />
/// </summary>
internal partial class PostgresqlMessageStore : IMessageStoreWithDurabilityPass, IStaleOutgoingSource,
    IDurabilityPassInboxSource
{
    /// <summary>
    ///     The advisory lock that keeps two overlapping durability passes against the same message schema from
    ///     sending the same rows twice. Derived from the schema name so that two Wolverine applications sharing
    ///     one database with their own schemas do not skip each other's passes
    /// </summary>
    internal int DurabilityPassLockId => $"{SchemaName}:durability-pass".GetDeterministicHashCode();

    public bool SupportsTransactionScopedDurabilityPassLock => true;

    public async Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token)
    {
        NpgsqlConnection conn;

        // The lock connection never joins an ambient System.Transactions scope. With Enlist=true it would, and
        // BeginTransactionAsync below then throws because a transaction is already in progress. Nor should the
        // session lock's lifetime depend on whether somebody else's scope commits
        using (new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            conn = await NpgsqlDataSource.OpenConnectionAsync(token);
        }

        NpgsqlTransaction? tx = null;

        try
        {
            if (Durability.UseTransactionScopedDurabilityPassLock)
            {
                // Behind a transaction-mode pooler only a transaction keeps this client on one server connection,
                // so the lock is taken and released inside one. This is the trade-off the GH-3664 note in
                // PollForScheduledMessagesAsync warns about, which is why it is opt in
                tx = await conn.BeginTransactionAsync(token);

                // A server-side idle_in_transaction_session_timeout shorter than the pass would end this
                // transaction, and release the lock, while the pass is still sending. SET LOCAL lasts only as long
                // as this transaction, so it is safe behind a transaction-mode pooler. No keepalive query instead:
                // see the GH-3664 note, a statement on this session while it holds the lock is what makes Marten's
                // gap detection treat it as a live reserver
                await using (var cmd = conn.CreateCommand(
                                 "select set_config('idle_in_transaction_session_timeout', '0', true)"))
                {
                    cmd.Transaction = tx;
                    await cmd.ExecuteNonQueryAsync(token);
                }

                if (await tx.TryGetGlobalTxLock(DurabilityPassLockId, token) == AttainLockResult.Success)
                {
                    return new TransactionScopedDurabilityPassLock(conn, tx, DurabilityPassLockId);
                }
            }
            // A dedicated connection with no transaction open on it for as long as the pass runs. See the
            // GH-3664 note in PollForScheduledMessagesAsync for why a long-held lock must not be transaction scoped
            else if (await conn.TryGetGlobalLock(DurabilityPassLockId, cancellation: token) == AttainLockResult.Success)
            {
                return new DurabilityPassLock(conn, DurabilityPassLockId, Logger);
            }
        }
        catch
        {
            await closeLockConnectionAsync(conn, tx);
            throw;
        }

        await closeLockConnectionAsync(conn, tx);
        return null;
    }

    // Ends the lock transaction, if there is one, and closes the lock connection, even when the rollback or the
    // transaction's own disposal fails
    private static async Task closeLockConnectionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx)
    {
        try
        {
            if (tx != null)
            {
                await rollBackLockTransactionAsync(conn, tx);
            }
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    /// <returns>The rollback failure, if there was one</returns>
    private static async Task<Exception?> rollBackLockTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        Exception? failure = null;

        try
        {
            // Nothing was written in this transaction, it only holds the lock
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            failure = e;

            // Returned to the pool as it is, the connection would carry the open transaction, and the lock, until
            // Npgsql's deferred reset ran on its next use -- which in a function instance may be never. Behind a
            // pooler that keeps the server connection pinned in the transaction and every later pass skips the
            // store. Clearing the pool makes Npgsql close this physical connection when it is returned instead
            NpgsqlConnection.ClearPool(conn);
        }
        finally
        {
            try
            {
                await tx.DisposeAsync();
            }
            catch (Exception e)
            {
                failure ??= e;
            }
        }

        return failure;
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

        var builder = new DbCommandBuilder(new NpgsqlCommand());
        builder.Append($"select {DatabaseConstants.OutgoingFields} from {QuotedSchemaName}.{DatabaseConstants.OutgoingTable}");
        writeStaleOutgoingPredicate(builder, staleBefore, excluded);

        // Oldest first wherever the stale clock exists
        if (Durability.OutboxStaleTime.HasValue)
        {
            builder.Append($" order by {DatabaseConstants.Timestamp}");
        }

        builder.Append(" limit ");
        builder.AppendParameter(limit);

        await using var conn = await NpgsqlDataSource.OpenConnectionAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        return await cmd.FetchListAsync(r => DatabasePersistence.ReadOutgoingAsync(r, token), token);
    }

    public async Task<int> CountStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        if (HasDisposed) return 0;

        var builder = new DbCommandBuilder(new NpgsqlCommand());
        builder.Append($"select count(*) from {QuotedSchemaName}.{DatabaseConstants.OutgoingTable}");
        writeStaleOutgoingPredicate(builder, staleBefore, excluded);

        await using var conn = await NpgsqlDataSource.OpenConnectionAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        var count = await cmd.ExecuteScalarAsync(token);
        return Convert.ToInt32(count);
    }

    public async Task RestartStaleOutgoingAsync(IReadOnlyList<Envelope> envelopes, CancellationToken token)
    {
        // The timestamp column only exists when OutboxStaleTime is set
        if (HasDisposed || envelopes.Count == 0 || !Durability.OutboxStaleTime.HasValue) return;

        var builder = new DbCommandBuilder(new NpgsqlCommand());
        builder.Append(
            $"update {QuotedSchemaName}.{DatabaseConstants.OutgoingTable} set {DatabaseConstants.Timestamp} = clock_timestamp() where {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} and {DatabaseConstants.Id} = any(");
        builder.AppendParameter(envelopes.Select(x => x.Id).ToArray());
        builder.Append(")");

        await using var conn = await NpgsqlDataSource.OpenConnectionAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;
        await cmd.ExecuteNonQueryAsync(token);
    }

    // owner_id = 0 [and timestamp <= @stale] [and not (id = any(@excluded))]
    private void writeStaleOutgoingPredicate(DbCommandBuilder builder, DateTimeOffset? staleBefore, Guid[] excluded)
    {
        builder.Append($" where {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode}");

        // The timestamp column only exists when OutboxStaleTime is set
        if (staleBefore.HasValue && Durability.OutboxStaleTime.HasValue)
        {
            builder.Append($" and {DatabaseConstants.Timestamp} <= ");
            builder.AppendParameter(staleBefore.Value);
        }

        if (excluded.Length > 0)
        {
            builder.Append($" and not ({DatabaseConstants.Id} = any(");
            builder.AppendParameter(excluded);
            builder.Append("))");
        }
    }

    public Task<IReadOnlyList<Envelope>> LoadDueScheduledAsync(DateTimeOffset now, Guid[] excluded, int limit,
        CancellationToken token)
    {
        var builder = new DbCommandBuilder(new NpgsqlCommand());
        builder.Append($"select {DatabaseConstants.IncomingFields} from {QuotedSchemaName}.{DatabaseConstants.IncomingTable}");
        writeDueScheduledPredicate(builder, now, excluded);
        builder.Append($" order by {DatabaseConstants.ExecutionTime} limit ");
        builder.AppendParameter(limit);

        return loadIncomingAsync(builder, token);
    }

    public Task<int> CountDueScheduledAsync(DateTimeOffset now, Guid[] excluded, CancellationToken token)
    {
        var builder = new DbCommandBuilder(new NpgsqlCommand());
        builder.Append($"select count(*) from {QuotedSchemaName}.{DatabaseConstants.IncomingTable}");
        writeDueScheduledPredicate(builder, now, excluded);

        return countAsync(builder, token);
    }

    public Task<IReadOnlyList<Envelope>> LoadRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        int limit, CancellationToken token)
    {
        var builder = new DbCommandBuilder(new NpgsqlCommand());
        builder.Append($"select {DatabaseConstants.IncomingFields} from {QuotedSchemaName}.{DatabaseConstants.IncomingTable}");
        writeRecoverableIncomingPredicate(builder, staleBefore, excluded);
        builder.Append(" limit ");
        builder.AppendParameter(limit);

        return loadIncomingAsync(builder, token);
    }

    public Task<int> CountRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        var builder = new DbCommandBuilder(new NpgsqlCommand());
        builder.Append($"select count(*) from {QuotedSchemaName}.{DatabaseConstants.IncomingTable}");
        writeRecoverableIncomingPredicate(builder, staleBefore, excluded);

        return countAsync(builder, token);
    }

    // status = 'Scheduled' and execution_time <= @now [and not (id = any(@excluded))]
    private static void writeDueScheduledPredicate(DbCommandBuilder builder, DateTimeOffset now, Guid[] excluded)
    {
        builder.Append(
            $" where {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}' and {DatabaseConstants.ExecutionTime} <= ");
        builder.AppendParameter(now);
        writeExcluded(builder, excluded);
    }

    // status = 'Incoming' and (owner_id = 0 [or timestamp <= @stale]) [and not (id = any(@excluded))]
    private void writeRecoverableIncomingPredicate(DbCommandBuilder builder, DateTimeOffset? staleBefore,
        Guid[] excluded)
    {
        builder.Append($" where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'");

        // The timestamp column only exists when InboxStaleTime is set. A row owned by any node was never claimed
        // by a live request, so it is recoverable at once, and a row a node claimed is once its stale clock runs out
        if (staleBefore.HasValue && Durability.InboxStaleTime.HasValue)
        {
            builder.Append(
                $" and ({DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} or {DatabaseConstants.Timestamp} <= ");
            builder.AppendParameter(staleBefore.Value);
            builder.Append(")");
        }
        else
        {
            builder.Append($" and {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode}");
        }

        writeExcluded(builder, excluded);
    }

    private static void writeExcluded(DbCommandBuilder builder, Guid[] excluded)
    {
        if (excluded.Length == 0) return;

        builder.Append($" and not ({DatabaseConstants.Id} = any(");
        builder.AppendParameter(excluded);
        builder.Append("))");
    }

    private async Task<IReadOnlyList<Envelope>> loadIncomingAsync(DbCommandBuilder builder, CancellationToken token)
    {
        if (HasDisposed) return [];

        await using var conn = await NpgsqlDataSource.OpenConnectionAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        var envelopes = await cmd.FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r, token), token);
        foreach (var envelope in envelopes) envelope.Store = this;

        return envelopes;
    }

    private async Task<int> countAsync(DbCommandBuilder builder, CancellationToken token)
    {
        if (HasDisposed) return 0;

        await using var conn = await NpgsqlDataSource.OpenConnectionAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    private sealed class DurabilityPassLock : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        private readonly int _lockId;
        private readonly ILogger _logger;

        public DurabilityPassLock(NpgsqlConnection conn, int lockId, ILogger logger)
        {
            _conn = conn;
            _lockId = lockId;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = _conn.CreateCommand("select pg_advisory_unlock(:id)").With("id", _lockId);
                if (await cmd.ExecuteScalarAsync(CancellationToken.None) is false)
                {
                    // The server connection this unlock reached does not hold the lock, so the one that does keeps
                    // it. That is what a pooler in transaction mode does to a session-level lock
                    _logger.LogWarning(
                        "The durability pass lock {LockId} was not held by the database session that tried to release it. If this application reaches PostgreSQL through a connection pooler in transaction mode (PgBouncer, Cloud SQL managed connection pooling), session-level locks are not reliable there: set DurabilitySettings.UseTransactionScopedDurabilityPassLock to true",
                        _lockId);
                }
            }
            finally
            {
                // Closing the session releases a session-level lock no matter what
                await _conn.DisposeAsync();
            }
        }
    }

    /// <summary>
    ///     GH-5279. See <see cref="DurabilitySettings.UseTransactionScopedDurabilityPassLock" />. Ending the
    ///     transaction releases the lock on the same server connection that took it, even behind a
    ///     transaction-mode pooler
    /// </summary>
    private sealed class TransactionScopedDurabilityPassLock : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        private readonly NpgsqlTransaction _tx;
        private readonly int _lockId;

        public TransactionScopedDurabilityPassLock(NpgsqlConnection conn, NpgsqlTransaction tx, int lockId)
        {
            _conn = conn;
            _tx = tx;
            _lockId = lockId;
        }

        public async ValueTask DisposeAsync()
        {
            Exception? failure;
            try
            {
                failure = await rollBackLockTransactionAsync(_conn, _tx);
            }
            finally
            {
                await _conn.DisposeAsync();
            }

            if (failure != null)
            {
                // The transaction holding the lock was already gone, so the lock may have been released before
                // the pass ended and another pass could have overlapped this one
                throw new InvalidOperationException(
                    $"The transaction holding the durability pass lock {_lockId} ended before the pass did, so the lock may have been released early and another pass may have run against this store at the same time. If this happens regularly, check that PgBouncer's idle_transaction_timeout (or anything else that ends idle transactions) is longer than DurabilityPassOptions.MaxDuration",
                    failure);
            }
        }
    }
}
