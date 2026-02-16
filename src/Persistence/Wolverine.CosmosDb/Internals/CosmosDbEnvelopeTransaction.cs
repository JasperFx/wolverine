using Microsoft.Azure.Cosmos;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.CosmosDb.Internals;

public class CosmosDbEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly int _nodeId;
    private readonly CosmosDbMessageStore _store;

    public CosmosDbEnvelopeTransaction(Container container, MessageContext context)
    {
        if (context.Storage is CosmosDbMessageStore store)
        {
            _store = store;
            _nodeId = context.Runtime.Options.Durability.AssignedNodeNumber;
        }
        else
        {
            throw new InvalidOperationException(
                "This Wolverine application is not using CosmosDb as the backing message persistence");
        }

        Container = container;
    }

    public Container Container { get; }

    public async Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, DurabilitySettings settings,
        CancellationToken cancellation)
    {
        var copy = Envelope.ForPersistedHandled(envelope, DateTimeOffset.UtcNow, settings);
        try
        {
            await PersistIncomingAsync(copy);
            envelope.WasPersistedInInbox = true;
            envelope.Status = EnvelopeStatus.Handled;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task PersistOutgoingAsync(Envelope envelope)
    {
        var outgoing = new OutgoingMessage(envelope);
        await Container.UpsertItemAsync(outgoing, new PartitionKey(outgoing.PartitionKey));
    }

    public async Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        foreach (var envelope in envelopes)
        {
            await PersistOutgoingAsync(envelope);
        }
    }

    public async Task PersistIncomingAsync(Envelope envelope)
    {
        var incoming = new IncomingMessage(envelope, _store);
        await Container.UpsertItemAsync(incoming, new PartitionKey(incoming.PartitionKey));
    }

    public ValueTask RollbackAsync()
    {
        return ValueTask.CompletedTask;
    }
}
