using System.Collections.Concurrent;
using SagaWorkflows.MoneyTransfer;
using SagaWorkflows.MoneyTransfer.Fraud;

namespace SagaWorkflows.Providers;

/// <summary>The bank's systems, in memory, in the providers service. Knows nothing about sagas.</summary>
public class Bank
{
    private readonly ConcurrentDictionary<string, decimal> _balances = new();

    public ConcurrentBag<string> ClosedAccounts { get; } = [];

    // Transfers the ledger refuses to record, to make the last step fail
    public ConcurrentBag<Guid> LedgerRefuses { get; } = [];

    // Accounts whose large transfers look fraudulent
    public ConcurrentBag<string> FraudSuspects { get; } = [];

    public void Reset()
    {
        _balances.Clear();
        ClosedAccounts.Clear();
        LedgerRefuses.Clear();
        FraudSuspects.Clear();
    }

    public void Open(string account, decimal balance) => _balances[account] = balance;

    public decimal BalanceOf(string account) => _balances.GetValueOrDefault(account);

    public void Withdraw(string account, decimal amount)
    {
        if (BalanceOf(account) < amount) throw new InsufficientFundsException(account);
        _balances.AddOrUpdate(account, -amount, (_, balance) => balance - amount);
    }

    public void Deposit(string account, decimal amount)
    {
        if (ClosedAccounts.Contains(account)) throw new AccountClosedException(account);
        _balances.AddOrUpdate(account, amount, (_, balance) => balance + amount);
    }

    public void Record(Guid transferId)
    {
        if (LedgerRefuses.Contains(transferId)) throw new LedgerUnavailableException();
    }

    public void Screen(string account)
    {
        if (FraudSuspects.Contains(account)) throw new SuspectedFraudException(account);
    }
}

public static class BankHandler
{
    public static MoneyWithdrawn Handle(Withdraw command, Bank bank)
    {
        bank.Withdraw(command.Account, command.Amount);
        return new MoneyWithdrawn(command.TransferId);
    }

    public static MoneyDeposited Handle(Deposit command, Bank bank)
    {
        bank.Deposit(command.Account, command.Amount);
        return new MoneyDeposited(command.TransferId);
    }

    public static TransferRecorded Handle(RecordTransfer command, Bank bank)
    {
        bank.Record(command.TransferId);
        return new TransferRecorded(command.TransferId);
    }

    public static TransferScreened Handle(ScreenTransfer command, Bank bank)
    {
        bank.Screen(command.Account);
        return new TransferScreened(command.TransferId);
    }

    // The undo steps. Cancelling has to be idempotent, in a workflow engine too
    public static void Handle(RefundWithdrawal command, Bank bank) => bank.Deposit(command.Account, command.Amount);

    public static void Handle(ReverseDeposit command, Bank bank) => bank.Withdraw(command.Account, command.Amount);

    public static void Handle(TransferCompleted completed) { }

    public static void Handle(TransferFailed failed) { }
}
