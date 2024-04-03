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

    public void ConfigureCommand(ICommandBuilder builder, IMartenSession session)
    {
        builder.Append(
            $"insert into {_outgoingTable} ({DatabaseConstants.OutgoingFields}) values (");

        builder.AppendParameter(EnvelopeSerializer.Serialize(Envelope));
        builder.Append(',');
        builder.AppendParameter(Envelope.Id);
        builder.Append(',');
        builder.AppendParameter(_ownerId);
        builder.Append(',');
        builder.AppendParameter(Envelope.Destination!.ToString());
        builder.Append(',');
        builder.AppendParameter(Envelope.DeliverBy.HasValue ? Envelope.DeliverBy.Value : DBNull.Value);
        builder.Append(',');
        builder.AppendParameter(Envelope.Attempts);
        builder.Append(',');
        builder.AppendParameter(Envelope.MessageType);
        builder.Append(");");
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