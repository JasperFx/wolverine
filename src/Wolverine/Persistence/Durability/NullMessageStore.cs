using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Scheduled;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     Nullo implementation of a message store
/// </summary>
public class NullMessageStore : IMessageStore, IMessageInbox, IMessageOutbox, IMessageStoreAdmin, IDeadLetters
{
    internal IScheduledJobProcessor? ScheduledJobs { get; set; }

    public Uri Uri => new Uri($"{PersistenceConstants.AgentScheme}://null");

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        return Task.CompletedTask;
    }

    public string Name => "Nullo";

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
        if (envelope.Status == EnvelopeStatus.Scheduled)
        {
            if (envelope.ScheduledTime == null)
            {
                throw new ArgumentOutOfRangeException(
                    $"The envelope {envelope} is marked as Scheduled, but does not have an ExecutionTime");
            }

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

    public Task ScheduleJobAsync(Envelope envelope)
    {
        if (!envelope.ScheduledTime.HasValue)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope),
                $"Envelope does not have a value for {nameof(Envelope.ScheduledTime)}");
        }

        ScheduledJobs?.Enqueue(envelope.ScheduledTime!.Value, envelope);

        return Task.CompletedTask;
    }

    public Task ReleaseIncomingAsync(int ownerId)
    {
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
        throw new NotSupportedException();
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
    public INodeAgentPersistence Nodes => throw new NotSupportedException();

    public IMessageStoreAdmin Admin => this;

    public void Describe(TextWriter writer)
    {
        writer.WriteLine("No persistent envelope storage");
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
        throw new NotSupportedException();
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

    public Task ClearAllAsync()
    {
        return Task.CompletedTask;
    }

    public Task<int> MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType)
    {
        return Task.FromResult(0);
    }

    public Task MarkDeadLetterEnvelopesAsReplayableAsync(Guid[] ids, string? tenantId = null) => Task.CompletedTask;
    public Task DeleteDeadLetterEnvelopesAsync(Guid[] ids, string? tenantId = null) => Task.CompletedTask;

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
        throw new NotSupportedException();
    }

    public Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing)
    {
        throw new NotSupportedException();
    }

    public Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        throw new NotSupportedException();
    }

    public Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        throw new NotSupportedException();
    }

    public Task<DeadLetterEnvelopesFound> QueryDeadLetterEnvelopesAsync(DeadLetterEnvelopeQueryParameters queryParameters, string? tenantId)
    {
        throw new NotImplementedException();
    }

    public Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        throw new NotImplementedException();
    }
}

internal class NullNodeAgentPersistence : INodeAgentPersistence
{
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

    public Task<Guid?> MarkNodeAsLeaderAsync(Guid? originalLeader, Guid id)
    {
        return Task.FromResult(default(Guid?));
    }

    public Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return Task.FromResult(default(WolverineNode?));
    }

    public Task MarkHealthCheckAsync(Guid nodeId)
    {
        return Task.CompletedTask;
    }

    public Task MarkHealthCheckAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<int>> LoadAllNodeAssignedIdsAsync()
    {
        return Task.FromResult((IReadOnlyList<int>)Array.Empty<int>());
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