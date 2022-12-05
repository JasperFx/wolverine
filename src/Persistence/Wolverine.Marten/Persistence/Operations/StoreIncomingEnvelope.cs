using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using Marten.Internal;
using Marten.Internal.Operations;
using Weasel.Postgresql;
using Wolverine.RDBMS;

namespace Wolverine.Marten.Persistence.Operations;

internal class StoreIncomingEnvelope : IStorageOperation
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
            builder.AddParameter(Envelope.Data),
            builder.AddParameter(Envelope.Id),
            builder.AddParameter(Envelope.Status.ToString()),
            builder.AddParameter(Envelope.OwnerId),
            builder.AddParameter(Envelope.ScheduledTime),
            builder.AddParameter(Envelope.Attempts),
            builder.AddParameter(Envelope.ConversationId),
            builder.AddParameter(Envelope.CorrelationId),
            builder.AddParameter(Envelope.ParentId),
            builder.AddParameter(Envelope.SagaId),
            builder.AddParameter(Envelope.MessageType),
            builder.AddParameter(Envelope.ContentType),
            builder.AddParameter(Envelope.ReplyRequested),
            builder.AddParameter(Envelope.AckRequested),
            builder.AddParameter(Envelope.ReplyUri?.ToString()),
            builder.AddParameter(Envelope.Destination?.ToString()),
            builder.AddParameter(Envelope.SentAt.ToUniversalTime())
        };

        // TODO -- this seems like a good thing to generalize and move to Weasel

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