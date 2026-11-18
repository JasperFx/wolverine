namespace SagaFaults.Orders;

// The orders service's messages, shared by both chapters' Order saga

public record PlaceOrder(Guid OrderId, string Sku, int Quantity, decimal Amount);

// Payment by card, handled in the orders service itself. Neither carries a saga id member: like ReserveInventory, they
// are tied to the Order saga by the saga-id header (GH-4484)
public record ChargeCard(decimal Amount);

public record CardCharged(decimal Amount);

public class CardDeclinedException(decimal amount) : Exception($"The card was declined for {amount}");

public static class CardPayments
{
    public const decimal Limit = 1000m;

    public static CardCharged Handle(ChargeCard command)
        => command.Amount > Limit ? throw new CardDeclinedException(command.Amount) : new CardCharged(command.Amount);
}

// What a Fault<ReserveInventory> that belongs to no Order turns into (GH-4484's IdNotProvided)
public record UnmatchedInventoryFailure(string Sku);

// What a CardCharged that belongs to no Order turns into (IdNotProvided for any message type)
public record UnmatchedPayment(decimal Amount);

// A stock check orders makes outside any saga, asking for its fault explicitly (GH-5553's FaultTo / ReturnFaultToSender)
public static class StockChecks
{
    public static void Handle(SagaFaults.Inventory.StockChecked checkedStock)
    {
    }

    public static void Handle(Wolverine.Fault<SagaFaults.Inventory.CheckStock> fault)
    {
    }
}

// Chapter 2: compensation

public record PaymentReceived(Guid OrderId, decimal Amount);

public record RefundPayment(Guid OrderId, decimal Amount);

public record CancelOrder(Guid OrderId);

public record NotifyCustomer(Guid OrderId, string Message);

public record OrderAlreadyClosed(string Sku);

// The order's ledger. A payment the ledger cannot record is retried, and then compensated
public class Ledger
{
    public bool Available { get; set; } = true;

    public void Record(Guid orderId, decimal amount)
    {
        if (!Available) throw new LedgerUnavailableException();
    }
}

public class LedgerUnavailableException() : Exception("The ledger is unavailable");

// Retrying cannot make a wrong amount right: a terminal failure (GH-5556)
[Wolverine.ErrorHandling.TerminalFailure]
public class PaymentAmountMismatchException(decimal expected, decimal received)
    : Exception($"Expected a payment of {expected}, but received {received}");

public class RefundAlreadyIssuedException(Guid orderId) : Exception($"Order {orderId} has already been refunded");

// Where the messages compensation sends end up. Nothing to do in a specification
public static class Storefront
{
    public static void Handle(UnmatchedInventoryFailure failure) { }
    public static void Handle(UnmatchedPayment payment) { }
    public static void Handle(RefundPayment refund) { }
    public static void Handle(CancelOrder cancel) { }
    public static void Handle(NotifyCustomer notify) { }
    public static void Handle(OrderAlreadyClosed closed) { }
}
