using System.Data.Common;
using JasperFx.Core;
using Wolverine.Runtime.Serialization;
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

        list.Add(builder.AddParameter(EnvelopeSerializer.Serialize(envelope)));
        list.Add(builder.AddParameter(envelope.Id));
        list.Add(owner);
        list.Add(builder.AddParameter(envelope.Destination!.ToString()));
        list.Add(builder.AddParameter(envelope.DeliverBy));

        list.Add(builder.AddParameter(envelope.Attempts));
        list.Add(builder.AddParameter(envelope.MessageType));

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
            builder.AddParameter(EnvelopeSerializer.Serialize(envelope)),
            builder.AddParameter(envelope.Id),
            builder.AddParameter(envelope.Status.ToString()),
            builder.AddParameter(envelope.OwnerId),
            builder.AddParameter(envelope.ScheduledTime),
            builder.AddParameter(envelope.Attempts),
            builder.AddParameter(envelope.MessageType),
            builder.AddParameter(envelope.Destination?.ToString())
        };

        // TODO -- this seems like a good thing to generalize and move to Weasel


        var parameterList = list.Select(x => $"@{x.ParameterName}").Join(", ");

        builder.Append(
            $@"insert into {settings.SchemaName}.{DatabaseConstants.IncomingTable}({DatabaseConstants.IncomingFields}) values ({parameterList});");
    }

    public static async Task<Envelope> ReadIncomingAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        // TODO -- don't fetch columns that aren't read here.

        var body = await reader.GetFieldValueAsync<byte[]>(0, cancellation);
        var envelope = EnvelopeSerializer.Deserialize(body);
        envelope.Status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(2, cancellation));
        envelope.OwnerId = await reader.GetFieldValueAsync<int>(3, cancellation);

        if (!await reader.IsDBNullAsync(4, cancellation))
        {
            envelope.ScheduledTime = await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellation);
        }

        envelope.Attempts = await reader.GetFieldValueAsync<int>(5, cancellation);

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
            list.Add(builder.AddParameter(EnvelopeSerializer.Serialize(error.Envelope)));
            list.Add(builder.AddParameter(error.Envelope.MessageType));
            list.Add(builder.AddParameter(error.Envelope.Destination?.ToString()));
            list.Add(builder.AddParameter(error.Envelope.Source));
            list.Add(builder.AddParameter(error.ExceptionType));
            list.Add(builder.AddParameter(error.ExceptionMessage));
            list.Add(builder.AddParameter(error.Envelope.SentAt.ToUniversalTime()));
            list.Add(builder.AddParameter(false));

            var parameterList = list.Select(x => $"@{x.ParameterName}").Join(", ");

            builder.Append(
                $"insert into {databaseSettings.SchemaName}.{DatabaseConstants.DeadLetterTable} ({DatabaseConstants.DeadLetterFields}) values ({parameterList});");
        }
    }

    public static async Task<Envelope> ReadOutgoingAsync(DbDataReader reader, CancellationToken cancellation = default)
    {
        // TODO -- don't use all the columns
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