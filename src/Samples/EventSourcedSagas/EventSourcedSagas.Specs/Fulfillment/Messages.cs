using JasperFx.Core;
using Wolverine;

namespace EventSourcedSagas.Fulfillment;

// The OrderFulfillment process of the "Process Manager via Handlers" docs page, as an event-sourced saga (GH-5415).

// ---- messages the saga receives ----------------------------------------------------------------------------------------

public record StartOrderFulfillment(Guid OrderFulfillmentId, Guid CustomerId, decimal TotalAmount);

// The storefront's own announcement of the same order. It may arrive before or after StartOrderFulfillment
public record OrderPlaced(Guid OrderFulfillmentId, Guid CustomerId, decimal TotalAmount);

public record PaymentReceived(Guid OrderFulfillmentId, decimal Amount);

// The warehouse's reply carries no saga id member: it is tied to the OrderFulfillment by the saga-id header
public record WarehouseReservedItems(Guid ReservationId);

public record CarrierConfirmedShipment(Guid OrderFulfillmentId, string TrackingNumber);

public record CancelOrderFulfillment(Guid OrderFulfillmentId, string Reason);

public record PaymentTimeout(Guid OrderFulfillmentId) : TimeoutMessage(15.Minutes());

// ---- the saga's events, appended to its own stream ----------------------------------------------------------------------

public record OrderFulfillmentStarted(Guid OrderFulfillmentId, Guid CustomerId, decimal TotalAmount);

public record PaymentConfirmed(decimal Amount);

public record ItemsReserved(Guid ReservationId);

public record ShipmentConfirmed(string TrackingNumber);

public record RefundIssued(decimal Amount);

// The terminal events: the only places the saga completes
public record OrderFulfillmentCompleted;

public record OrderFulfillmentCancelled(string Reason);

// ---- messages the saga sends ------------------------------------------------------------------------------------------

public record RefundPayment(Guid OrderFulfillmentId, decimal Amount);

public record ShipmentForClosedOrder(Guid OrderFulfillmentId, string TrackingNumber);

// ---- the payment ledger, and how a payment fails ------------------------------------------------------------------------

// A payment the ledger cannot record is retried, and then compensated
public class PaymentLedger
{
    public bool Available { get; set; } = true;

    public void Record(Guid orderFulfillmentId, decimal amount)
    {
        if (!Available) throw new LedgerUnavailableException();
    }
}

public class LedgerUnavailableException() : Exception("The payment ledger is unavailable");

// Retrying cannot make a wrong amount right: compensated at once
[Wolverine.ErrorHandling.TerminalFailure]
public class PaymentAmountMismatchException(decimal expected, decimal received)
    : Exception($"Expected a payment of {expected}, but received {received}");

public class RefundAlreadyIssuedException(Guid orderFulfillmentId)
    : Exception($"Order fulfillment {orderFulfillmentId} has already been refunded");

// Where the messages the saga sends end up. Nothing to do in a specification
public static class Payments
{
    public static void Handle(RefundPayment refund) { }
    public static void Handle(ShipmentForClosedOrder shipment) { }
}
