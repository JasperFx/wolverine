namespace ProcessManagerSample.OrderFulfillment;

/// <summary>
/// Emitted when the fulfillment process kicks off for a newly placed order.
/// Creates the stream and sets the correlation identity for all downstream handlers.
/// </summary>
public record OrderFulfillmentStarted(
    Guid OrderFulfillmentStateId,
    Guid CustomerId,
    decimal TotalAmount);

/// <summary>
/// Integration event from the payment service. The process manager treats this as both the
/// trigger message and the fact it records on its own stream.
/// </summary>
public record PaymentConfirmed(
    Guid OrderFulfillmentStateId,
    decimal Amount);

public record ItemsReserved(
    Guid OrderFulfillmentStateId,
    Guid ReservationId);

public record ShipmentConfirmed(
    Guid OrderFulfillmentStateId,
    string TrackingNumber);

/// <summary>
/// Terminal event for the happy path. Appended by whichever continue handler observes
/// that all three prerequisite steps are now complete.
/// </summary>
public record OrderFulfillmentCompleted(Guid OrderFulfillmentStateId);

/// <summary>
/// Terminal event for the compensating path. Appended by the cancel handler or a timeout.
/// </summary>
public record OrderFulfillmentCancelled(
    Guid OrderFulfillmentStateId,
    string Reason);
