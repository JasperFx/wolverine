using Polecat;
using Wolverine.Runtime;

namespace Wolverine.Polecat;

/// <summary>
///     Outbox-ed messaging sending with Polecat
/// </summary>
public interface IPolecatOutbox : IMessageBus
{
    /// <summary>
    ///     Current document session
    /// </summary>
    IDocumentSession? Session { get; }

    /// <summary>
    ///     Enroll a Polecat document session into the outbox'd sender
    /// </summary>
    /// <param name="session"></param>
    void Enroll(IDocumentSession session);
}

public class PolecatOutbox : MessageContext, IPolecatOutbox
{
    public PolecatOutbox(IWolverineRuntime runtime, IDocumentSession session) : base(runtime)
    {
        Enroll(session);
    }

    public void Enroll(IDocumentSession session)
    {
        Session = session;
        var polecatEnvelopeTransaction = new PolecatEnvelopeTransaction(session, this);
        Transaction = polecatEnvelopeTransaction;

        // Polecat requires listeners on SessionOptions before session creation, and this session is already
        // open, so the store's FlushEnrolledOutboxOnCommit runs this listener for it
        FlushEnrolledOutboxOnCommit.Enroll(session,
            new FlushOutgoingMessagesOnCommit(this, polecatEnvelopeTransaction.Store));
        session.AddTransactionParticipant(new KeepSaveChangesRunning());
    }

    public IDocumentSession? Session { get; private set; }
}
