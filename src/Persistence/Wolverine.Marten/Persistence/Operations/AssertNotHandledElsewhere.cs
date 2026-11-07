using System.Data.Common;
using Marten.Internal;
using Marten.Internal.Operations;
using Marten.Services;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Storage;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace Wolverine.Marten.Persistence.Operations;

/// <summary>
/// GH-5423. Queued right after the mark-as-handled statements. It counts Handled rows for the envelope's identity
/// that this execution did not write. More than zero means another execution already finished the message -- a
/// node ejected as stale that resumed after its rows were recovered and handled elsewhere -- so the session's
/// commit fails with <see cref="DuplicateIncomingEnvelopeException" />, the work rolls back, and the envelope is
/// discarded as the duplicate it is.
/// </summary>
internal class AssertNotHandledElsewhere : global::Marten.Internal.Operations.IStorageOperation
{
    private readonly MarkAsHandledStatement _check;
    private readonly Envelope _envelope;

    public AssertNotHandledElsewhere(MarkAsHandledStatement check, Envelope envelope)
    {
        _check = check;
        _envelope = envelope;
    }

    public void ConfigureCommand(Weasel.Postgresql.ICommandBuilder builder, IStorageSession session)
    {
        // The statement was built with '?' placeholders, in the order of its arguments
        var parts = _check.Sql.Split('?');
        for (var i = 0; i < parts.Length; i++)
        {
            builder.Append(parts[i]);
            if (i < _check.Arguments.Length)
            {
                builder.AppendParameter(_check.Arguments[i]);
            }
        }
    }

    public void Postprocess(DbDataReader reader, IList<Exception> exceptions)
    {
        if (reader.Read() && Convert.ToInt64(reader.GetValue(0)) > 0)
        {
            exceptions.Add(new DuplicateIncomingEnvelopeException(_envelope));
        }
    }

    public async Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        if (await reader.ReadAsync(token) && Convert.ToInt64(reader.GetValue(0)) > 0)
        {
            exceptions.Add(new DuplicateIncomingEnvelopeException(_envelope));
        }
    }

    public OperationRole Role()
    {
        return OperationRole.Other;
    }

    // See StoreIncomingEnvelope: a type Marten already maps, since this touches a Wolverine table
    public Type DocumentType => typeof(Envelope);
}
