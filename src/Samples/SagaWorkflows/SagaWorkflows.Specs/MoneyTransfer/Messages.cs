using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;

namespace SagaWorkflows.MoneyTransfer;

// The money transfer of Temporal's .NET "Saga" sample (samples-dotnet/src/Saga): withdraw from one account, deposit to
// another, then record the transfer in the ledger. When a step fails for good, what already happened is undone.
// Every message carries the transfer id as its saga identity, so the same messages serve the document saga (Transfer)
// and the event-sourced one (EventSourcedTransfer).

public record TransferMoney([property: SagaIdentity] Guid TransferId, string FromAccount, string ToAccount, decimal Amount);

/// <summary>A step of a transfer: something the bank is asked to do, which may fail there.</summary>
public interface ITransferStep
{
    Guid TransferId { get; }
}

public record Withdraw([property: SagaIdentity] Guid TransferId, string Account, decimal Amount) : ITransferStep;

public record Deposit([property: SagaIdentity] Guid TransferId, string Account, decimal Amount) : ITransferStep;

public record RecordTransfer([property: SagaIdentity] Guid TransferId, string FromAccount, string ToAccount, decimal Amount)
    : ITransferStep;

// The bank's replies
public record MoneyWithdrawn([property: SagaIdentity] Guid TransferId);

public record MoneyDeposited([property: SagaIdentity] Guid TransferId);

public record TransferRecorded([property: SagaIdentity] Guid TransferId);

// The undo steps
public record RefundWithdrawal([property: SagaIdentity] Guid TransferId, string Account, decimal Amount);

public record ReverseDeposit([property: SagaIdentity] Guid TransferId, string Account, decimal Amount);

// How it ended
public record TransferCompleted([property: SagaIdentity] Guid TransferId);

public record TransferFailed([property: SagaIdentity] Guid TransferId, string Reason);

// ---- how a step fails at the bank ------------------------------------------------------------------------------------

// Not worth retrying: the step is compensated at once
[TerminalFailure]
public class InsufficientFundsException(string account) : Exception($"Account {account} has insufficient funds");

[TerminalFailure]
public class AccountClosedException(string account) : Exception($"Account {account} is closed");

// Worth retrying, then compensated
public class LedgerUnavailableException() : Exception("The ledger is unavailable");
