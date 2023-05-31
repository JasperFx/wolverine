using Lamar;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence.Durability;

public interface IMessageInbox
{
    Task ScheduleExecutionAsync(Envelope envelope);
    Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception);
    Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope);
    Task StoreIncomingAsync(Envelope envelope);
    Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes);
    Task ScheduleJobAsync(Envelope envelope);
    
    Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope);
    
    // Good as is
    Task ReleaseIncomingAsync(int ownerId);

    // Good as is
    Task ReleaseIncomingAsync(int ownerId, Uri receivedAt);
}

public interface IMessageOutbox
{
    Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination);
    Task StoreOutgoingAsync(Envelope envelope, int ownerId);
    Task DeleteOutgoingAsync(Envelope[] envelopes);
    Task DeleteOutgoingAsync(Envelope envelope);

    Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId);
}

public interface IMessageStore : IAsyncDisposable
{
    /// <summary>
    /// Called to initialize the Wolverine storage on application bootstrapping
    /// </summary>
    /// <param name="runtime"></param>
    /// <returns></returns>
    Task InitializeAsync(IWolverineRuntime runtime);
    
    IMessageInbox Inbox { get; }
    
    IMessageOutbox Outbox { get; }
    
    INodeAgentPersistence Nodes { get; }

    [Obsolete("use IStatefulResource model instead")]
    IMessageStoreAdmin Admin { get; }

    void Describe(TextWriter writer);


    [Obsolete("Will have to have tenant id now?, or all dead letter queue goes to main DB?")]
    Task<ErrorReport?> LoadDeadLetterEnvelopeAsync(Guid id);
    
    Task DrainAsync();
}

public record IncomingCount(Uri Destination, int Count);