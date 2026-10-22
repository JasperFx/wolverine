using System.Data.Common;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.RDBMS;

public class DatabaseEnvelopeTransaction : IEnvelopeTransaction, IDisposable
{
    private readonly IMessageDatabase _database;
    private readonly DbTransaction _tx;

    public DatabaseEnvelopeTransaction(IMessageDatabase database, DbTransaction tx)
    {
        _tx = tx;
        _database = database;
    }

    /// <summary>
    ///     Deliberately does NOT dispose the <see cref="DbTransaction" />: whoever began it owns it. Every caller,
    ///     the documented outbox pattern and Wolverine's own saga storage alike, finishes the transaction and
    ///     disposes it, or its connection, itself. This runs much later, when the <see cref="MessageContext" />
    ///     goes back to its pool, and Npgsql reuses one transaction object per pooled physical connection -- so
    ///     disposing it here killed the live transaction of whichever handler had rented that connection since.
    ///     Kept so that existing <c>using</c> blocks still compile. R1 in the durable compliance rig.
    /// </summary>
    public void Dispose()
    {
    }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        return PersistOutgoingAsync([envelope]);
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        if (envelopes.Length == 0)
        {
            return Task.CompletedTask;
        }

        return _database.StoreOutgoingAsync(_tx, envelopes);
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        return _database.StoreIncomingAsync(_tx, [envelope]);
    }

    public async Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, DurabilitySettings settings,
        CancellationToken cancellation)
    {
        var copy = Envelope.ForPersistedHandled(envelope, DateTimeOffset.UtcNow, settings);
        try
        {
            await PersistIncomingAsync(copy);
            envelope.WasPersistedInInbox = true;
            envelope.Status = EnvelopeStatus.Handled;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ValueTask RollbackAsync()
    {
        return new ValueTask(_tx.RollbackAsync());
    }
}