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

public record DeadLetterEnvelopesFound(IReadOnlyList<DeadLetterEnvelope> DeadLetterEnvelopes, Guid NextId, string? TenantId);
public record DeadLetterEnvelope(Envelope Envelope, string ExceptionType, string ExceptionMessage);

public class DeadLetterEnvelopeQueryParameters
{
    public uint Limit { get; set; } = 100;
    public Guid? StartId { get; set; }
    public string? MessageType { get; set; }
    public string? ExceptionType { get; set; }
    public DateTime? From { get; set; }
    public DateTime? Until { get; set; }
}

public interface IDeadLetters
{
    Task<DeadLetterEnvelopesFound> QueryDeadLetterEnvelopesAsync(DeadLetterEnvelopeQueryParameters queryParameters, string tenantId);

    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null);
}

public interface IMessageStore : IAsyncDisposable
{
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
}

public record IncomingCount(Uri Destination, int Count);
