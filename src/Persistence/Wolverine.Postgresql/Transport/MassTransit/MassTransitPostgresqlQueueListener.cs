using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Postgresql.Transport.MassTransit;

internal class MassTransitPostgresqlQueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly MassTransitPostgresqlQueue _queue;
    private readonly IReceiver _receiver;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;
    private readonly MassTransitPostgresqlEnvelopeMapper _mapper;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _lockDuration;
    private readonly Guid _consumerId = Guid.NewGuid();
    private readonly string _fetchSql;
    private readonly string _deleteSql;
    private readonly string _unlockSql;

    // The lease coordinates per delivered envelope, so we can ack (delete_message) or
    // nack (unlock_message) using the (delivery id, lock id) pair MassTransit handed us.
    private readonly ConcurrentDictionary<Guid, (long DeliveryId, Guid LockId)> _leases = new();

    private Task? _task;

    public MassTransitPostgresqlQueueListener(MassTransitPostgresqlQueue queue, IWolverineRuntime runtime,
        IReceiver receiver)
    {
        _queue = queue;
        _receiver = receiver;
        _dataSource = queue.Parent.Store.NpgsqlDataSource;
        _logger = runtime.LoggerFactory.CreateLogger<MassTransitPostgresqlQueueListener>();
        _mapper = queue.BuildMapper(runtime);
        _pollingInterval = queue.PollingInterval ?? runtime.DurabilitySettings.ScheduledJobPollingTime;
        _lockDuration = queue.LockDuration;

        Address = queue.Uri;

        var schema = queue.Parent.SchemaName;

        _fetchSql =
            $@"SELECT message_delivery_id, lock_id, content_type, message_type, body, message_id, correlation_id, conversation_id, source_address, destination_address, response_address, sent_time, headers FROM {schema}.fetch_messages(:queue, :consumer_id, :lock_id, :lock_duration, :count)";

        _deleteSql = $"SELECT {schema}.delete_message(:delivery_id, :lock_id)";
        _unlockSql = $"SELECT {schema}.unlock_message(:delivery_id, :lock_id, :delay, :headers)";
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public Uri Address { get; }

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        if (!_leases.TryGetValue(envelope.Id, out var lease))
        {
            return;
        }

        await using var conn = await _dataSource.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand(_deleteSql);
            cmd.Parameters.Add(new NpgsqlParameter("delivery_id", NpgsqlDbType.Bigint) { Value = lease.DeliveryId });
            cmd.Parameters.Add(new NpgsqlParameter("lock_id", NpgsqlDbType.Uuid) { Value = lease.LockId });
            await cmd.ExecuteScalarAsync();
        }
        finally
        {
            await conn.CloseAsync();
            _leases.TryRemove(envelope.Id, out _);
        }
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (!_leases.TryGetValue(envelope.Id, out var lease))
        {
            return;
        }

        await using var conn = await _dataSource.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand(_unlockSql);
            cmd.Parameters.Add(new NpgsqlParameter("delivery_id", NpgsqlDbType.Bigint) { Value = lease.DeliveryId });
            cmd.Parameters.Add(new NpgsqlParameter("lock_id", NpgsqlDbType.Uuid) { Value = lease.LockId });
            cmd.Parameters.Add(new NpgsqlParameter("delay", NpgsqlDbType.Interval) { Value = TimeSpan.Zero });
            cmd.Parameters.Add(new NpgsqlParameter("headers", NpgsqlDbType.Jsonb) { Value = DBNull.Value });
            await cmd.ExecuteScalarAsync();
        }
        finally
        {
            await conn.CloseAsync();
            _leases.TryRemove(envelope.Id, out _);
        }
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync();
    }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        _task?.SafeDispose();
    }

    public Task StartAsync()
    {
        _task = Task.Run(listenForMessagesAsync, _cancellation.Token);
        return Task.CompletedTask;
    }

    private async Task listenForMessagesAsync()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var messages = await fetchAsync(_queue.MaximumMessagesToReceive, _cancellation.Token);
                failedCount = 0;

                if (messages.Count > 0)
                {
                    await _receiver.ReceivedAsync(this, messages.ToArray());
                }
                else
                {
                    await Task.Delay(_pollingInterval, _cancellation.Token);
                }
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException or OperationCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }

                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();
                _logger.LogError(e, "Error while trying to receive messages from MassTransit PostgreSQL queue {Name}",
                    _queue.Name);
                await Task.Delay(pauseTime);
            }
        }
    }

    private async Task<IReadOnlyList<Envelope>> fetchAsync(int count, CancellationToken token)
    {
        // A fresh lock_id per fetch call; the consumer_id is stable for the listener lifetime.
        var lockId = Guid.NewGuid();

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        try
        {
            await using var cmd = conn.CreateCommand(_fetchSql);
            cmd.Parameters.Add(new NpgsqlParameter("queue", NpgsqlDbType.Text) { Value = _queue.Name });
            cmd.Parameters.Add(new NpgsqlParameter("consumer_id", NpgsqlDbType.Uuid) { Value = _consumerId });
            cmd.Parameters.Add(new NpgsqlParameter("lock_id", NpgsqlDbType.Uuid) { Value = lockId });
            cmd.Parameters.Add(new NpgsqlParameter("lock_duration", NpgsqlDbType.Interval) { Value = _lockDuration });
            cmd.Parameters.Add(new NpgsqlParameter("count", NpgsqlDbType.Integer) { Value = count });

            var envelopes = new List<Envelope>();

            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var row = await readRowAsync(reader, token);
                var envelope = _mapper.MapIncoming(row);

                // Stash the lease so CompleteAsync / DeferAsync can ack/nack this delivery.
                _leases[envelope.Id] = (row.MessageDeliveryId, row.LockId);
                envelopes.Add(envelope);
            }

            return envelopes;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static async Task<MassTransitPostgresqlEnvelopeMapper.FetchedRow> readRowAsync(NpgsqlDataReader reader,
        CancellationToken token)
    {
        return new MassTransitPostgresqlEnvelopeMapper.FetchedRow
        {
            MessageDeliveryId = await reader.GetFieldValueAsync<long>(0, token),
            LockId = await reader.GetFieldValueAsync<Guid>(1, token),
            ContentType = await readNullableStringAsync(reader, 2, token),
            MessageType = await readNullableStringAsync(reader, 3, token),
            Body = await readJsonbAsync(reader, 4, token),
            MessageId = await readNullableGuidAsync(reader, 5, token),
            CorrelationId = await readNullableGuidAsync(reader, 6, token),
            ConversationId = await readNullableGuidAsync(reader, 7, token),
            SourceAddress = await readNullableStringAsync(reader, 8, token),
            DestinationAddress = await readNullableStringAsync(reader, 9, token),
            ResponseAddress = await readNullableStringAsync(reader, 10, token),
            SentTime = await readNullableDateTimeAsync(reader, 11, token),
            Headers = await readJsonbAsync(reader, 12, token)
        };
    }

    private static async Task<string?> readNullableStringAsync(NpgsqlDataReader reader, int ordinal,
        CancellationToken token)
    {
        return await reader.IsDBNullAsync(ordinal, token)
            ? null
            : await reader.GetFieldValueAsync<string>(ordinal, token);
    }

    // jsonb columns come back as the JSON text representation.
    private static async Task<string?> readJsonbAsync(NpgsqlDataReader reader, int ordinal, CancellationToken token)
    {
        return await reader.IsDBNullAsync(ordinal, token)
            ? null
            : await reader.GetFieldValueAsync<string>(ordinal, token);
    }

    private static async Task<Guid?> readNullableGuidAsync(NpgsqlDataReader reader, int ordinal,
        CancellationToken token)
    {
        return await reader.IsDBNullAsync(ordinal, token)
            ? null
            : await reader.GetFieldValueAsync<Guid>(ordinal, token);
    }

    private static async Task<DateTime?> readNullableDateTimeAsync(NpgsqlDataReader reader, int ordinal,
        CancellationToken token)
    {
        return await reader.IsDBNullAsync(ordinal, token)
            ? null
            : await reader.GetFieldValueAsync<DateTime>(ordinal, token);
    }
}
