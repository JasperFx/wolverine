using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Postgresql.Transport.MassTransit;

internal class MassTransitPostgresqlQueueSender : ISender
{
    private readonly MassTransitPostgresqlQueue _queue;
    private readonly NpgsqlDataSource _dataSource;
    private readonly MassTransitPostgresqlEnvelopeMapper _mapper;
    private readonly string _sendSql;

    public MassTransitPostgresqlQueueSender(MassTransitPostgresqlQueue queue, IWolverineRuntime runtime)
    {
        _queue = queue;
        _dataSource = queue.Parent.Store.NpgsqlDataSource;
        _mapper = queue.BuildMapper(runtime);
        Destination = queue.Uri;

        // Call MassTransit's transport.send_message with NAMED args, supplying only the
        // parameters we populate so the function's own defaults cover the rest.
        _sendSql =
            $@"SELECT {queue.Parent.SchemaName}.send_message(
    entity_name => :entity_name,
    priority => :priority,
    body => :body,
    content_type => :content_type,
    message_type => :message_type,
    message_id => :message_id,
    correlation_id => :correlation_id,
    conversation_id => :conversation_id,
    source_address => :source_address,
    destination_address => :destination_address,
    response_address => :response_address,
    sent_time => :sent_time,
    headers => :headers,
    host => :host)";
    }

    // MassTransit delayed delivery uses a scheduling token + delay interval; not supported in v1.
    public bool SupportsNativeScheduledSend => false;

    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        try
        {
            return await _queue.ExistsAsync();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        var message = _mapper.MapOutgoing(_queue, envelope);

        await using var conn = await _dataSource.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand(_sendSql);

            cmd.Parameters.Add(new NpgsqlParameter("entity_name", NpgsqlDbType.Text) { Value = message.EntityName });
            // MassTransit's message_delivery.priority is NOT NULL; the function doesn't default it,
            // so pass MassTransit's normal default (100).
            cmd.Parameters.Add(new NpgsqlParameter("priority", NpgsqlDbType.Integer) { Value = 100 });
            cmd.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Jsonb) { Value = message.Body });
            cmd.Parameters.Add(new NpgsqlParameter("content_type", NpgsqlDbType.Text) { Value = message.ContentType });
            cmd.Parameters.Add(new NpgsqlParameter("message_type", NpgsqlDbType.Text)
                { Value = (object?)message.MessageType ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("message_id", NpgsqlDbType.Uuid) { Value = message.MessageId });
            cmd.Parameters.Add(new NpgsqlParameter("correlation_id", NpgsqlDbType.Uuid)
                { Value = (object?)message.CorrelationId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("conversation_id", NpgsqlDbType.Uuid)
                { Value = (object?)message.ConversationId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("source_address", NpgsqlDbType.Text)
                { Value = (object?)message.SourceAddress ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("destination_address", NpgsqlDbType.Text)
                { Value = (object?)message.DestinationAddress ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("response_address", NpgsqlDbType.Text)
                { Value = (object?)message.ResponseAddress ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("sent_time", NpgsqlDbType.TimestampTz)
                { Value = envelope.SentAt.UtcDateTime });
            cmd.Parameters.Add(new NpgsqlParameter("headers", NpgsqlDbType.Jsonb) { Value = message.Headers });
            cmd.Parameters.Add(new NpgsqlParameter("host", NpgsqlDbType.Jsonb) { Value = message.Host });

            await cmd.ExecuteScalarAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
