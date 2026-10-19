namespace PaymentsMonolith;

// Integration events for cross-module communication via durable local queues.
// Replaces: InMemoryEventBus + OutboxBroker + OutboxProcessor + InboxEventHandlerDecorator
// + ModuleClient + ModuleRegistry + Contract validation

public record UserCreated(Guid UserId, string Email, string FullName);
public record CustomerCompleted(Guid CustomerId, string Name, string FullName, string Nationality);
public record OwnerCreated(Guid OwnerId, string Name, string FullName);
public record WalletCreated(Guid WalletId, Guid OwnerId, string Currency);
public record FundsAdded(Guid WalletId, Guid OwnerId, string Currency, decimal Amount);
public record FundsDeducted(Guid WalletId, Guid OwnerId, string Currency, decimal Amount);
public record DepositCompleted(Guid DepositId, Guid CustomerId, string Currency, decimal Amount);
public record WithdrawalCompleted(Guid WithdrawalId, Guid CustomerId, string Currency, decimal Amount);
