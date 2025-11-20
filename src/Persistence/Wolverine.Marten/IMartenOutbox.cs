using Marten;
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
        
        session.Listeners.Add(new FlushOutgoingMessagesOnCommit(this, martenEnvelopeTransaction.Store));
    }

    public IDocumentSession? Session { get; private set; }
}