using System.Data.Common;
using JasperFx.Core;
using Marten.Internal;
using Marten.Internal.Operations;
using Marten.Services;
using Weasel.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Marten.Persistence.Operations;

internal class StoreOutgoingEnvelope : IStorageOperation, NoDataReturnedCall
{
    private readonly string _outgoingTable;
    private readonly int _ownerId;

    public StoreOutgoingEnvelope(string outgoingTable, Envelope envelope, int ownerId)
    {
        Envelope = envelope;
        _outgoingTable = outgoingTable;
        _ownerId = ownerId;
    }

    public Envelope Envelope { get; }

    public void ConfigureCommand(CommandBuilder builder, IMartenSession session)
    {
        var list = new List<DbParameter>
        {
            builder.AddParameter(EnvelopeSerializer.Serialize(Envelope)),
            builder.AddParameter(Envelope.Id),
            builder.AddParameter(_ownerId),
            builder.AddParameter(Envelope.Destination!.ToString()),
            builder.AddParameter(Envelope.DeliverBy),
            builder.AddParameter(Envelope.Attempts),
            builder.AddParameter(Envelope.MessageType)
        };

        var parameterList = list.Select(x => $":{x.ParameterName}").Join(", ");

        builder.Append(
            $"insert into {_outgoingTable} ({DatabaseConstants.OutgoingFields}) values ({parameterList});");
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