using System.Data.Common;
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

    public static async Task<DeadLetterEnvelope> ReadDeadLetterAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        var id = ReadGuid(reader, 0);
        var executionTime = await reader.IsDBNullAsync(1, cancellation).ConfigureAwait(false) ? null : await reader.GetFieldValueAsync<DateTimeOffset?>(1, cancellation);
        var envelope = EnvelopeSerializer.Deserialize(await reader.GetFieldValueAsync<byte[]>(2, cancellation));
        var messageType = await reader.GetFieldValueAsync<string>(3, cancellation);
        var receivedAt = await reader.GetFieldValueAsync<string>(4, cancellation);
        var source = await reader.GetFieldValueAsync<string>(5, cancellation);
        var exceptionType = await reader.GetFieldValueAsync<string>(6, cancellation);
        var exceptionMessage = await reader.GetFieldValueAsync<string>(7, cancellation);
        var sentAt = await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellation);

        // Oracle stores bool as NUMBER(1) - read as decimal and convert
        var replayableValue = reader.GetValue(9);
        var replayable = Convert.ToInt32(replayableValue) != 0;

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
