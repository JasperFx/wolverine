using JasperFx;
using SagaWorkflows.MoneyTransfer.Fraud;
using Wolverine;

namespace SagaWorkflows.MoneyTransfer;

// GH-5672: the money transfer as a document saga. Each step's reply moves the transfer on, and one Compensate method
// answers the failure of any step, because every step implements ITransferStep. Fault<T> is not covariant, so Wolverine
// files that method under the fault of every concrete ITransferStep it knows the saga sends -- Withdraw, Deposit and
// RecordTransfer -- and asks for those faults when the saga sends them.
// GH-5673: what to undo is the saga's undo stack. Each step pushes its undo once its reply says it happened, and a
// failure unwinds the stack newest first: no Withdrawn / Deposited flags, no hand-written undo() method.
#region sample_compensate_a_family_of_steps
public class Transfer : CompensatingSaga, IRevisioned
{
    // A transfer this large is screened for fraud before the money is deposited
    public const decimal ScreeningThreshold = 10_000m;

    public Guid Id { get; set; }
    public string FromAccount { get; set; } = "";
    public string ToAccount { get; set; } = "";
    public decimal Amount { get; set; }

    public static (Transfer, Withdraw) Start(TransferMoney command)
        => (new Transfer
            {
                Id = command.TransferId,
                FromAccount = command.FromAccount,
                ToAccount = command.ToAccount,
                Amount = command.Amount
            },
            new Withdraw(command.TransferId, command.FromAccount, command.Amount));

    public async Task<Deposit?> Handle(MoneyWithdrawn withdrawn, IMessageBus bus)
    {
        PushUndo(new RefundWithdrawal(Id, FromAccount, Amount), "withdrawal");
        if (Amount < ScreeningThreshold) return deposit();

        // Sent through the bus, not returned, so no signature shows that this saga sends a ScreenTransfer
        await bus.SendAsync(new ScreenTransfer(Id, FromAccount, Amount));
        return null;
    }

    public Deposit Handle(TransferScreened screened) => deposit();

    public RecordTransfer Handle(MoneyDeposited deposited)
    {
        PushUndo(new ReverseDeposit(Id, ToAccount, Amount), "deposit");
        return new RecordTransfer(Id, FromAccount, ToAccount, Amount);
    }

    public TransferCompleted Handle(TransferRecorded recorded)
    {
        MarkCompleted();
        return new TransferCompleted(Id);
    }

    // Any step that failed for good: undo what already happened, newest first. [Compensates] adds the one step
    // Wolverine cannot see this saga send
    [Compensates(typeof(ScreenTransfer))]
    public OutgoingMessages Compensate(ITransferStep failed, ExceptionInfo ex)
        => [..UndoMessages(), new TransferFailed(Id, ex.Message)];

    // An exact type beats an interface: a refused deposit gets its own explanation
    public OutgoingMessages Compensate(Deposit failed, ExceptionInfo ex)
        => [..UndoMessages(), new TransferFailed(Id, $"The deposit to {failed.Account} was refused: {ex.Message}")];

    private Deposit deposit() => new(Id, ToAccount, Amount);
}
#endregion
