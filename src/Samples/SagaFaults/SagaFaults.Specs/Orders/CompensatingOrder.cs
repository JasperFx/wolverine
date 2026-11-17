using SagaFaults.Inventory;
using Wolverine;

namespace SagaFaults.Orders.Compensation;

// Chapter 2 (GH-5556): the same Order saga, compensating a failed step instead of handling its Fault<T> by hand.
// Compensate methods are selected by the failed message and the exception type, most specific exception first.
// Declaring a Compensate for ReserveInventory is what asks for the fault of every ReserveInventory this saga sends (the
// wolverine.fault.reply-to header), as Handle(Fault<ReserveInventory>) does in chapter 1.
// Each Compensate runs as a handler chain of its own, so error policies -- attributes on the method, or global ones --
// apply to it as to any handler. There is no separate retry setting for compensation.
public class Order : Saga, ICompensatingSaga
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Amount { get; set; }

    public bool InventoryReserved { get; set; }
    public bool Paid { get; set; }
    public bool Refunded { get; set; }

    // ICompensatingSaga: kept in the saga body, so no schema change
    public SagaStatus Status { get; set; } = SagaStatus.Active;
    public List<CompensationLogEntry> CompensationLog { get; set; } = [];

    public static (Order, ReserveInventory) Start(PlaceOrder command)
        => (new Order { Id = command.OrderId, Sku = command.Sku, Quantity = command.Quantity, Amount = command.Amount },
            new ReserveInventory(command.Sku, command.Quantity));

    public void Handle(InventoryReserved reserved) => InventoryReserved = true;

    // The saga's own handler. It fails when the ledger is down (retried, then compensated) or when the amount is wrong
    // (a terminal failure: compensated at once)
    public void Handle(PaymentReceived payment, Ledger ledger)
    {
        if (payment.Amount != Amount) throw new PaymentAmountMismatchException(Amount, payment.Amount);

        ledger.Record(Id, payment.Amount);
        Paid = true;
    }

    // ReserveInventory failed in the inventory service with OutOfStockException, or a subclass, after that service's
    // retries. Only an ExceptionInfo crosses the wire, so a failure in another service is a RemoteFailure<TException>,
    // matched through the exception's type hierarchy (GH-5555)
    public object[] Compensate(ReserveInventory failed, RemoteFailure<OutOfStockException> failure)
        => [new CancelOrder(Id), new NotifyCustomer(Id, $"{failed.Sku} is out of stock")];

    // Any other failure of that step, local or remote
    public RefundPayment Compensate(ReserveInventory failed, ExceptionInfo ex) => new(Id, Amount);

    // The saga's own handler failed, in this service: the live exception
    public RefundPayment Compensate(PaymentReceived failed, PaymentAmountMismatchException ex)
        => Refunded ? throw new RefundAlreadyIssuedException(Id) : new RefundPayment(Id, failed.Amount);

    public RefundPayment Compensate(PaymentReceived failed, LedgerUnavailableException ex) => new(Id, failed.Amount);

    // The saga is gone (completed or deleted) when a fault of its ReserveInventory arrives
    public static OrderAlreadyClosed NotFound(Fault<ReserveInventory> fault) => new(fault.Message.Sku);
}
