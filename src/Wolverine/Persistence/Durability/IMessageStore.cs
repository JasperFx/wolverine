using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence.Durability;

public enum MessageStoreRole
{
    /// <summary>
    ///     Denotes that this message store is the main message store for the application and where
    ///     node information is stored
    /// </summary>
    Main,

    /// <summary>
    ///     Denotes that this message store is an additional message store for the application, but
    ///     does not store the node information
    /// </summary>
    Ancillary,

    /// <summary>
    ///     This message store is strictly for one or more tenants
    /// </summary>
    Tenant,

    /// <summary>
    ///     This message store is a multi-tenanted composite of other message stores
    /// </summary>
    Composite
}

public interface IMessageInbox
{
    Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope);
    Task ScheduleExecutionAsync(Envelope envelope);
    Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception);
    Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope);
    Task StoreIncomingAsync(Envelope envelope);
    Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes);

    Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation);

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

public interface IMessageStoreWithAgentSupport : IMessageStore
{
    IAgent BuildAgent(IWolverineRuntime runtime);
}

public interface IMessageStore : IAsyncDisposable
{
    /// <summary>
    ///     What is the role of this message store within the application?
    /// </summary>
    MessageStoreRole Role { get; }
    
    /// <summary>
    /// In the case of multi-tenancy, this would hold one or more tenant ids
    /// </summary>
    List<string> TenantIds { get; }

    /// <summary>
    ///     Unique identifier for a message store in case of systems that use multiple message
    ///     store databases. Must use the "messagedb" scheme, and reflect the database connection
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
    ///     Descriptive name for cases of multiple message stores
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Called to initialize the Wolverine storage on application bootstrapping
    /// </summary>
    /// <param name="runtime"></param>
    /// <returns></returns>
    void Initialize(IWolverineRuntime runtime);

    DatabaseDescriptor Describe();

    Task DrainAsync();
    IAgent StartScheduledJobs(IWolverineRuntime runtime);

    Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit);
    Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming);

    void PromoteToMain(IWolverineRuntime runtime);
    void DemoteToAncillary();
}

public record IncomingCount(Uri Destination, int Count);

public class AncillaryMessageStoreApplication<T> 
{
    private readonly IMessageStore? _store;

    public AncillaryMessageStoreApplication(IWolverineRuntime runtime)
    {
        _store = runtime.Stores.FindAncillaryStore(typeof(T));
    }

    public void Apply(MessageContext context)
    {
        context.Storage = _store;
    }
}

public class AncillaryMessageStore
{
    public Type MarkerType { get; }
    public IMessageStore Inner { get; }

    public AncillaryMessageStore(Type markerType, IMessageStore inner)
    {
        MarkerType = markerType;
        Inner = inner;

        inner.DemoteToAncillary();
    }
}

public interface ITenantedMessageSource : ITenantedSource<IMessageStore>
{
}