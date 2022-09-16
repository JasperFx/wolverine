using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Wolverine.Persistence.Durability;

public interface IEnvelopePersistence : IDisposable
{
    IEnvelopeStorageAdmin Admin { get; }

    IDurableStorageSession Session { get; }

    // Used by IRetries and DurableCallback
    Task ScheduleExecutionAsync(Envelope[] envelopes);


    // Used by DurableCallback
    Task MoveToDeadLetterStorageAsync(ErrorReport[] errors);

    Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? ex);

    // Used by DurableCallback
    Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope);

    // Used by LoopbackSendingAgent
    Task StoreIncomingAsync(Envelope envelope);

    // Used by DurableListener and LoopbackSendingAgent
    Task StoreIncomingAsync(Envelope[] envelopes);

    // DurableListener and DurableRetryAgent
    Task DeleteIncomingEnvelopesAsync(Envelope[] envelopes);

    // Used by DurableCallback
    Task DeleteIncomingEnvelopeAsync(Envelope envelope);

    void Describe(TextWriter writer);
    Task ScheduleJobAsync(Envelope envelope);

    Task<IReadOnlyList<Envelope>> LoadScheduledToExecuteAsync(DateTimeOffset utcNow);

    Task ReassignDormantNodeToAnyNodeAsync(int nodeId);
    Task<int[]> FindUniqueOwnersAsync(int currentNodeId);


    Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination);
    Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing);
    Task DeleteByDestinationAsync(Uri? destination);
    Task<Uri[]> FindAllDestinationsAsync();

    // Used by DurableRetryAgent, could go to IDurabilityAgent
    Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId);

    // Used by DurableSendingAgent, could go to durability agent
    Task StoreOutgoingAsync(Envelope envelope, int ownerId);

    // Used by DurableSendingAgent
    Task StoreOutgoingAsync(Envelope[] envelopes, int ownerId);

    // Used by DurableSendingAgent
    Task DeleteOutgoingAsync(Envelope[] envelopes);

    // Used by DurableSendingAgent
    Task DeleteOutgoingAsync(Envelope envelope);

    Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync();
    Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming);

    // TODO -- call this in system drain?
    Task ReleaseIncomingAsync(int ownerId);

    // TODO -- call from DurableReceiver.DrainAsync()
    Task ReleaseIncomingAsync(int ownerId, Uri receivedAt);

    Task<ErrorReport?> LoadDeadLetterEnvelopeAsync(Guid id);
}
