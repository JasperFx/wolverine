﻿using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence.Durability;

public enum MessageStoreRole
{
    /// <summary>
    /// Denotes that this message store is the main message store for the application and where
    /// node information is stored
    /// </summary>
    Main,
    
    /// <summary>
    /// Denotes that this message store is an additional message store for the application, but
    /// does not store the node information
    /// </summary>
    Ancillary,
    
    /// <summary>
    /// This message store is strictly for one or more tenants
    /// </summary>
    Tenant,
    
    /// <summary>
    /// This message store is a multi-tenanted composite of other message stores
    /// </summary>
    Composite
}

public interface IMessageInbox
{
    // This is *moving* an existing, persisted envelope in the inbox to being
    // scheduled for a retry
    Task ScheduleJobAsync(Envelope envelope);
    Task ScheduleExecutionAsync(Envelope envelope);
    Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception);
    Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope);
    Task StoreIncomingAsync(Envelope envelope);
    Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes);
    

    Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope);
    
    // Only called by DurableReceiver
    Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes);

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

public interface IMessageStoreWithAgentSupport : IMessageStore
{
    IAgent BuildAgent(IWolverineRuntime runtime);
}

public interface IMessageStore : IAsyncDisposable
{
    /// <summary>
    /// What is the role of this message store within the application?
    /// </summary>
    MessageStoreRole Role { get; }
    
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
    
    [Obsolete("Eliminate this in 4.0")]
    void Describe(TextWriter writer);

    DatabaseDescriptor Describe();

    Task DrainAsync();
    IAgent StartScheduledJobs(IWolverineRuntime runtime);

    Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit);
    Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming);
    
    
    /// <summary>
    /// Descriptive name for cases of multiple message stores
    /// </summary>
    string Name { get; }
    
    void PromoteToMain(IWolverineRuntime runtime);
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

public interface ITenantedMessageSource : ITenantedSource<IMessageStore>
{

}

