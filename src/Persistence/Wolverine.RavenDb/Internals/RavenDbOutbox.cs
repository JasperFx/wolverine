using Raven.Client.Documents.Session;
using Wolverine.Runtime;

namespace Wolverine.RavenDb.Internals;

public class RavenDbOutbox : MessageContext, IRavenDbOutbox
{
    public RavenDbOutbox(IWolverineRuntime runtime, IAsyncDocumentSession session) : base(runtime)
    {
        Enroll(session);
    }

    public void Enroll(IAsyncDocumentSession session)
    {
        Session = session;
        Transaction = new RavenDbEnvelopeTransaction(session, this);
    }

    /// <summary>
    /// Commit the session unit of work and flush out any outgoing outbox'd messages
    /// </summary>
    /// <param name="cancellation"></param>
    public async Task SaveChangesAsync(CancellationToken cancellation = default)
    {
        await Session.SaveChangesAsync(cancellation);
        await FlushOutgoingMessagesAsync();
    }

    public IAsyncDocumentSession Session { get; private set; }
}