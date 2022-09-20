using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Marten.Internal;
using Marten.Internal.Operations;
using Weasel.Postgresql;
using Wolverine.RDBMS;

namespace Wolverine.Marten.Persistence.Operations;

public class StoreOutgoingEnvelope : IStorageOperation
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
            builder.AddParameter(Envelope.Data),
            builder.AddParameter(Envelope.Id),
            builder.AddParameter(_ownerId),
            builder.AddParameter(Envelope.Destination!.ToString()),
            builder.AddParameter(Envelope.DeliverBy),
            builder.AddParameter(Envelope.Attempts),
            builder.AddParameter(Envelope.ConversationId),
            builder.AddParameter(Envelope.CorrelationId),
            builder.AddParameter(Envelope.ParentId),
            builder.AddParameter(Envelope.SagaId),
            builder.AddParameter(Envelope.MessageType),
            builder.AddParameter(Envelope.ContentType),
            builder.AddParameter(Envelope.ReplyRequested),
            builder.AddParameter(Envelope.AckRequested),
            builder.AddParameter(Envelope.ReplyUri?.ToString())
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
