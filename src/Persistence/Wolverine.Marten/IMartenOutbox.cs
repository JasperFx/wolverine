using Marten;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
///     Outbox-ed messaging sending with Marten
/// </summary>
public interface IMartenOutbox : IMessagePublisher
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

internal class MartenOutbox : MessageContext, IMartenOutbox
{
    public MartenOutbox(IWolverineRuntime runtime, IDocumentSession session) : base(runtime)
    {
        Enroll(session);
    }

    public void Enroll(IDocumentSession session)
    {
        Session = session;
        Transaction = new MartenEnvelopeTransaction(session, this);

        session.Listeners.Add(new FlushOutgoingMessagesOnCommit(this));
    }

    public IDocumentSession? Session { get; private set; }
}