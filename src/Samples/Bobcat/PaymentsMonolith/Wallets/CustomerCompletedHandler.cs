using Marten;
using PaymentsMonolith;

namespace Wallets;

/// <summary>
/// Handles CustomerCompleted from the Customers module.
/// Creates a wallet for the newly completed customer.
/// Replaces: Contract + InboxEventHandlerDecorator + IEventHandler + IWalletRepository
/// </summary>
public static class CustomerCompletedHandler
{
    public static WalletCreated Handle(CustomerCompleted message, IDocumentSession session)
    {
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            OwnerId = message.CustomerId,
            OwnerName = message.FullName,
            Currency = "PLN",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(wallet);

        return new WalletCreated(wallet.Id, wallet.OwnerId, wallet.Currency);
    }
}
