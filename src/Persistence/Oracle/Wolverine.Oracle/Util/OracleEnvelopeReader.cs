using System.Data.Common;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Oracle.Util;

/// <summary>
/// Oracle-specific envelope readers. Oracle stores Guids as RAW(16) which returns byte[],
/// not Guid directly. These methods handle the byte[] → Guid conversion that the shared
/// DatabasePersistence readers don't handle.
/// </summary>
internal static class OracleEnvelopeReader
{
    public static async Task<Envelope> ReadIncomingAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        var body = await reader.GetFieldValueAsync<byte[]>(0, cancellation);
        var envelope = body.Length > 0 ? EnvelopeSerializer.Deserialize(body) : new Envelope { Message = new PlaceHolder() };
        envelope.Id = ReadGuid(reader, 1);
        envelope.Status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(2, cancellation));
        envelope.OwnerId = Convert.ToInt32(reader.GetValue(3));
        envelope.MessageType = await reader.GetFieldValueAsync<string>(6, cancellation);

        var rawUri = await reader.GetFieldValueAsync<string>(7, cancellation);
        envelope.Destination = new Uri(rawUri);

        if (!await reader.IsDBNullAsync(4, cancellation))
        {
            envelope.ScheduledTime = await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellation);
        }

        envelope.Attempts = Convert.ToInt32(reader.GetValue(5));

        if (!await reader.IsDBNullAsync(8, cancellation))
        {
            envelope.KeepUntil = await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellation);
        }

        return envelope;
    }

    public static async Task<Envelope> ReadOutgoingAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        var body = await reader.GetFieldValueAsync<byte[]>(0, cancellation);
        var envelope = EnvelopeSerializer.Deserialize(body);
        envelope.OwnerId = Convert.ToInt32(reader.GetValue(2));

        if (!await reader.IsDBNullAsync(4, cancellation))
        {
            envelope.DeliverBy = await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellation);
        }

        envelope.Attempts = Convert.ToInt32(reader.GetValue(5));

        return envelope;
    }

    public static async Task<DeadLetterEnvelope> ReadDeadLetterAsync(DbDataReader reader,
        CancellationToken cancellation = default, ILogger? logger = null)
    {
        var id = ReadGuid(reader, 0);
        var executionTime = await reader.IsDBNullAsync(1, cancellation).ConfigureAwait(false) ? null : await reader.GetFieldValueAsync<DateTimeOffset?>(1, cancellation);

        // Scalars first, body last — a body we cannot deserialize still yields a self-describing row.
        var messageType = await reader.GetFieldValueAsync<string>(3, cancellation);
        // GH-3166: received_at is NULL for any envelope that dead-lettered without a Destination. The
        // shared RDBMS reader was guarded at the time; this Oracle copy was not, so a single
        // destination-less row still aborted the whole Oracle DLQ read.
        var receivedAt = await reader.IsDBNullAsync(4, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(4, cancellation);
        var source = await reader.IsDBNullAsync(5, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(5, cancellation);
        var exceptionType = await reader.IsDBNullAsync(6, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(6, cancellation);
        var exceptionMessage = await reader.IsDBNullAsync(7, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(7, cancellation);
        var sentAt = await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellation);

        // Oracle stores bool as NUMBER(1) - read as decimal and convert
        var replayableValue = reader.GetValue(9);
        var replayable = Convert.ToInt32(replayableValue) != 0;

        // Shared with the RDBMS reader so this copy cannot drift back into aborting the whole query on
        // one unreadable body.
        var envelope = await DatabasePersistence
            .ReadDeadLetterBodyAsync(reader, id, messageType, receivedAt, cancellation, logger);

        return new DeadLetterEnvelope(
            id,
            executionTime,
            envelope,
            messageType,
            receivedAt,
            source,
            exceptionType,
            exceptionMessage,
            sentAt,
            replayable
        );
    }

    /// <summary>
    /// Read a Guid from an Oracle RAW(16) column. Oracle returns byte[] for RAW columns.
    /// </summary>
    internal static Guid ReadGuid(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        if (value is Guid g) return g;
        if (value is byte[] bytes) return new Guid(bytes);
        throw new InvalidCastException($"Cannot convert {value?.GetType().Name ?? "null"} to Guid at ordinal {ordinal}");
    }

    /// <summary>
    /// Async version: Read a Guid from an Oracle RAW(16) column.
    /// </summary>
    internal static async Task<Guid> ReadGuidAsync(DbDataReader reader, int ordinal, CancellationToken cancellation = default)
    {
        var value = await reader.GetFieldValueAsync<object>(ordinal, cancellation);
        if (value is Guid g) return g;
        if (value is byte[] bytes) return new Guid(bytes);
        throw new InvalidCastException($"Cannot convert {value?.GetType().Name ?? "null"} to Guid at ordinal {ordinal}");
    }
}
