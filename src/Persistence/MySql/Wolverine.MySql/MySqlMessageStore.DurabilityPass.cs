using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.MySql;

/// <summary>
///     GH-5016. MySQL's part in an externally triggered durability pass under
///     <see cref="DurabilityMode.ServerlessWithOutbox" />
/// </summary>
internal partial class MySqlMessageStore : IMessageStoreWithDurabilityPass, IStaleOutgoingSource,
    IDurabilityPassInboxSource
{
    /// <summary>
    ///     The session-scoped named lock (GET_LOCK) that keeps two overlapping durability passes against the same
    ///     message schema from sending the same rows twice. Named for the schema so that two Wolverine applications
    ///     sharing one server with their own schemas do not skip each other's passes. MySQL caps lock names at 64
    ///     characters, so a long schema name is hashed instead
    /// </summary>
    internal string DurabilityPassLockName
    {
        get
        {
            var name = $"wolverine_pass_{SchemaName}";
            if (name.Length <= 64) return name;

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SchemaName)));
            return $"wolverine_pass_{hash[..32]}";
        }
    }

    public async Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token)
    {
        // A dedicated connection for as long as the pass runs. Named locks belong to the session, and a
        // second GET_LOCK on the same session would succeed, so it cannot be a pooled connection shared
        // with anything else
        var conn = MySqlDataSource.CreateConnection();

        try
        {
            await conn.OpenAsync(token);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT GET_LOCK(@lockName, 0)";
            cmd.Parameters.AddWithValue("@lockName", DurabilityPassLockName);

            // 1 when taken, 0 when another session holds it, NULL on error
            var result = await cmd.ExecuteScalarAsync(token);
            if (result is not null and not DBNull && Convert.ToInt64(result) == 1)
            {
                return new DurabilityPassLock(conn, DurabilityPassLockName);
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

        var builder = new DbCommandBuilder(new MySqlCommand());
        builder.Append($"SELECT {DatabaseConstants.OutgoingFields} FROM {SchemaName}.{DatabaseConstants.OutgoingTable}");
        writeStaleOutgoingPredicate(builder, staleBefore, excluded);

        // Oldest first wherever the stale clock exists
        if (Durability.OutboxStaleTime.HasValue)
        {
            builder.Append($" ORDER BY {DatabaseConstants.Timestamp}");
        }

        builder.Append(" LIMIT ");
        builder.AppendParameter(limit);

        await using var conn = CreateConnection();
        await conn.OpenAsync(token);
        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        return await cmd.FetchListAsync(r => DatabasePersistence.ReadOutgoingAsync(r, token), token);
    }

    public Task<int> CountStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        var builder = new DbCommandBuilder(new MySqlCommand());
        builder.Append($"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.OutgoingTable}");
        writeStaleOutgoingPredicate(builder, staleBefore, excluded);

        return countAsync(builder, token);
    }

    public async Task RestartStaleOutgoingAsync(IReadOnlyList<Envelope> envelopes, CancellationToken token)
    {
        // The timestamp column only exists when OutboxStaleTime is set
        if (HasDisposed || envelopes.Count == 0 || !Durability.OutboxStaleTime.HasValue) return;

        var builder = new DbCommandBuilder(new MySqlCommand());
        builder.Append(
            $"UPDATE {SchemaName}.{DatabaseConstants.OutgoingTable} SET {DatabaseConstants.Timestamp} = UTC_TIMESTAMP(6) WHERE {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} AND {DatabaseConstants.Id} IN (");
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
        builder.Append($" WHERE {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode}");

        // The timestamp column only exists when OutboxStaleTime is set
        if (staleBefore.HasValue && Durability.OutboxStaleTime.HasValue)
        {
            builder.Append($" AND {DatabaseConstants.Timestamp} <= ");
            builder.AppendParameter(staleBefore.Value);
        }

        writeExcluded(builder, excluded);
    }

    public Task<IReadOnlyList<Envelope>> LoadDueScheduledAsync(DateTimeOffset now, Guid[] excluded, int limit,
        CancellationToken token)
    {
        var builder = new DbCommandBuilder(new MySqlCommand());
        builder.Append($"SELECT {DatabaseConstants.IncomingFields} FROM {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeDueScheduledPredicate(builder, now, excluded);
        builder.Append($" ORDER BY {DatabaseConstants.ExecutionTime} LIMIT ");
        builder.AppendParameter(limit);

        return loadIncomingAsync(builder, token);
    }

    public Task<int> CountDueScheduledAsync(DateTimeOffset now, Guid[] excluded, CancellationToken token)
    {
        var builder = new DbCommandBuilder(new MySqlCommand());
        builder.Append($"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeDueScheduledPredicate(builder, now, excluded);

        return countAsync(builder, token);
    }

    public Task<IReadOnlyList<Envelope>> LoadRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        int limit, CancellationToken token)
    {
        var builder = new DbCommandBuilder(new MySqlCommand());
        builder.Append($"SELECT {DatabaseConstants.IncomingFields} FROM {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeRecoverableIncomingPredicate(builder, staleBefore, excluded);
        builder.Append(" LIMIT ");
        builder.AppendParameter(limit);

        return loadIncomingAsync(builder, token);
    }

    public Task<int> CountRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        var builder = new DbCommandBuilder(new MySqlCommand());
        builder.Append($"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeRecoverableIncomingPredicate(builder, staleBefore, excluded);

        return countAsync(builder, token);
    }

    // status = 'Scheduled' and execution_time <= @now [and id not in (@excluded)]
    private static void writeDueScheduledPredicate(DbCommandBuilder builder, DateTimeOffset now, Guid[] excluded)
    {
        builder.Append(
            $" WHERE {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}' AND {DatabaseConstants.ExecutionTime} <= ");
        builder.AppendParameter(now);
        writeExcluded(builder, excluded);
    }

    // status = 'Incoming' and (owner_id = 0 [or timestamp <= @stale]) [and id not in (@excluded)]
    private void writeRecoverableIncomingPredicate(DbCommandBuilder builder, DateTimeOffset? staleBefore,
        Guid[] excluded)
    {
        builder.Append($" WHERE {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'");

        // The timestamp column only exists when InboxStaleTime is set. A row owned by any node was never claimed
        // by a live request, so it is recoverable at once, and a row a node claimed is once its stale clock runs out
        if (staleBefore.HasValue && Durability.InboxStaleTime.HasValue)
        {
            builder.Append(
                $" AND ({DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} OR {DatabaseConstants.Timestamp} <= ");
            builder.AppendParameter(staleBefore.Value);
            builder.Append(")");
        }
        else
        {
            builder.Append($" AND {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode}");
        }

        writeExcluded(builder, excluded);
    }

    // No array parameters in MySQL, so one parameter per excluded id. Only rows that already failed in this
    // pass are excluded, which keeps the list tiny next to the 65,535 placeholder ceiling
    private static void writeExcluded(DbCommandBuilder builder, Guid[] excluded)
    {
        if (excluded.Length == 0) return;

        builder.Append($" AND {DatabaseConstants.Id} NOT IN (");
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
        private readonly MySqlConnection _conn;
        private readonly string _lockName;

        public DurabilityPassLock(MySqlConnection conn, string lockName)
        {
            _conn = conn;
            _lockName = lockName;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT RELEASE_LOCK(@lockName)";
                cmd.Parameters.AddWithValue("@lockName", _lockName);
                await cmd.ExecuteScalarAsync(CancellationToken.None);
            }
            finally
            {
                // Ending the session releases a named lock no matter what
                await _conn.DisposeAsync();
            }
        }
    }
}
