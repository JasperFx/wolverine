using Wolverine.Runtime;
using Marten;

namespace Wolverine.Persistence.Marten.Publishing;

public class OutboxedSessionFactory
{
    private readonly ISessionFactory _factory;
    private readonly bool _shouldPublishEvents;

    public OutboxedSessionFactory(ISessionFactory factory, IWolverineRuntime runtime)
    {
        _factory = factory;
        _shouldPublishEvents = runtime.TryFindExtension<MartenIntegration>()?.ShouldPublishEvents ?? false;
    }

    /// <summary>Build new instances of IQuerySession on demand</summary>
    /// <returns></returns>
    public IQuerySession QuerySession(IMessageContext context)
    {
        return _factory.QuerySession();
    }

    /// <summary>Build new instances of IDocumentSession on demand</summary>
    /// <returns></returns>
    public IDocumentSession OpenSession(IMessageContext context)
    {
        var session = _factory.OpenSession();
        context.EnlistInOutbox(new MartenEnvelopeOutbox(session, context));

        if (_shouldPublishEvents)
        {
            session.Listeners.Add(new PublishIncomingEventsBeforeCommit(context));
        }

        session.Listeners.Add(new FlushOutgoingMessagesOnCommit(context));

        return session;
    }
}
