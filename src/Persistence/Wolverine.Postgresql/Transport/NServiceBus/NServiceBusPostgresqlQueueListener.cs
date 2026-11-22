using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Postgresql.Transport.NServiceBus;

internal class NServiceBusPostgresqlQueueListener : DatabaseListener
{
    private readonly NServiceBusPostgresqlQueue _queue;
    private readonly NpgsqlDataSource _dataSource;
    private readonly NServiceBusPostgresqlEnvelopeMapper _mapper;
    private readonly NServiceBusPostgresqlQueueSender _sender;
    private readonly string _receiveSql;
    private readonly TimeSpan _reinsertBudget;
    private readonly CancellationToken _hostCancellation;

    public NServiceBusPostgresqlQueueListener(NServiceBusPostgresqlQueue queue, IWolverineRuntime runtime,
        IReceiver receiver)
        : base(queue.Uri, queue.Name, receiver,
            runtime.LoggerFactory.CreateLogger<NServiceBusPostgresqlQueueListener>(),
            queue.PollingInterval ?? runtime.DurabilitySettings.ScheduledJobPollingTime)
    {
        _queue = queue;
        _dataSource = queue.Parent.Store.NpgsqlDataSource;
        _mapper = queue.BuildMapper(runtime);
        _sender = new NServiceBusPostgresqlQueueSender(queue, runtime);
        _reinsertBudget = runtime.DurabilitySettings.DrainTimeout;
        _hostCancellation = runtime.Cancellation;

        // Destructive competing-consumer pop honoring NServiceBus FIFO-by-seq ordering. The
        // column identifiers are left unquoted, which PostgreSQL folds to lowercase — matching
        // the lowercase columns both NServiceBus and NServiceBusQueueTable declare.
        _receiveSql = $@"
WITH message AS (
    DELETE FROM {queue.TableIdentifier}
    WHERE ctid IN (
        SELECT ctid FROM {queue.TableIdentifier}
        ORDER BY seq
        LIMIT :count
        FOR UPDATE SKIP LOCKED)
    RETURNING id, headers, body, seq)
SELECT id, headers, body, seq FROM message;";
    }

    protected override int MaximumMessagesToReceive => _queue.MaximumMessagesToReceive;

    public override ValueTask CompleteAsync(Envelope envelope)
    {
        // The row was already removed by the destructive pop.
        return ValueTask.CompletedTask;
    }

    public override async ValueTask DeferAsync(Envelope envelope)
    {
        // Re-expose the message by writing it back to the NServiceBus queue table. The pop already deleted the
        // row, so this copy is the only one there will be: retry it, and fail loudly if it never goes in
        // (GH-5775). The copy takes back the seq the row was popped with to keep its FIFO place. The host's
        // cancellation stops the retries but never an insert in flight; the command timeout bounds that.
        var seq = (envelope as NServiceBusPostgresqlEnvelope)?.Seq;
        await reinsertWithRetriesAsync(envelope, _ => _sender.ReinsertAsync(envelope, seq, CancellationToken.None),
            _reinsertBudget, _hostCancellation);
    }

    protected override async Task<IReadOnlyList<Envelope>> TryPopAsync(int count, CancellationToken token)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(token);

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

                    var seq = Convert.ToInt64(reader.GetValue(3));

                    return _mapper.MapIncoming(id, headers, body, seq);
                }, cancellation: token);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
