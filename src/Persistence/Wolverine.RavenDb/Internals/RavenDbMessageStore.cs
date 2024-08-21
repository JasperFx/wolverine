using Raven.Client.Documents;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IMessageStore
{
    private readonly IDocumentStore _store;

    public RavenDbMessageStore(IDocumentStore store)
    {
        _store = store;
    }

    public async ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public bool HasDisposed { get; set; }
    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public INodeAgentPersistence Nodes => this;
    public IMessageStoreAdmin Admin => this;
    public IDeadLetters DeadLetters => this;
    public void Initialize(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public void Describe(TextWriter writer)
    {
        throw new NotImplementedException();
    }

    public async Task DrainAsync()
    {
        throw new NotImplementedException();
    }

    public IAgent StartScheduledJobs(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public IAgentFamily? BuildAgentFamily(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        throw new NotImplementedException();
    }
}