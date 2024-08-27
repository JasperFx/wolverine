using Raven.Client.Documents;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IMessageStore
{
    private readonly IDocumentStore _store;
    

    public RavenDbMessageStore(IDocumentStore store)
    {
        _store = store;

        _leaderLockId = "wolverine/leader";
        _scheduledLockId = "wolverine/scheduled";
    }

    public ValueTask DisposeAsync()
    {
        // Assume that the RavenDb store is owned by the IoC container
        HasDisposed = true;
        return new ValueTask();
    }

    public bool HasDisposed { get; set; }
    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public INodeAgentPersistence Nodes => this;
    public IMessageStoreAdmin Admin => this;
    public IDeadLetters DeadLetters => this;
    public void Initialize(IWolverineRuntime runtime)
    {
        // NOTHING YET
    }

    public void Describe(TextWriter writer)
    {
        writer.WriteLine("RavenDb backed Wolverine envelope storage");
    }

    public Task DrainAsync()
    {
        return Task.CompletedTask;
    }

    public IAgent StartScheduledJobs(IWolverineRuntime runtime)
    {
        _leaderLockId = "wolverine/leader/" + runtime.Options.ServiceName.ToLowerInvariant();
        _scheduledLockId = _scheduledLockId + "/" + runtime.Options.ServiceName.ToLowerInvariant();
        _runtime = runtime;
        var agent =  new RavenDbDurabilityAgent(_store, runtime, this);
        agent.StartTimers();
        return agent;
    }

    public IAgentFamily? BuildAgentFamily(IWolverineRuntime runtime)
    {
        return null;
    }

    public async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        using var session = _store.OpenAsyncSession();
        var incoming = await session
            .Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.OwnerId == TransportConstants.AnyNode && x.ReceivedAt == listenerAddress && x.Status == EnvelopeStatus.Incoming)
            .OrderBy(x => x.EnvelopeId)
            .Take(limit)
            .ToListAsync();
        
        return incoming.Select(x => x.Read()).ToList();
    }
}