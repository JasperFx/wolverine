using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wolverine.Transports;

namespace Wolverine.Persistence.Durability;

public interface IEnvelopeTransaction
{
    Task PersistOutgoingAsync(Envelope envelope);
    Task PersistOutgoingAsync(Envelope[] envelopes);
    Task PersistIncomingAsync(Envelope envelope);

    Task CopyToAsync(IEnvelopeTransaction other);

    ValueTask RollbackAsync();
}

public static class EnvelopeTransactionExtensions
{
    public static Task PersistAsync(this IEnvelopeTransaction transaction, Envelope envelope)
    {
        if (envelope.Destination.Scheme == TransportConstants.Local)
        {
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
        foreach (var envelope in envelopes)
        {
            await transaction.PersistAsync(envelope);
        }
    }
}

