using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
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
    ///     The session-level advisory lock that keeps two overlapping durability passes against the same
    ///     message schema from sending the same rows twice. Derived from the schema name so that two Wolverine
    ///     applications sharing one database with their own schemas do not skip each other's passes
    /// </summary>
    internal int DurabilityPassLockId => $"{SchemaName}:durability-pass".GetDeterministicHashCode();

    public async Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token)
    {
        // A dedicated connection with no transaction open on it for as long as the pass runs. See the
        // GH-3664 note in PollForScheduledMessagesAsync for why a long-held lock must not be transaction scoped
        var conn = await NpgsqlDataSource.OpenConnectionAsync(token);

        try
        {
            if (await conn.TryGetGlobalLock(DurabilityPassLockId, cancellation: token) == AttainLockResult.Success)
            {
                return new DurabilityPassLock(conn, DurabilityPassLockId);
            }
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }

        await conn.DisposeAsync();
        return null;
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

        public DurabilityPassLock(NpgsqlConnection conn, int lockId)
        {
            _conn = conn;
            _lockId = lockId;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _conn.ReleaseGlobalLock(_lockId, cancellation: CancellationToken.None);
            }
            finally
            {
                // Closing the session releases a session-level lock no matter what
                await _conn.DisposeAsync();
            }
        }
    }
}
