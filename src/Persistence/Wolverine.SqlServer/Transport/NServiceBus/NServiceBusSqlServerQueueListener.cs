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

        // Destructive competing-consumer pop honoring NServiceBus FIFO-by-RowVersion ordering.
        _receiveSql = $@"
WITH message AS (
    SELECT TOP(@count) Id, Headers, Body
    FROM {queue.TableIdentifier} WITH (UPDLOCK, READPAST, ROWLOCK)
    ORDER BY RowVersion)
DELETE FROM message
OUTPUT deleted.Id, deleted.Headers, deleted.Body;";
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
}
