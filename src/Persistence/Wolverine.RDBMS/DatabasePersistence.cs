using System.Data.Common;
using JasperFx.Core;
using Wolverine.Util;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public static class DatabasePersistence
{
    public static DbCommand BuildOutgoingStorageCommand(Envelope envelope, int ownerId,
        DatabaseSettings settings)
    {
        var builder = settings.ToCommandBuilder();

        var owner = builder.AddNamedParameter("owner", ownerId);
        ConfigureOutgoingCommand(settings, builder, envelope, owner);
        return builder.Compile();
    }

    public static DbCommand BuildOutgoingStorageCommand(Envelope[] envelopes, int ownerId,
        DatabaseSettings settings)
    {
        var builder = settings.ToCommandBuilder();

        var owner = builder.AddNamedParameter("owner", ownerId);

        foreach (var envelope in envelopes) ConfigureOutgoingCommand(settings, builder, envelope, owner);

        return builder.Compile();
    }

    private static void ConfigureOutgoingCommand(DatabaseSettings settings, DbCommandBuilder builder, Envelope envelope,
        DbParameter owner)
    {
        var list = new List<DbParameter>();

        list.Add(builder.AddParameter(envelope.Data));
        list.Add(builder.AddParameter(envelope.Id));
        list.Add(owner);
        list.Add(builder.AddParameter(envelope.Destination!.ToString()));
        list.Add(builder.AddParameter(envelope.DeliverBy));

        list.Add(builder.AddParameter(envelope.Attempts));
        list.Add(builder.AddParameter(envelope.ConversationId));
        list.Add(builder.AddParameter(envelope.CorrelationId));
        list.Add(builder.AddParameter(envelope.ParentId));
        list.Add(builder.AddParameter(envelope.SagaId));
        list.Add(builder.AddParameter(envelope.MessageType));
        list.Add(builder.AddParameter(envelope.ContentType));
        list.Add(builder.AddParameter(envelope.ReplyRequested));
        list.Add(builder.AddParameter(envelope.AckRequested));
        list.Add(builder.AddParameter(envelope.ReplyUri?.ToString()));
        list.Add(builder.AddParameter(envelope.SentAt.ToUniversalTime()));

        var parameterList = list.Select(x => $"@{x.ParameterName}").Join(", ");

        builder.Append(
            $"insert into {settings.SchemaName}.{DatabaseConstants.OutgoingTable} ({DatabaseConstants.OutgoingFields}) values ({parameterList});");
    }

    public static DbCommand BuildIncomingStorageCommand(IEnumerable<Envelope> envelopes,
        DatabaseSettings settings)
    {
        var builder = settings.ToCommandBuilder();

        foreach (var envelope in envelopes) BuildIncomingStorageCommand(settings, builder, envelope);

        return builder.Compile();
    }

    public static void BuildIncomingStorageCommand(DatabaseSettings settings, DbCommandBuilder builder,
        Envelope envelope)
    {
        var list = new List<DbParameter>
        {
            builder.AddParameter(envelope.Data),
            builder.AddParameter(envelope.Id),
            builder.AddParameter(envelope.Status.ToString()),
            builder.AddParameter(envelope.OwnerId),
            builder.AddParameter(envelope.ScheduledTime),
            builder.AddParameter(envelope.Attempts),
            builder.AddParameter(envelope.ConversationId),
            builder.AddParameter(envelope.CorrelationId),
            builder.AddParameter(envelope.ParentId),
            builder.AddParameter(envelope.SagaId),
            builder.AddParameter(envelope.MessageType),
            builder.AddParameter(envelope.ContentType),
            builder.AddParameter(envelope.ReplyRequested),
            builder.AddParameter(envelope.AckRequested),
            builder.AddParameter(envelope.ReplyUri?.ToString()),
            builder.AddParameter(envelope.Destination?.ToString()),
            builder.AddParameter(envelope.SentAt.ToUniversalTime())
        };

        // TODO -- this seems like a good thing to generalize and move to Weasel


        var parameterList = list.Select(x => $"@{x.ParameterName}").Join(", ");

        builder.Append(
            $@"insert into {settings.SchemaName}.{DatabaseConstants.IncomingTable} ({DatabaseConstants.IncomingFields}) values ({parameterList});");
    }

    public static async Task<Envelope> ReadIncomingAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        var envelope = new Envelope
        {
            Data = await reader.GetFieldValueAsync<byte[]>(0, cancellation),
            Id = await reader.GetFieldValueAsync<Guid>(1, cancellation),
            Status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(2, cancellation)),
            OwnerId = await reader.GetFieldValueAsync<int>(3, cancellation)
        };

        if (!await reader.IsDBNullAsync(4, cancellation))
        {
            envelope.ScheduledTime = await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellation);
        }

        envelope.Attempts = await reader.GetFieldValueAsync<int>(5, cancellation);

        envelope.ConversationId = await reader.MaybeReadAsync<Guid>(6, cancellation);
        envelope.CorrelationId = await reader.MaybeReadAsync<string>(7, cancellation);
        envelope.ParentId = await reader.MaybeReadAsync<string>(8, cancellation);
        envelope.SagaId = await reader.MaybeReadAsync<string>(9, cancellation);

        envelope.MessageType = await reader.GetFieldValueAsync<string>(10, cancellation);
        envelope.ContentType = await reader.GetFieldValueAsync<string>(11, cancellation);
        envelope.ReplyRequested = await reader.MaybeReadAsync<string>(12, cancellation);
        envelope.AckRequested = await reader.GetFieldValueAsync<bool>(13, cancellation);
        envelope.ReplyUri = await reader.ReadUriAsync(14, cancellation);
        envelope.Destination = await reader.ReadUriAsync(15, cancellation);
        envelope.SentAt = await reader.GetFieldValueAsync<DateTimeOffset>(16, cancellation);

        return envelope;
    }

    public static void ConfigureDeadLetterCommands(ErrorReport[] errors, DbCommandBuilder builder,
        DatabaseSettings databaseSettings)
    {
        foreach (var error in errors)
        {
            var list = new List<DbParameter>();

            list.Add(builder.AddParameter(error.Id));
            list.Add(builder.AddParameter(error.Envelope.ScheduledTime));
            list.Add(builder.AddParameter(error.Envelope.Attempts));
            list.Add(builder.AddParameter(error.Envelope.Data));
            list.Add(builder.AddParameter(error.Envelope.ConversationId));
            list.Add(builder.AddParameter(error.Envelope.CorrelationId));
            list.Add(builder.AddParameter(error.Envelope.ParentId));
            list.Add(builder.AddParameter(error.Envelope.SagaId));
            list.Add(builder.AddParameter(error.Envelope.MessageType));
            list.Add(builder.AddParameter(error.Envelope.ContentType));
            list.Add(builder.AddParameter(error.Envelope.ReplyRequested));
            list.Add(builder.AddParameter(error.Envelope.AckRequested));
            list.Add(builder.AddParameter(error.Envelope.ReplyUri?.ToString()));
            list.Add(builder.AddParameter(error.Envelope.Source));
            list.Add(builder.AddParameter(error.Explanation));
            list.Add(builder.AddParameter(error.ExceptionText));
            list.Add(builder.AddParameter(error.ExceptionType));
            list.Add(builder.AddParameter(error.ExceptionMessage));
            list.Add(builder.AddParameter(error.Envelope.SentAt.ToUniversalTime()));

            var parameterList = list.Select(x => $"@{x.ParameterName}").Join(", ");

            builder.Append(
                $"insert into {databaseSettings.SchemaName}.{DatabaseConstants.DeadLetterTable} ({DatabaseConstants.DeadLetterFields}) values ({parameterList});");
        }
    }

    public static async Task<Envelope> ReadOutgoingAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        var envelope = new Envelope
        {
            Data = await reader.GetFieldValueAsync<byte[]>(0, cancellation),
            Id = await reader.GetFieldValueAsync<Guid>(1, cancellation),
            OwnerId = await reader.GetFieldValueAsync<int>(2, cancellation),
            Destination = (await reader.GetFieldValueAsync<string>(3, cancellation)).ToUri()
        };

        if (!await reader.IsDBNullAsync(4, cancellation))
        {
            envelope.DeliverBy = await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellation);
        }

        envelope.Attempts = await reader.GetFieldValueAsync<int>(5, cancellation);
        envelope.ConversationId = await reader.MaybeReadAsync<Guid>(6, cancellation);
        envelope.CorrelationId = await reader.MaybeReadAsync<string>(7, cancellation);
        envelope.ParentId = await reader.MaybeReadAsync<string>(8, cancellation);
        envelope.SagaId = await reader.MaybeReadAsync<string>(9, cancellation);
        envelope.MessageType = await reader.GetFieldValueAsync<string>(10, cancellation);
        envelope.ContentType = await reader.GetFieldValueAsync<string>(11, cancellation);
        envelope.ReplyRequested = await reader.MaybeReadAsync<string>(12, cancellation);
        envelope.AckRequested = await reader.GetFieldValueAsync<bool>(13, cancellation);
        envelope.ReplyUri = await reader.ReadUriAsync(14, cancellation);
        envelope.SentAt = await reader.GetFieldValueAsync<DateTimeOffset>(15, cancellation);

        return envelope;
    }
}