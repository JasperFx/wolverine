using JasperFx.Core.Descriptions;
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
    Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes);

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

public record DeadLetterEnvelopesFound(IReadOnlyList<DeadLetterEnvelope> DeadLetterEnvelopes, Guid? NextId, string? TenantId);
public record DeadLetterEnvelope(
    Guid Id,
    DateTimeOffset? ExecutionTime,
    Envelope Envelope,
    string MessageType,
    string ReceivedAt,
    string Source,
    string ExceptionType,
    string ExceptionMessage,
    DateTimeOffset SentAt,
    bool Replayable
    );

public interface IMessageStore : IAsyncDisposable
{
    /// <summary>
    /// Unique identifier for a message store in case of systems that use multiple message
    /// store databases. Must use the "messagedb" scheme, and reflect the database connection
    /// </summary>
    Uri Uri { get; }
    
    // /// <summary>
    // /// Let's consuming services in Wolverine know that this message store
    // /// has been disposed and cannot be used in a "DrainAsync". This mostly
    // /// happens when an IHost is disposed without being cleanly closed first
    // /// </summary>
    bool HasDisposed { get; }

    IMessageInbox Inbox { get; }

    IMessageOutbox Outbox { get; }

    INodeAgentPersistence Nodes { get; }

    IMessageStoreAdmin Admin { get; }

    IDeadLetters DeadLetters { get; }

    /// <summary>
    ///     Called to initialize the Wolverine storage on application bootstrapping
    /// </summary>
    /// <param name="runtime"></param>
    /// <returns></returns>
    void Initialize(IWolverineRuntime runtime);

    void Describe(TextWriter writer);

    Task DrainAsync();
    IAgent StartScheduledJobs(IWolverineRuntime runtime);

    IAgentFamily? BuildAgentFamily(IWolverineRuntime runtime);
    Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit);
    Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming);
    
    
    /// <summary>
    /// Descriptive name for cases of multiple message stores
    /// </summary>
    string Name { get; }
}

public record IncomingCount(Uri Destination, int Count);

/// <summary>
/// Marks a secondary message store for a Wolverine application
/// </summary>
public interface IAncillaryMessageStore : IMessageStore
{
    Type MarkerType { get; }
}

public interface IAncillaryMessageStore<T> : IAncillaryMessageStore
{
}

public interface ITenantedMessageStore
{
    DatabaseCardinality Cardinality { get; }
    ValueTask<IMessageStore> FindDatabaseAsync(string tenantId);
    Task RefreshAsync();
    IReadOnlyList<IMessageStore> AllActive();
}
