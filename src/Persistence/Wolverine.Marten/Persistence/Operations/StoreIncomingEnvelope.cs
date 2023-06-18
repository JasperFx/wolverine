using System.Data.Common;
using JasperFx.Core;
using Marten.Internal;
using Marten.Internal.Operations;
using Marten.Services;
using Weasel.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Marten.Persistence.Operations;

internal class StoreIncomingEnvelope : IStorageOperation, NoDataReturnedCall
{
    private readonly string _incomingTable;

    public StoreIncomingEnvelope(string incomingTable, Envelope envelope)
    {
        Envelope = envelope;
        _incomingTable = incomingTable;
    }

    public Envelope Envelope { get; }

    public void ConfigureCommand(CommandBuilder builder, IMartenSession session)
    {
        var list = new List<DbParameter>
        {
            builder.AddParameter(EnvelopeSerializer.Serialize(Envelope)),
            builder.AddParameter(Envelope.Id),
            builder.AddParameter(Envelope.Status.ToString()),
            builder.AddParameter(Envelope.OwnerId),
            builder.AddParameter(Envelope.ScheduledTime),
            builder.AddParameter(Envelope.Attempts),
            builder.AddParameter(Envelope.MessageType),
            builder.AddParameter(Envelope.Destination?.ToString())
        };

        var parameterList = list.Select(x => $":{x.ParameterName}").Join(", ");

        builder.Append(
            $@"insert into {_incomingTable} ({DatabaseConstants.IncomingFields}) values ({parameterList});");
    }

    public void Postprocess(DbDataReader reader, IList<Exception> exceptions)
    {
        // Nothing
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public OperationRole Role()
    {
        return OperationRole.Other;
    }

    public Type DocumentType => typeof(Envelope);
}