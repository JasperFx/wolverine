using Wolverine;

namespace EventSourcedSagas.Fulfillment;

// GH-5415: the OrderFulfillment process manager of the docs page, as one class. Its state is its own event stream, kept
// by Marten (or Polecat, or Fisher) with an Inline snapshot Wolverine registers for it.
//
// - Start returns events and messages, never the saga: the starting state comes only from Apply(OrderFulfillmentStarted).
// - Handle returns events (and messages) as an aggregate handler does. Wolverine appends the events to this saga's stream.
// - The saga completes only in the Apply of a terminal event. Wolverine checks for completion before any Handle runs, so
//   no handler carries a "has this already finished?" guard; a late message goes to NotFound.
// - Compensate returns events and messages too. Wolverine records the compensation itself, as events of its own on this
//   stream, and the EventSourcedSaga base type rebuilds Status and CompensationLog from them.
#region sample_event_sourced_saga
public class OrderFulfillment : EventSourcedSaga
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }

    public bool IsPaid { get; set; }
    public bool AreItemsReserved { get; set; }
    public bool IsShipped { get; set; }
    public bool IsRefunded { get; set; }
    public string? CancellationReason { get; set; }

    // ---- starting -------------------------------------------------------------------------------------------------

    public static (OrderFulfillmentStarted, PaymentTimeout) Start(StartOrderFulfillment command)
        => (new OrderFulfillmentStarted(command.OrderFulfillmentId, command.CustomerId, command.TotalAmount),
            new PaymentTimeout(command.OrderFulfillmentId));

    // Starts the fulfillment if it has not started, and is handled like any other message if it has
    public IEnumerable<object> StartOrHandle(OrderPlaced placed)
    {
        if (Id != Guid.Empty) yield break;

        yield return new OrderFulfillmentStarted(placed.OrderFulfillmentId, placed.CustomerId, placed.TotalAmount);
    }

    // ---- continuing -----------------------------------------------------------------------------------------------

    // The step's own guard against a duplicate delivery stays: it is reliable because the snapshot is written with the events
    public IEnumerable<object> Handle(PaymentReceived payment, PaymentLedger ledger)
    {
        if (IsPaid) return [];
        if (payment.Amount != TotalAmount) throw new PaymentAmountMismatchException(TotalAmount, payment.Amount);

        ledger.Record(Id, payment.Amount);
        return completingAfter(new PaymentConfirmed(payment.Amount), AreItemsReserved && IsShipped);
    }

    public IEnumerable<object> Handle(WarehouseReservedItems reserved)
        => AreItemsReserved ? [] : completingAfter(new ItemsReserved(reserved.ReservationId), IsPaid && IsShipped);

    public IEnumerable<object> Handle(CarrierConfirmedShipment shipment)
        => IsShipped ? [] : completingAfter(new ShipmentConfirmed(shipment.TrackingNumber), IsPaid && AreItemsReserved);

    public OrderFulfillmentCancelled Handle(CancelOrderFulfillment command) => new(command.Reason);

    public IEnumerable<object> Handle(PaymentTimeout timeout)
        => IsPaid ? [] : [new OrderFulfillmentCancelled("Payment timed out")];

    private static IEnumerable<object> completingAfter(object step, bool everyOtherStepIsDone)
        => everyOtherStepIsDone ? [step, new OrderFulfillmentCompleted()] : [step];

    // ---- after completion -----------------------------------------------------------------------------------------

    // A shipment confirmed for a fulfillment that has already completed or been cancelled
    public static ShipmentForClosedOrder NotFound(CarrierConfirmedShipment shipment)
        => new(shipment.OrderFulfillmentId, shipment.TrackingNumber);

    // ---- compensation ---------------------------------------------------------------------------------------------

    // The ledger stayed unavailable through the retries
    public (RefundIssued, OutgoingMessages) Compensate(PaymentReceived failed, LedgerUnavailableException ex)
        => refund(failed.Amount);

    // A wrong amount, compensated at once. Refunding twice is refused, which fails the compensation itself
    public (RefundIssued, OutgoingMessages) Compensate(PaymentReceived failed, PaymentAmountMismatchException ex)
        => IsRefunded ? throw new RefundAlreadyIssuedException(Id) : refund(failed.Amount);

    private (RefundIssued, OutgoingMessages) refund(decimal amount)
        => (new RefundIssued(amount), [new RefundPayment(Id, amount)]);

    // ---- the state, rebuilt from the stream -----------------------------------------------------------------------

    public void Apply(OrderFulfillmentStarted e)
    {
        Id = e.OrderFulfillmentId;
        CustomerId = e.CustomerId;
        TotalAmount = e.TotalAmount;
    }

    public void Apply(PaymentConfirmed e) => IsPaid = true;

    public void Apply(ItemsReserved e) => AreItemsReserved = true;

    public void Apply(ShipmentConfirmed e) => IsShipped = true;

    public void Apply(RefundIssued e) => IsRefunded = true;

    public void Apply(OrderFulfillmentCompleted e) => MarkCompleted();

    public void Apply(OrderFulfillmentCancelled e)
    {
        CancellationReason = e.Reason;
        MarkCompleted();
    }
}
#endregion
