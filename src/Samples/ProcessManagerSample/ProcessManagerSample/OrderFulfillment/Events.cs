namespace ProcessManagerSample.OrderFulfillment;

/// <summary>
/// Emitted when the fulfillment process kicks off for a newly placed order.
/// Creates the stream and sets the correlation identity for all downstream handlers.
/// </summary>
public record OrderFulfillmentStarted(
    Guid OrderFulfillmentStateId,
    Guid CustomerId,
    decimal TotalAmount);
