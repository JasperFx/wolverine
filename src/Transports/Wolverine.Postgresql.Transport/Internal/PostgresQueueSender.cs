using System.Collections.Immutable;
using Npgsql;
using NpgsqlTypes;
using static NpgsqlTypes.NpgsqlDbType;

namespace Wolverine.Transports.Postgresql.Internal;

internal sealed class PostgresQueueSender
{
    private readonly PostgresQueue _queue;

    public PostgresQueueSender(PostgresQueue queue)
    {
        _queue = queue;
    }

    /// <summary>
    /// Inserts messages into the queue
    /// </summary>
    /// <param name="messages"></param>
    /// <param name="cancellationToken"></param>
    public async Task SendAsync(
        IEnumerable<PostgresMessage> messages,
        CancellationToken cancellationToken)
    {
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 5
        };

        await Parallel.ForEachAsync(
            messages,
            options,
            async (message, ct) => { await SendAsync(message, ct); });
    }

    /// <summary>
    /// Inserts messages into the queue
    /// </summary>
    /// <param name="messages"></param>
    /// <param name="cancellationToken"></param>
    private async Task SendAsync(
        PostgresMessage message,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await _queue.Transport.Client.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            INSERT INTO {_queue.QueueName} (Id, CorrelationId, MessageType, ContentType, ScheduledTime, Data, Headers, Attempts, SenderId)
            VALUES (@id, @correlationId, @messageType, @contentType, @scheduledTime, @data, @header, @attempt, @senderId);
        """;

        // add the parameters to the command
        command.Parameters.AddWithValue("id", Uuid, message.Id);
        command.Parameters.AddWithValue("correlationId", Varchar, message.CorrelationId!);
        command.Parameters.AddWithValue("messageType", Varchar, message.MessageType!);
        command.Parameters.AddWithValue("contentType", Varchar, message.ContentType!);
        command.Parameters.AddWithValue("scheduledTime", TimestampTz, message.ScheduledTime);
        command.Parameters.AddWithValue("data", Bytea, message.Data!);
        command.Parameters.AddWithValue("header",
            Jsonb,
            (IReadOnlyDictionary<string, string>?) message.Headers ??
            ImmutableDictionary<string, string>.Empty);
        command.Parameters.AddWithValue("attempt", Integer, message.Attempts);
        command.Parameters.AddWithValue("senderId", Uuid, message.SenderId);

        // execute the command
        await command.PrepareAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
