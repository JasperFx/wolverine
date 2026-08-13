using Fisher;
using Wolverine.Runtime;

namespace Wolverine.Fisher;

/// <summary>
///     Outbox-ed messaging sending with Fisher
/// </summary>
public interface IFisherOutbox : IMessageBus
{
    /// <summary>
    ///     Current document session
    /// </summary>
    IDocumentSession? Session { get; }

    /// <summary>
    ///     Enroll a Fisher document session into the outbox'd sender
    /// </summary>
    /// <param name="session"></param>
    void Enroll(IDocumentSession session);
}

public class FisherOutbox : MessageContext, IFisherOutbox
{
    public FisherOutbox(IWolverineRuntime runtime, IDocumentSession session) : base(runtime)
    {
        Enroll(session);
    }

    public void Enroll(IDocumentSession session)
    {
        Session = session;
        var fisherEnvelopeTransaction = new FisherEnvelopeTransaction(session, this);
        Transaction = fisherEnvelopeTransaction;

        // Fisher requires listeners on SessionOptions before session creation,
        // so we use ITransactionParticipant for the outbox flush
        session.AddTransactionParticipant(new FlushOutgoingMessagesParticipant(this, fisherEnvelopeTransaction.Store));
    }

    public IDocumentSession? Session { get; private set; }
}
