using Marten;
using Wolverine.Marten.Publishing;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
///     Outbox-ed messaging sending with Marten
/// </summary>
public interface IMartenOutbox : IMessageBus
{
    /// <summary>
    ///     Current document session
    /// </summary>
    IDocumentSession? Session { get; }

    /// <summary>
    ///     Enroll a Marten document session into the outbox'd sender
    /// </summary>
    /// <param name="session"></param>
    void Enroll(IDocumentSession session);
}

public class MartenOutbox : MessageContext, IMartenOutbox
{
    public MartenOutbox(IWolverineRuntime runtime, IDocumentSession session) : base(runtime)
    {
        Enroll(session);
    }

    public void Enroll(IDocumentSession session)
    {
        Session = session;
        var martenEnvelopeTransaction = new MartenEnvelopeTransaction(session, this);
        Transaction = martenEnvelopeTransaction;
        
        // The same listeners as a session opened by the OutboxedSessionFactory, so an enrolled session
        // also forwards its events when that's enabled
        OutboxedSessionFactory.AddOutboxListeners(session, this, martenEnvelopeTransaction,
            Runtime.TryFindExtension<MartenIntegration>()?.UseFastEventForwarding ?? false,
            Runtime.Options.Tracking.EnableEventAppendTracking);
    }

    public IDocumentSession? Session { get; private set; }
}