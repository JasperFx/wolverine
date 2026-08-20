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
    RETURNING id, headers, body)
SELECT id, headers, body FROM message;";
    }

    protected override int MaximumMessagesToReceive => _queue.MaximumMessagesToReceive;

    public override ValueTask CompleteAsync(Envelope envelope)
    {
        // The row was already removed by the destructive pop.
        return ValueTask.CompletedTask;
    }

    public override async ValueTask DeferAsync(Envelope envelope)
    {
        // Re-expose the message by writing it back to the NServiceBus queue table.
        await _sender.SendAsync(envelope);
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

                    return _mapper.MapIncoming(id, headers, body);
                }, cancellation: token);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
