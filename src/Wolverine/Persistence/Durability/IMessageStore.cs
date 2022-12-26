namespace Wolverine.Persistence.Durability;

public interface IMessageStore : IDisposable
{
    IMessageStoreAdmin Admin { get; }

    IDurableStorageSession Session { get; }

    Task ScheduleExecutionAsync(Envelope[] envelopes);


    Task MoveToDeadLetterStorageAsync(ErrorReport[] errors);

    Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception ex);

    Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope);

    Task StoreIncomingAsync(Envelope envelope);

    Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes);

    Task DeleteIncomingEnvelopesAsync(Envelope[] envelopes);

    Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope);

    void Describe(TextWriter writer);
    Task ScheduleJobAsync(Envelope envelope);

    Task<IReadOnlyList<Envelope>> LoadScheduledToExecuteAsync(DateTimeOffset utcNow);

    Task ReassignDormantNodeToAnyNodeAsync(int nodeId);
    Task<int[]> FindUniqueOwnersAsync(int currentNodeId);


    Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination);
    Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing);
    Task DeleteByDestinationAsync(Uri? destination);
    Task<Uri[]> FindAllDestinationsAsync();

    Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId);

    Task StoreOutgoingAsync(Envelope envelope, int ownerId);

    Task StoreOutgoingAsync(Envelope[] envelopes, int ownerId);

    Task DeleteOutgoingAsync(Envelope[] envelopes);

    Task DeleteOutgoingAsync(Envelope envelope);


    Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming);

    // TODO -- call this in system drain?
    Task ReleaseIncomingAsync(int ownerId);

    // TODO -- call from DurableReceiver.DrainAsync()
    Task ReleaseIncomingAsync(int ownerId, Uri receivedAt);

    Task<ErrorReport?> LoadDeadLetterEnvelopeAsync(Guid id);

    Task DeleteExpiredHandledEnvelopesAsync(DateTimeOffset utcNow);

    Task<IReadOnlyList<IncomingCount>> LoadAtLargeIncomingCountsAsync();

    Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit);
}

public record IncomingCount(Uri Destination, int Count);