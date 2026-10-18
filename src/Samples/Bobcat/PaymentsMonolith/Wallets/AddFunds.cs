using FluentValidation;
using Marten;
using PaymentsMonolith;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Wallets;

public record AddFunds(Guid WalletId, decimal Amount, string TransferName = "deposit", string? Metadata = null)
{
    public class Validator : AbstractValidator<AddFunds>
    {
        public Validator()
        {
            RuleFor(x => x.WalletId).NotEmpty();
            RuleFor(x => x.Amount).GreaterThan(0);
        }
    }
}

public static class AddFundsEndpoint
{
    [WolverinePost("/api/wallets/{walletId}/funds/add")]
    public static (Wallet, FundsAdded) Post(
        AddFunds command,
        [Entity("WalletId", Required = true)] Wallet wallet,
        IDocumentSession session)
    {
        wallet.Balance += command.Amount;
        wallet.Transfers.Add(new Transfer
        {
            Name = command.TransferName,
            Type = "Incoming",
            Amount = command.Amount,
            Metadata = command.Metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        session.Store(wallet);

        return (wallet, new FundsAdded(wallet.Id, wallet.OwnerId, wallet.Currency, command.Amount));
    }
}
