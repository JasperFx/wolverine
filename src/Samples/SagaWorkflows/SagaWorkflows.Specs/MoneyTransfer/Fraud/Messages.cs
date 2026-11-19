using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;

namespace SagaWorkflows.MoneyTransfer.Fraud;

// Fraud screening for a large transfer. The workflows service routes everything in this namespace to the bank with one
// namespace rule, and a transfer sends ScreenTransfer through IMessageBus rather than returning it. So no signature and
// no PublishMessage<T>() rule tells Wolverine that a transfer sends a ScreenTransfer: [Compensates] has to.

public record ScreenTransfer([property: SagaIdentity] Guid TransferId, string Account, decimal Amount)
    : ITransferStep;

public record TransferScreened([property: SagaIdentity] Guid TransferId);

[TerminalFailure]
public class SuspectedFraudException(string account) : Exception($"A transfer from {account} looks fraudulent");
