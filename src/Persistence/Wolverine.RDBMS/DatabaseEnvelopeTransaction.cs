using System.Data.Common;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.RDBMS;

public class DatabaseEnvelopeTransaction : IEnvelopeTransaction, IDisposable
{
    private readonly IMessageDatabase _persistence;
    private readonly DbTransaction _tx;

    public DatabaseEnvelopeTransaction(MessageContext context, DbTransaction tx)
    {
        _persistence = context.Storage as IMessageDatabase ??
                       throw new InvalidOperationException(
                           "This message context is not using Database-backed messaging persistence");
        _tx = tx;
    }

    public void Dispose()
    {
        _tx.Dispose();
    }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        return PersistOutgoingAsync(new[] { envelope });
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        if (!envelopes.Any())
        {
            return Task.CompletedTask;
        }

        return _persistence.StoreOutgoingAsync(_tx, envelopes);
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        return _persistence.StoreIncomingAsync(_tx, new[] { envelope });
    }

    public ValueTask RollbackAsync()
    {
        return new ValueTask(_tx.RollbackAsync());
    }
}