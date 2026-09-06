using System.Data.Common;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public static class DatabasePersistence
{
    public static DbCommand BuildOutgoingStorageCommand(Envelope envelope, int ownerId,
        IMessageDatabase database)
    {
        var builder = database.ToCommandBuilder();

        var owner = builder.AddNamedParameter("owner", ownerId);
        ConfigureOutgoingCommand(database, builder, envelope, owner);
        return builder.Compile();
    }

    public static DbCommand BuildOutgoingStorageCommand(Envelope[] envelopes, int ownerId,
        IMessageDatabase database)
    {
        var builder = database.ToCommandBuilder();

        var owner = builder.AddNamedParameter("owner", ownerId);

        foreach (var envelope in envelopes) ConfigureOutgoingCommand(database, builder, envelope, owner);

        return builder.Compile();
    }

    private static void ConfigureOutgoingCommand(IMessageDatabase settings, DbCommandBuilder builder, Envelope envelope,
        DbParameter owner)
    {
        var list = new List<DbParameter>
        {
            builder.AddParameter(EnvelopeSerializer.Serialize(envelope)),
            builder.AddParameter(envelope.Id),
            owner,
            builder.AddParameter(envelope.Destination!.ToString()),
            builder.AddParameter(envelope.DeliverBy),
            builder.AddParameter(envelope.Attempts),
            builder.AddParameter(envelope.MessageType)
        };

        var parameterList = list.Select(x => $"@{x.ParameterName}").Join(", ");

        builder.Append(
            $"insert into {settings.TableNameFor(DatabaseConstants.OutgoingTable)} ({DatabaseConstants.OutgoingFields}) values ({parameterList});");
    }

    public static DbCommand BuildIncomingStorageCommand(IEnumerable<Envelope> envelopes,
        IMessageDatabase database)
    {
        var builder = database.ToCommandBuilder();

        foreach (var envelope in envelopes) BuildIncomingStorageCommand(database, builder, envelope);

        return builder.Compile();
    }

    public static void BuildIncomingStorageCommand(IMessageDatabase settings, DbCommandBuilder builder,
        Envelope envelope)
    {
        // Don't store any data if the envelope is already marked as handled
        var data = envelope.Status == EnvelopeStatus.Handled ? [] : EnvelopeSerializer.Serialize(envelope);
        
        var list = new List<DbParameter>
        {
            builder.AddParameter(data),
            builder.AddParameter(envelope.Id),
            builder.AddParameter(envelope.Status.ToString()),
            builder.AddParameter(envelope.OwnerId),
            builder.AddParameter(envelope.ScheduledTime),
            builder.AddParameter(envelope.Attempts),
            builder.AddParameter(envelope.MessageType),
            builder.AddParameter(envelope.Destination?.ToString()),
            builder.AddParameter(envelope.KeepUntil)
        };

        var parameterList = list.Select(x => $"@{x.ParameterName}").Join(", ");

        builder.Append(
            $@"insert into {settings.TableNameFor(DatabaseConstants.IncomingTable)}({DatabaseConstants.IncomingFields}) values ({parameterList});");
    }

    public static async Task<Envelope> ReadIncomingAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        var body = await reader.GetFieldValueAsync<byte[]>(0, cancellation);
        var envelope = body.Length > 0 ? EnvelopeSerializer.Deserialize(body) : new Envelope{Message = new PlaceHolder()};
        envelope.Id = await reader.GetFieldValueAsync<Guid>(1, cancellation);
        envelope.Status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(2, cancellation));
        envelope.OwnerId = await reader.GetFieldValueAsync<int>(3, cancellation);
        envelope.MessageType = await reader.GetFieldValueAsync<string>(6, cancellation);

        var rawUri = await reader.GetFieldValueAsync<string>(7, cancellation);
        envelope.Destination = new Uri(rawUri);

        if (!await reader.IsDBNullAsync(4, cancellation))
        {
            envelope.ScheduledTime = await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellation);
        }

        envelope.Attempts = await reader.GetFieldValueAsync<int>(5, cancellation);

        if (!await reader.IsDBNullAsync(8, cancellation))
        {
            envelope.KeepUntil = await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellation);
        }

        return envelope;
    }

    public static async Task<DeadLetterEnvelope> ReadDeadLetterAsync(DbDataReader reader,
        CancellationToken cancellation = default, ILogger? logger = null)
    {
        var id = await reader.GetFieldValueAsync<Guid>(0, cancellation);
        var executionTime = await reader.IsDBNullAsync(1, cancellation).ConfigureAwait(false) ? null : await reader.GetFieldValueAsync<DateTimeOffset?>(1, cancellation);

        // Read every scalar column BEFORE deserializing the body, so that a body we cannot read still
        // yields a row describing itself. See the placeholder note below.
        var messageType = await reader.GetFieldValueAsync<string>(3, cancellation);
        // GH-3166: received_at is written as envelope.Destination?.ToString() (DatabasePersistence's dead
        // letter insert), so it is NULL for any envelope that dead-lettered without a destination (e.g. a
        // locally-published message that failed before routing). Guard the read the same way `source` is —
        // an unguarded GetFieldValueAsync<string> throws on DBNull, which previously poisoned the whole
        // DLQ "Query Messages" fetch and surfaced as "No messages loaded".
        var receivedAt = await reader.IsDBNullAsync(4, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(4, cancellation);
        var source = await reader.IsDBNullAsync(5, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(5, cancellation);
        var exceptionType = await reader.IsDBNullAsync(6, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(6, cancellation);
        var exceptionMessage = await reader.IsDBNullAsync(7, cancellation) ? string.Empty : await reader.GetFieldValueAsync<string>(7, cancellation);
        var sentAt = await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellation);
        var replayable = await reader.GetFieldValueAsync<bool>(9, cancellation);

        var envelope = await ReadDeadLetterBodyAsync(reader, id, messageType, receivedAt, cancellation, logger);

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

    // GH-3166 hardened the per-column reads; this hardens the body itself. A single unreadable `body`
    // — a row written by something other than EnvelopeSerializer (hand-seeded raw JSON is the observed
    // case), or one serialized by an incompatible Wolverine version — used to throw straight out of the
    // per-row read and abort the enclosing DeadLetterEnvelopeQuery for the WHOLE database. The console
    // then showed "No dead letter queue entries found", hiding every other, perfectly readable dead
    // letter in that store. One bad row must cost you that row, not the queue.
    // Public so the Oracle reader — which has to duplicate the surrounding column reads for RAW(16) Guids
    // and NUMBER(1) bools — shares this guard rather than re-growing its own copy of the bug.
    public static async Task<Envelope> ReadDeadLetterBodyAsync(DbDataReader reader, Guid id, string messageType,
        string receivedAt, CancellationToken cancellation, ILogger? logger = null)
    {
        try
        {
            if (await reader.IsDBNullAsync(2, cancellation))
            {
                return BuildUnreadableDeadLetterEnvelope(id, messageType, receivedAt);
            }

            return EnvelopeSerializer.Deserialize(await reader.GetFieldValueAsync<byte[]>(2, cancellation));
        }
        catch (Exception e)
        {
            logger?.LogError(e,
                "Unable to deserialize the stored body of dead letter envelope {Id} of message type {MessageType}. Returning a placeholder so the rest of the dead letter queue stays readable",
                id, messageType);

            return BuildUnreadableDeadLetterEnvelope(id, messageType, receivedAt);
        }
    }

    private static Envelope BuildUnreadableDeadLetterEnvelope(Guid id, string messageType, string receivedAt)
    {
        var envelope = new Envelope
        {
            Id = id,
            MessageType = messageType,
            Message = new PlaceHolder()
        };

        // received_at IS the destination Uri string (see the GH-3166 note above), so we can usually still
        // tell the operator which endpoint the poison row belongs to.
        if (receivedAt.IsNotEmpty() && Uri.TryCreate(receivedAt, UriKind.Absolute, out var destination))
        {
            envelope.Destination = destination;
        }

        return envelope;
    }

    public static void ConfigureDeadLetterCommands(DurabilitySettings durability, Envelope envelope,
        Exception? exception, DbCommandBuilder builder,
        IMessageDatabase wolverineDatabase)
    {
        byte[] data = [];
        try
        {
            data = EnvelopeSerializer.Serialize(envelope);
        }
        catch (WolverineSerializationException e)
        {
            wolverineDatabase.Logger.LogError(e, "Error trying to serialize a dead letter envelope");
        }
        
        var list = new List<DbParameter>
        {
            builder.AddParameter(envelope.Id),
            builder.AddParameter(envelope.ScheduledTime),
            builder.AddParameter(data),
            builder.AddParameter(envelope.MessageType ?? "unknown"),
            builder.AddParameter(envelope.Destination?.ToString()),
            builder.AddParameter(envelope.Source ?? "unknown"),
            builder.AddParameter(exception.DeadLetterExceptionType()),
            builder.AddParameter(exception.DeadLetterExceptionMessage()),
            builder.AddParameter(envelope.SentAt.ToUniversalTime()),
            builder.AddParameter(false)
        };

        var deadLetterFields = DatabaseConstants.DeadLetterFields;
        if (durability.DeadLetterQueueExpirationEnabled)
        {
            // If there is a deliver by, use that
            var expiration = envelope.DeliverBy.HasValue 
                ? builder.AddParameter(envelope.DeliverBy.Value)
                : builder.AddParameter(DateTimeOffset.UtcNow.Add(durability.DeadLetterQueueExpiration));
            
            list.Add(expiration);
            deadLetterFields += ", " + DatabaseConstants.Expires;
        }

        var parameterList = list.Select(x => $"@{x.ParameterName}").Join(", ");
        
        builder.Append(
            $"insert into {wolverineDatabase.TableNameFor(DatabaseConstants.DeadLetterTable)} ({deadLetterFields}) values ({parameterList});");
    }

    public static async Task<Envelope> ReadOutgoingAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        var body = await reader.GetFieldValueAsync<byte[]>(0, cancellation);
        var envelope = EnvelopeSerializer.Deserialize(body);
        envelope.OwnerId = await reader.GetFieldValueAsync<int>(2, cancellation);

        if (!await reader.IsDBNullAsync(4, cancellation))
        {
            envelope.DeliverBy = await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellation);
        }

        envelope.Attempts = await reader.GetFieldValueAsync<int>(5, cancellation);

        return envelope;
    }
}