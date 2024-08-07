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

public class DeadLetterEnvelopeQueryParameters
{
    public uint Limit { get; set; } = 100;
    public Guid? StartId { get; set; }
    public string? MessageType { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? Until { get; set; }
}

public interface IDeadLetters
{
    Task<DeadLetterEnvelopesFound> QueryDeadLetterEnvelopesAsync(DeadLetterEnvelopeQueryParameters queryParameters, string? tenantId = null);

    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null);

    /// <summary>
    ///     Marks the Envelopes in DeadLetterTable
    ///     as replayable. DurabilityAgent will move the envelopes to IncomingTable.
    /// </summary>
    /// <param name="exceptionType">Exception Type that should be marked. Default is any.</param>
    /// <returns>Number of envelopes marked.</returns>
    Task<int> MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType = "");

    /// <summary>
    ///     Marks the Envelope in DeadLetterTable
    ///     as replayable. DurabilityAgent will move the envelopes to IncomingTable.
    /// </summary>
    /// <param name="ids"></param>
    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    Task MarkDeadLetterEnvelopesAsReplayableAsync(Guid[] ids, string? tenantId = null);

    /// <summary>
    /// Deletes the DeadLetterEnvelope from the DeadLetterTable
    /// </summary>
    /// <param name="ids"></param>
    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    Task DeleteDeadLetterEnvelopesAsync(Guid[] ids, string? tenantId = null);
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

    IAgentFamily? BuildAgentFamily(IWolverineRuntime runtime);
    Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit);
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
