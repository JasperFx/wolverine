using SagaWorkflows.MoneyTransfer.Fraud;
using Wolverine;

namespace SagaWorkflows.MoneyTransfer;

// GH-5672: the same money transfer as an event-sourced saga. Its steps go out in OutgoingMessages, which no signature
// shows, but the workflows service routes them to the bank with PublishMessage<T>() rules, and that is enough for
// Wolverine to know them: the Compensate for ITransferStep covers Withdraw, Deposit and RecordTransfer here too.
// GH-5673: its undo stack is kept as Wolverine's own events on the saga's stream.

public record TransferStarted(Guid TransferId, string FromAccount, string ToAccount, decimal Amount);

public record FundsWithdrawn;

public record FundsDeposited;

public record TransferUndone(string Reason);

public class EventSourcedTransfer : EventSourcedSaga
{
    public Guid Id { get; set; }
    public string FromAccount { get; set; } = "";
    public string ToAccount { get; set; } = "";
    public decimal Amount { get; set; }

    public string? UndoneBecause { get; set; }

    public static (TransferStarted, OutgoingMessages) Start(TransferMoney command)
        => (new TransferStarted(command.TransferId, command.FromAccount, command.ToAccount, command.Amount),
            [new Withdraw(command.TransferId, command.FromAccount, command.Amount)]);

    public async Task<(FundsWithdrawn, OutgoingMessages)> Handle(MoneyWithdrawn withdrawn, IMessageBus bus)
    {
        PushUndo(new RefundWithdrawal(Id, FromAccount, Amount), "withdrawal");
        if (Amount < Transfer.ScreeningThreshold) return (new FundsWithdrawn(), [deposit()]);

        // Sent through the bus, and routed by a namespace rule: Wolverine cannot know this saga sends it
        await bus.SendAsync(new ScreenTransfer(Id, FromAccount, Amount));
        return (new FundsWithdrawn(), []);
    }

    public OutgoingMessages Handle(TransferScreened screened) => [deposit()];

    public (FundsDeposited, OutgoingMessages) Handle(MoneyDeposited deposited)
    {
        PushUndo(new ReverseDeposit(Id, ToAccount, Amount), "deposit");
        return (new FundsDeposited(), [new RecordTransfer(Id, FromAccount, ToAccount, Amount)]);
    }

    public (SagaCompleted, OutgoingMessages) Handle(TransferRecorded recorded)
        => (new SagaCompleted(), [new TransferCompleted(Id)]);

    [Compensates(typeof(ScreenTransfer))]
    public (TransferUndone, OutgoingMessages) Compensate(ITransferStep failed, ExceptionInfo ex) => undo(ex.Message);

    public (TransferUndone, OutgoingMessages) Compensate(Deposit failed, ExceptionInfo ex)
        => undo($"The deposit to {failed.Account} was refused: {ex.Message}");

    private Deposit deposit() => new(Id, ToAccount, Amount);

    private (TransferUndone, OutgoingMessages) undo(string reason)
        => (new TransferUndone(reason), [..UndoMessages(), new TransferFailed(Id, reason)]);

    public void Apply(TransferStarted e)
    {
        Id = e.TransferId;
        FromAccount = e.FromAccount;
        ToAccount = e.ToAccount;
        Amount = e.Amount;
    }

    // Kept as business facts; the undo stack, not these, decides what a failure undoes
    public void Apply(FundsWithdrawn e) { }

    public void Apply(FundsDeposited e) { }

    public void Apply(TransferUndone e) => UndoneBecause = e.Reason;
}
