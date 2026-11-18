using SagaWorkflows.MoneyTransfer.Fraud;
using Wolverine;

namespace SagaWorkflows.MoneyTransfer;

// GH-5672: the same money transfer as an event-sourced saga. Its steps go out in OutgoingMessages, which no signature
// shows, but the workflows service routes them to the bank with PublishMessage<T>() rules, and that is enough for
// Wolverine to know them: the Compensate for ITransferStep covers Withdraw, Deposit and RecordTransfer here too.

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

    public bool Withdrawn { get; set; }
    public bool Deposited { get; set; }
    public string? UndoneBecause { get; set; }

    public static (TransferStarted, OutgoingMessages) Start(TransferMoney command)
        => (new TransferStarted(command.TransferId, command.FromAccount, command.ToAccount, command.Amount),
            [new Withdraw(command.TransferId, command.FromAccount, command.Amount)]);

    public async Task<(FundsWithdrawn, OutgoingMessages)> Handle(MoneyWithdrawn withdrawn, IMessageBus bus)
    {
        if (Amount < Transfer.ScreeningThreshold) return (new FundsWithdrawn(), [deposit()]);

        // Sent through the bus, and routed by a namespace rule: Wolverine cannot know this saga sends it
        await bus.SendAsync(new ScreenTransfer(Id, FromAccount, Amount));
        return (new FundsWithdrawn(), []);
    }

    public OutgoingMessages Handle(TransferScreened screened) => [deposit()];

    public (FundsDeposited, OutgoingMessages) Handle(MoneyDeposited deposited)
        => (new FundsDeposited(), [new RecordTransfer(Id, FromAccount, ToAccount, Amount)]);

    public (SagaCompleted, OutgoingMessages) Handle(TransferRecorded recorded)
        => (new SagaCompleted(), [new TransferCompleted(Id)]);

    [Compensates(typeof(ScreenTransfer))]
    public (TransferUndone, OutgoingMessages) Compensate(ITransferStep failed, ExceptionInfo ex) => undo(ex.Message);

    public (TransferUndone, OutgoingMessages) Compensate(Deposit failed, ExceptionInfo ex)
        => undo($"The deposit to {failed.Account} was refused: {ex.Message}");

    private Deposit deposit() => new(Id, ToAccount, Amount);

    private (TransferUndone, OutgoingMessages) undo(string reason)
    {
        var messages = new OutgoingMessages();
        if (Deposited) messages.Add(new ReverseDeposit(Id, ToAccount, Amount));
        if (Withdrawn) messages.Add(new RefundWithdrawal(Id, FromAccount, Amount));
        messages.Add(new TransferFailed(Id, reason));

        return (new TransferUndone(reason), messages);
    }

    public void Apply(TransferStarted e)
    {
        Id = e.TransferId;
        FromAccount = e.FromAccount;
        ToAccount = e.ToAccount;
        Amount = e.Amount;
    }

    public void Apply(FundsWithdrawn e) => Withdrawn = true;

    public void Apply(FundsDeposited e) => Deposited = true;

    public void Apply(TransferUndone e) => UndoneBecause = e.Reason;
}
