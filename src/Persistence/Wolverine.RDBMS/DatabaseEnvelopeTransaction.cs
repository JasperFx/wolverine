using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

public class DatabaseEnvelopeTransaction : IEnvelopeTransaction, IDisposable
{
    private readonly IDatabaseBackedEnvelopePersistence _persistence;
    private readonly DbTransaction _tx;

    public DatabaseEnvelopeTransaction(MessageContext context, DbTransaction tx)
    {
        _persistence = context.Persistence as IDatabaseBackedEnvelopePersistence ??
                       throw new InvalidOperationException(
                           "This message context is not using Database-backed messaging persistence");
        _tx = tx;
    }

    public void Dispose()
    {
        _tx?.Dispose();
    }

    public Task PersistAsync(Envelope envelope)
    {
        return PersistAsync(new[] { envelope });
    }

    public Task PersistAsync(Envelope[] envelopes)
    {
        if (!envelopes.Any())
        {
            return Task.CompletedTask;
        }

        return _persistence.StoreOutgoingAsync(_tx, envelopes);
    }

    public Task ScheduleJobAsync(Envelope envelope)
    {
        envelope.OwnerId = TransportConstants.AnyNode;
        envelope.Status = EnvelopeStatus.Scheduled;

        return _persistence.StoreIncomingAsync(_tx, new[] { envelope });
    }

    public Task CopyToAsync(IEnvelopeTransaction other)
    {
        throw new NotSupportedException(
            $"Cannot copy data from an existing Sql Server envelope transaction to {other}. You may have erroneously enlisted an IMessageContext in a transaction twice.");
    }

    public async ValueTask RollbackAsync()
    {
        await _tx.RollbackAsync().ConfigureAwait(false);
    }
}
