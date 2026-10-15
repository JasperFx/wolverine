using Raven.Client.Documents.Session;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals;

public class RavenDbEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly int _nodeId;
    private readonly RavenDbMessageStore _store;

    // GH-5016: owned by any node (0) under ServerlessWithOutbox, which has no node record to own them
    private readonly bool _ownedByAnyNode;

    public RavenDbEnvelopeTransaction(IAsyncDocumentSession session, MessageContext context)
    {
        if (context.Storage is RavenDbMessageStore store)
        {
            _store = store;
            _nodeId = context.Runtime.Options.Durability.AssignedNodeNumber;
            _ownedByAnyNode = context.Runtime.Options.Durability.Mode == DurabilityMode.ServerlessWithOutbox;
        }
        else
        {
            throw new InvalidOperationException(
                "This Wolverine application is not using RavenDb as the backing message persistence");
        }

        Session = session;
    }
    
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

    public IAsyncDocumentSession Session { get; }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        return Session.StoreAsync(buildOutgoing(envelope));
    }

    public async Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        foreach (var envelope in envelopes)
        {
            await Session.StoreAsync(buildOutgoing(envelope));
        }
    }

    private OutgoingMessage buildOutgoing(Envelope envelope)
    {
        var outgoing = new OutgoingMessage(envelope);
        if (_ownedByAnyNode)
        {
            outgoing.OwnerId = TransportConstants.AnyNode;
        }

        return outgoing;
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        return Session.StoreAsync(new IncomingMessage(envelope, _store));
    }

    public ValueTask RollbackAsync()
    {
        return ValueTask.CompletedTask;
    }
}