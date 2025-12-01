using System.Data.Common;
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

    public void ConfigureCommand(ICommandBuilder builder, IMartenSession session)
    {
        builder.Append(
            $"insert into {_incomingTable} ({DatabaseConstants.IncomingFields}) values (");
        builder.AppendParameter(EnvelopeSerializer.Serialize(Envelope));
        builder.Append(',');
        builder.AppendParameter(Envelope.Id);
        builder.Append(',');
        builder.AppendParameter(Envelope.Status.ToString());
        builder.Append(',');
        builder.AppendParameter(Envelope.OwnerId);
        builder.Append(',');
        builder.AppendParameter(Envelope.ScheduledTime.HasValue ? Envelope.ScheduledTime.Value : DBNull.Value);
        builder.Append(',');
        builder.AppendParameter(Envelope.Attempts);
        builder.Append(',');
        builder.AppendParameter(Envelope.MessageType);
        builder.Append(',');
        builder.AppendParameter(Envelope.Destination?.ToString());
        builder.Append(',');
        builder.AppendParameter(Envelope.KeepUntil.HasValue ? Envelope.KeepUntil.Value : DBNull.Value);
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