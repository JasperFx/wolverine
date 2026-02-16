using Microsoft.Azure.Cosmos;
using Wolverine.CosmosDb.Internals;
using Wolverine.Runtime;

namespace Wolverine.CosmosDb;

public class CosmosDbOutbox : MessageContext, ICosmosDbOutbox
{
    public CosmosDbOutbox(IWolverineRuntime runtime, Container container) : base(runtime)
    {
        Enroll(container);
    }

    public void Enroll(Container container)
    {
        Container = container;
        Transaction = new CosmosDbEnvelopeTransaction(container, this);
    }

    /// <summary>
    /// Flush out any outgoing outbox'd messages
    /// </summary>
    /// <param name="cancellation"></param>
    public async Task SaveChangesAsync(CancellationToken cancellation = default)
    {
        await FlushOutgoingMessagesAsync();
    }

    public Container Container { get; private set; } = null!;
}
