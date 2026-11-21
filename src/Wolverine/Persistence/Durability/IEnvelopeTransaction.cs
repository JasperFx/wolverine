using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.Persistence.Durability;

public interface IEnvelopeTransaction
{
    Task PersistOutgoingAsync(Envelope envelope);
    Task PersistOutgoingAsync(Envelope[] envelopes);
    Task PersistIncomingAsync(Envelope envelope);

    ValueTask RollbackAsync();

    Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, DurabilitySettings settings,
        CancellationToken cancellation);
}

public static class EnvelopeTransactionExtensions
{
    public static Task PersistAsync(this IEnvelopeTransaction transaction, Envelope envelope)
    {
        var serializing = envelope.SerializeAheadAsync();
        if (!serializing.IsCompletedSuccessfully)
        {
            return serializeThenPersistAsync(transaction, envelope, serializing);
        }

        return persist(transaction, envelope);
    }

    // The store reads Envelope.Data synchronously when it writes the row, so an async serializer has to have
    // finished before it gets there
    private static async Task serializeThenPersistAsync(IEnvelopeTransaction transaction, Envelope envelope,
        ValueTask serializing)
    {
        await serializing.ConfigureAwait(false);
        await persist(transaction, envelope).ConfigureAwait(false);
    }

    private static Task persist(IEnvelopeTransaction transaction, Envelope envelope)
    {
        if (envelope.Destination!.Scheme == TransportConstants.Local)
        {
            // GH-5586. A full local queue leaves the envelope in the inbox for recovery rather than making the flush
            // after the commit wait for room. Not when the message context is its own transaction: nothing is written
            // until its flush, which stores and forwards and makes the same choice there.
            if (envelope.Sender is DurableLocalQueue queue)
            {
                // GH-5699: an inbox row is Incoming (or Scheduled) and owned by this node, however the envelope was sent
                queue.PrepareForInbox(envelope);

                if (transaction is not MessageContext) queue.ChooseInboxOwner(envelope);
            }

            return transaction.PersistIncomingAsync(envelope);
        }

        switch (envelope.Status)
        {
            case EnvelopeStatus.Outgoing:
                return transaction.PersistOutgoingAsync(envelope);

            case EnvelopeStatus.Incoming:
            case EnvelopeStatus.Handled:
            case EnvelopeStatus.Scheduled:
                return transaction.PersistIncomingAsync(envelope);
        }

        throw new InvalidOperationException();
    }

    public static async Task PersistAsync(this IEnvelopeTransaction transaction, IEnumerable<Envelope> envelopes)
    {
        foreach (var envelope in envelopes) await transaction.PersistAsync(envelope);
    }
}