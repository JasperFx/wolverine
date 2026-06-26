using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Descriptors;
using Wolverine.Logging;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Scheduled;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     Nullo implementation of a message store
/// </summary>
public class NullMessageStore : IMessageStore, IMessageInbox, IMessageOutbox, IMessageStoreAdmin, IDeadLetters, IScheduledMessages
{
    internal IScheduledJobProcessor? ScheduledJobs { get; set; }

    public Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        return Task.FromResult(false);
    }

    public MessageStoreRole Role => MessageStoreRole.Main;
    public Uri Uri => new Uri($"{PersistenceConstants.AgentScheme}://null");

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        return Task.CompletedTask;
    }
    
    public List<string> TenantIds { get; } = new();

    public string Name => "Nullo";
    public void PromoteToMain(IWolverineRuntime runtime)
    {
        
    }

    public void DemoteToAncillary()
    {
        
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        return Task.CompletedTask;
    }

    public IAgentFamily? BuildAgentFamily(IWolverineRuntime runtime)
    {
        return null;
    }

    public Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        return Task.CompletedTask;
    }

    public Task ScheduleExecutionAsync(Envelope envelope)
    {
        return Task.CompletedTask;
    }

    public Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        return Task.CompletedTask;
    }

    public Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        return Task.CompletedTask;
    }

    public Task StoreIncomingAsync(Envelope envelope)
    {
        // A no-op store never throws; just schedule in memory when we can and otherwise do nothing.
        if (envelope.Status == EnvelopeStatus.Scheduled && envelope.ScheduledTime != null)
        {
            ScheduledJobs?.Enqueue(envelope.ScheduledTime.Value, envelope);
        }

        return Task.CompletedTask;
    }

    public Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        foreach (var envelope in envelopes.Where(x => x.Status == EnvelopeStatus.Scheduled))
            ScheduledJobs?.Enqueue(envelope.ScheduledTime!.Value, envelope);

        return Task.CompletedTask;
    }

    public Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        if (envelope.ScheduledTime.HasValue)
        {
            ScheduledJobs?.Enqueue(envelope.ScheduledTime.Value, envelope);
        }

        return Task.CompletedTask;
    }

    public Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        return Task.CompletedTask;
    }

    public Task DeleteOutgoingAsync(Envelope envelope)
    {
        return Task.CompletedTask;
    }

    public Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        return Task.FromResult((IReadOnlyList<Envelope>)Array.Empty<Envelope>());
    }

    public Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        return Task.CompletedTask;
    }

    public void Initialize(IWolverineRuntime runtime)
    {
    }

    public bool HasDisposed { get; set; }
    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public IDeadLetters DeadLetters => this;
    public IScheduledMessages ScheduledMessages => this;
    // A no-op node persistence rather than throwing: observers / CritterWatch call this to record node
    // lifecycle and must not blow up on a storeless (Solo / NullMessageStore) host.
    public INodeAgentPersistence Nodes => NullNodeAgentPersistence.Instance;

    // No durable backing → no dynamic-listener registry. Solo-mode hosts that
    // *do* want dynamic listeners need a real message store.
    public IListenerStore Listeners => NullListenerStore.Instance;

    public IMessageStoreAdmin Admin => this;

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "DatabaseDescriptor(subject) reads subject's runtime-type properties for diagnostic reporting. NullMessageStore properties trimmed away are silently omitted, which is acceptable for this diagnostic surface.")]
    public DatabaseDescriptor Describe()
    {
        return new DatabaseDescriptor(this);
    }

    public ValueTask DisposeAsync()
    {
        HasDisposed = true;
        return ValueTask.CompletedTask;
    }

    public Task DrainAsync()
    {
        return Task.CompletedTask;
    }

    public IAgent StartScheduledJobs(IWolverineRuntime wolverineRuntime)
    {
        // In-memory scheduled jobs are wired separately (WolverineRuntime.startInMemoryScheduledJobs), so
        // there is no durable scheduled-job agent to run here — return a no-op agent rather than throwing.
        return new CompositeAgent(new Uri($"{PersistenceConstants.AgentScheme}://scheduledjobs/null"),
            Array.Empty<IAgent>());
    }

    public Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        return Task.FromResult((IReadOnlyList<Envelope>)Array.Empty<Envelope>());
    }

    public Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        return Task.FromResult((IReadOnlyList<Envelope>)Array.Empty<Envelope>());
    }

    public Task ReleaseAllOwnershipAsync()
    {
        return Task.CompletedTask;
    }

    public Task ReleaseAllOwnershipAsync(int ownerId)
    {
        return Task.CompletedTask;
    }

    public Task CheckConnectivityAsync(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task MigrateAsync()
    {
        return Task.CompletedTask;
    }

    public Task<PersistedCounts> FetchCountsAsync()
    {
        // Nothing to do, but keeps the metrics from blowing up
        return Task.FromResult(new PersistedCounts());
    }

    public Task DeleteAllHandledAsync()
    {
        return Task.CompletedTask;
    }

    public Task ClearAllAsync()
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range, CancellationToken token)
    {
        return Task.FromResult<IReadOnlyList<DeadLetterQueueCount>>(new List<DeadLetterQueueCount>());
    }

    public Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return Task.FromResult(new DeadLetterEnvelopeResults());
    }

    public Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task RebuildAsync()
    {
        return Task.CompletedTask;
    }

    public Task DeleteIncomingEnvelopesAsync(Envelope[] envelopes)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Envelope>> LoadScheduledToExecuteAsync(DateTimeOffset utcNow)
    {
        return Task.FromResult((IReadOnlyList<Envelope>)Array.Empty<Envelope>());
    }

    public Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        return Task.FromResult((IReadOnlyList<Envelope>)Array.Empty<Envelope>());
    }

    public Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        return Task.CompletedTask;
    }

    public Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        return Task.FromResult<DeadLetterEnvelope?>(null);
    }

    Task<ScheduledMessageResults> IScheduledMessages.QueryAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        return Task.FromResult(new ScheduledMessageResults());
    }

    public Task CancelAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScheduledMessageCount>> SummarizeAsync(string serviceName, CancellationToken token)
    {
        return Task.FromResult<IReadOnlyList<ScheduledMessageCount>>(new List<ScheduledMessageCount>());
    }
}

internal class NullNodeAgentPersistence : INodeAgentPersistence
{
    public static readonly NullNodeAgentPersistence Instance = new();

    public Task ClearAllAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        return Task.FromResult(0);
    }

    public Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult((IReadOnlyList<WolverineNode>)Array.Empty<WolverineNode>());
    }

    public Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new NodeAgentState([], new AgentRestrictions([])));
    }

    public Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return Task.FromResult(default(WolverineNode?));
    }

    public Task MarkHealthCheckAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        return Task.CompletedTask;
    }

    public Task LogRecordsAsync(params NodeRecord[] records)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count)
    {
        return Task.FromResult((IReadOnlyList<NodeRecord>)Array.Empty<NodeRecord>());
    }

    public bool HasLeadershipLock()
    {
        return false;
    }

    public Task<bool> TryAttainLeadershipLockAsync(CancellationToken token)
    {
        return Task.FromResult(false);
    }

    public Task ReleaseLeadershipLockAsync()
    {
        return Task.CompletedTask;
    }
}