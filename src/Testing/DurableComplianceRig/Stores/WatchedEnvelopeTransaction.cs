using System.Collections.Concurrent;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace DurableComplianceRig.Stores;

/// <summary>
/// Rig diagnostic. Wraps the <see cref="DatabaseEnvelopeTransaction" /> a plain RDBMS handler enlists, and records
/// who disposed it while the handler still owned it -- disposing it disposes the handler's own DbTransaction.
/// </summary>
public sealed class WatchedEnvelopeTransaction(DatabaseEnvelopeTransaction inner, Guid messageId)
    : IEnvelopeTransaction, IDisposable
{
    public static readonly ConcurrentQueue<string> EarlyDisposals = new();

    private volatile bool _handlerIsDone;

    public void HandlerIsDone() => _handlerIsDone = true;

    public void Dispose()
    {
        if (!_handlerIsDone)
        {
            EarlyDisposals.Enqueue($"{messageId} disposed while its handler was still running, by:{Environment.NewLine}{Environment.StackTrace}");
        }

        // Diagnostic switch: the handler already disposed its own DbTransaction with its connection
        if (Environment.GetEnvironmentVariable("RIG_SKIP_LATE_DISPOSE") != "1") inner.Dispose();
    }

    public Task PersistOutgoingAsync(Envelope envelope) => inner.PersistOutgoingAsync(envelope);
    public Task PersistOutgoingAsync(Envelope[] envelopes) => inner.PersistOutgoingAsync(envelopes);
    public Task PersistIncomingAsync(Envelope envelope) => inner.PersistIncomingAsync(envelope);
    public ValueTask RollbackAsync() => inner.RollbackAsync();

    public Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, DurabilitySettings settings,
        CancellationToken cancellation) => inner.TryMakeEagerIdempotencyCheckAsync(envelope, settings, cancellation);
}
