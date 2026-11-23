using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.SqlServer.Transport.NServiceBus;

internal class NServiceBusSqlServerQueueListener : DatabaseListener
{
    private readonly NServiceBusSqlServerQueue _queue;
    private readonly string _connectionString;
    private readonly NServiceBusSqlServerEnvelopeMapper _mapper;
    private readonly NServiceBusSqlServerQueueSender _sender;
    private readonly string _receiveSql;
    private readonly TimeSpan _reinsertBudget;
    private readonly CancellationToken _hostCancellation;

    public NServiceBusSqlServerQueueListener(NServiceBusSqlServerQueue queue, IWolverineRuntime runtime,
        IReceiver receiver)
        : base(queue.Uri, queue.Name, receiver,
            runtime.LoggerFactory.CreateLogger<NServiceBusSqlServerQueueListener>(),
            queue.PollingInterval ?? runtime.DurabilitySettings.ScheduledJobPollingTime)
    {
        _queue = queue;
        _connectionString = queue.Parent.Settings.ConnectionString!;
        _mapper = queue.BuildMapper(runtime);
        _sender = new NServiceBusSqlServerQueueSender(queue, runtime);
        _reinsertBudget = runtime.DurabilitySettings.DrainTimeout;
        _hostCancellation = runtime.Cancellation;

        // Destructive competing-consumer pop honoring NServiceBus FIFO-by-RowVersion ordering.
        _receiveSql = $@"
WITH message AS (
    SELECT TOP(@count) Id, Headers, Body
    FROM {queue.TableIdentifier} WITH (UPDLOCK, READPAST, ROWLOCK)
    ORDER BY RowVersion)
DELETE FROM message
OUTPUT deleted.Id, deleted.Headers, deleted.Body;";
    }

    // GH-5815. A receive transaction holds one message at a time
    protected override int MaximumMessagesToReceive => _queue.ReceivesInTransaction ? 1 : _queue.MaximumMessagesToReceive;

    public override async ValueTask CompleteAsync(Envelope envelope)
    {
        // GH-5815. Committing the receive transaction deletes the row for good. Without one, the row was already
        // removed by the destructive pop.
        await commitReceiveAsync(envelope);
    }

    public override async ValueTask DeferAsync(Envelope envelope)
    {
        // GH-5815. Rolling back the receive transaction puts the row back as it was, RowVersion and all, so the
        // message keeps its place at the head of the queue
        if (await rollbackReceiveAsync(envelope)) return;

        // Re-expose the message by writing it back to the NServiceBus queue table. The pop already deleted the
        // row, so this copy is the only one there will be: retry it, and fail loudly if it never goes in
        // (GH-5775). The copy goes to the back of the queue: RowVersion is an IDENTITY column and cannot be
        // written back without IDENTITY_INSERT, which needs ALTER permission on the table.
        var sqlServerEnvelope = envelope as NServiceBusSqlServerEnvelope;

        await reinsertWithRetriesAsync(envelope, attempt =>
        {
            // Any attempt after the first one (in this deferral or an earlier one of the same envelope) may
            // follow an insert that committed while reporting a failure
            var retry = attempt > 1 || sqlServerEnvelope is { ReinsertAttempted: true };
            if (sqlServerEnvelope != null) sqlServerEnvelope.ReinsertAttempted = true;

            return _sender.ReinsertAsync(envelope, retry);
        }, _reinsertBudget, _hostCancellation);
    }

    protected override async Task<IReadOnlyList<Envelope>> TryPopAsync(int count, CancellationToken token)
    {
        if (_queue.ReceivesInTransaction) return await popInReceiveTransactionAsync(token);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(token);

        try
        {
            return await conn.CreateCommand(_receiveSql)
                .With("count", count)
                .FetchListAsync(async reader =>
                {
                    var id = await reader.GetFieldValueAsync<Guid>(0, token);
                    var headers = await reader.IsDBNullAsync(1, token)
                        ? null
                        : await reader.GetFieldValueAsync<string>(1, token);
                    var body = await reader.IsDBNullAsync(2, token)
                        ? Array.Empty<byte>()
                        : await reader.GetFieldValueAsync<byte[]>(2, token);

                    return _mapper.MapIncoming(id, headers, body);
                }, cancellation: token);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// GH-5815. The same pop, of one message, inside a transaction left open until the message is settled. The
    /// deleted row stays exclusively locked, so the READPAST of every other consumer skips it, and a rollback
    /// (or the session dying) restores it with its original RowVersion.
    /// </summary>
    private async Task<IReadOnlyList<Envelope>> popInReceiveTransactionAsync(CancellationToken token)
    {
        var conn = new SqlConnection(_connectionString);
        SqlTransaction? tx = null;

        try
        {
            await conn.OpenAsync(token);
            tx = (SqlTransaction)await conn.BeginTransactionAsync(token);

            var cmd = conn.CreateCommand(_receiveSql).With("count", 1);
            cmd.Transaction = tx;

            var messages = await cmd.FetchListAsync(async reader =>
            {
                var id = await reader.GetFieldValueAsync<Guid>(0, token);
                var headers = await reader.IsDBNullAsync(1, token)
                    ? null
                    : await reader.GetFieldValueAsync<string>(1, token);
                var body = await reader.IsDBNullAsync(2, token)
                    ? Array.Empty<byte>()
                    : await reader.GetFieldValueAsync<byte[]>(2, token);

                return _mapper.MapIncoming(id, headers, body);
            }, cancellation: token);

            if (messages.Count == 0)
            {
                await tx.RollbackAsync(CancellationToken.None);
                await tx.DisposeAsync();
                await conn.DisposeAsync();
                return messages;
            }

            holdReceive(messages[0], conn, tx);
            return messages;
        }
        catch
        {
            // Disposing the connection rolls back anything the pop did
            if (tx != null) await tx.DisposeAsync();
            await conn.DisposeAsync();
            throw;
        }
    }
}
