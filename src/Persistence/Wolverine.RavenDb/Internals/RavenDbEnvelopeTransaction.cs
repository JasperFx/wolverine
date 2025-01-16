using Raven.Client.Documents.Session;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.RavenDb.Internals;

public class RavenDbEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly int _nodeId;
    private readonly RavenDbMessageStore _store;

    public RavenDbEnvelopeTransaction(IAsyncDocumentSession session, MessageContext context)
    {
        if (context.Storage is RavenDbMessageStore store)
        {
            _store = store;
            _nodeId = context.Runtime.Options.Durability.AssignedNodeNumber;
        }
        else
        {
            throw new InvalidOperationException(
                "This Wolverine application is not using RavenDb as the backing message persistence");
        }

        Session = session;
    }

    public IAsyncDocumentSession Session { get; }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        return Session.StoreAsync(new OutgoingMessage(envelope));
    }

    public async Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        foreach (var envelope in envelopes)
        {
            await Session.StoreAsync(new OutgoingMessage(envelope));
        }
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