using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.SqlServer.Persistence;

/// <summary>
///     GH-5016. SQL Server's part in an externally triggered durability pass under
///     <see cref="DurabilityMode.ServerlessWithOutbox" />
/// </summary>
public partial class SqlServerMessageStore : IMessageStoreWithDurabilityPass, IStaleOutgoingSource,
    IDurabilityPassInboxSource
{
    /// <summary>
    ///     The session-owned application lock (sp_getapplock) that keeps two overlapping durability passes
    ///     against the same message schema from sending the same rows twice. Named for the schema so that two
    ///     Wolverine applications sharing one database with their own schemas do not skip each other's passes
    /// </summary>
    internal string DurabilityPassLockId => $"Wolverine:DurabilityPass:{SchemaName}";

    public async Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token)
    {
        // A dedicated connection with no transaction open on it for as long as the pass runs, so the lock is
        // owned by the session rather than by a transaction
        var conn = CreateConnection();

        try
        {
            await conn.OpenAsync(token);

            if (await conn.TryGetGlobalLock(DurabilityPassLockId, cancellation: token, lockTimeoutMs: 0))
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

        var builder = new SqlServerDbCommandBuilder();
        builder.Append("select top (");
        builder.AppendParameter(limit);
        builder.Append($") {DatabaseConstants.OutgoingFields} from {SchemaName}.{DatabaseConstants.OutgoingTable}");
        writeStaleOutgoingPredicate(builder, staleBefore, excluded);

        // Oldest first wherever the stale clock exists
        if (Durability.OutboxStaleTime.HasValue)
        {
            builder.Append($" order by {DatabaseConstants.Timestamp}");
        }

        await using var conn = CreateConnection();
        await conn.OpenAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        return await cmd.FetchListAsync(r => DatabasePersistence.ReadOutgoingAsync(r, token), token);
    }

    public Task<int> CountStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        var builder = new SqlServerDbCommandBuilder();
        builder.Append($"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}");
        writeStaleOutgoingPredicate(builder, staleBefore, excluded);

        return countAsync(builder, token);
    }

    public async Task RestartStaleOutgoingAsync(IReadOnlyList<Envelope> envelopes, CancellationToken token)
    {
        // The timestamp column only exists when OutboxStaleTime is set
        if (HasDisposed || envelopes.Count == 0 || !Durability.OutboxStaleTime.HasValue) return;

        var builder = new SqlServerDbCommandBuilder();
        builder.Append(
            $"update {SchemaName}.{DatabaseConstants.OutgoingTable} set {DatabaseConstants.Timestamp} = GETUTCDATE() where {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} and {DatabaseConstants.Id} in (");
        for (var i = 0; i < envelopes.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.AppendParameter(envelopes[i].Id);
        }

        builder.Append(")");

        await using var conn = CreateConnection();
        await conn.OpenAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;
        await cmd.ExecuteNonQueryAsync(token);
    }

    // owner_id = 0 [and timestamp <= @stale] [and id not in (@excluded)]
    private void writeStaleOutgoingPredicate(DbCommandBuilder builder, DateTimeOffset? staleBefore, Guid[] excluded)
    {
        builder.Append($" where {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode}");

        // The timestamp column only exists when OutboxStaleTime is set
        if (staleBefore.HasValue && Durability.OutboxStaleTime.HasValue)
        {
            builder.Append($" and {DatabaseConstants.Timestamp} <= ");
            builder.AppendParameter(staleBefore.Value);
        }

        writeExcluded(builder, excluded);
    }

    public Task<IReadOnlyList<Envelope>> LoadDueScheduledAsync(DateTimeOffset now, Guid[] excluded, int limit,
        CancellationToken token)
    {
        var builder = new SqlServerDbCommandBuilder();
        builder.Append("select top (");
        builder.AppendParameter(limit);
        builder.Append($") {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeDueScheduledPredicate(builder, now, excluded);
        builder.Append($" order by {DatabaseConstants.ExecutionTime}");

        return loadIncomingAsync(builder, token);
    }

    public Task<int> CountDueScheduledAsync(DateTimeOffset now, Guid[] excluded, CancellationToken token)
    {
        var builder = new SqlServerDbCommandBuilder();
        builder.Append($"select count(*) from {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeDueScheduledPredicate(builder, now, excluded);

        return countAsync(builder, token);
    }

    public Task<IReadOnlyList<Envelope>> LoadRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        int limit, CancellationToken token)
    {
        var builder = new SqlServerDbCommandBuilder();
        builder.Append("select top (");
        builder.AppendParameter(limit);
        builder.Append($") {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeRecoverableIncomingPredicate(builder, staleBefore, excluded);

        return loadIncomingAsync(builder, token);
    }

    public Task<int> CountRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        var builder = new SqlServerDbCommandBuilder();
        builder.Append($"select count(*) from {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeRecoverableIncomingPredicate(builder, staleBefore, excluded);

        return countAsync(builder, token);
    }

    // status = 'Scheduled' and execution_time <= @now [and id not in (@excluded)]
    private static void writeDueScheduledPredicate(DbCommandBuilder builder, DateTimeOffset now, Guid[] excluded)
    {
        builder.Append(
            $" where {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}' and {DatabaseConstants.ExecutionTime} <= ");
        builder.AppendParameter(now);
        writeExcluded(builder, excluded);
    }

    // status = 'Incoming' and (owner_id = 0 [or timestamp <= @stale]) [and id not in (@excluded)]
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

    // SQL Server has no array parameters, so one parameter per excluded id. Only rows that already failed in
    // this pass are excluded, and a step stops once there are more than DurabilityPassContext.MaxExcludedIds
    // of them, which keeps the list below the 2,100 parameter ceiling. See GH-5035
    private static void writeExcluded(DbCommandBuilder builder, Guid[] excluded)
    {
        if (excluded.Length == 0) return;

        builder.Append($" and {DatabaseConstants.Id} not in (");
        for (var i = 0; i < excluded.Length; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.AppendParameter(excluded[i]);
        }

        builder.Append(")");
    }

    private async Task<IReadOnlyList<Envelope>> loadIncomingAsync(DbCommandBuilder builder, CancellationToken token)
    {
        if (HasDisposed) return [];

        await using var conn = CreateConnection();
        await conn.OpenAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        var envelopes = await cmd.FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r, token), token);
        foreach (var envelope in envelopes) envelope.Store = this;

        return envelopes;
    }

    private async Task<int> countAsync(DbCommandBuilder builder, CancellationToken token)
    {
        if (HasDisposed) return 0;

        await using var conn = CreateConnection();
        await conn.OpenAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    private sealed class DurabilityPassLock : IAsyncDisposable
    {
        private readonly SqlConnection _conn;
        private readonly string _lockId;

        public DurabilityPassLock(SqlConnection conn, string lockId)
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
                // Ending the session releases a session-owned application lock no matter what
                await _conn.DisposeAsync();
            }
        }
    }
}
