namespace ProcessManagerSample.OrderFulfillment;

/// <summary>
/// Starts the order fulfillment process. The caller assigns the identity, which
/// then becomes both the stream id and the correlation id for the remaining steps.
/// Name matches the OrderFulfillmentState convention so Wolverine resolves the stream id
/// without needing a [WriteAggregate("...")] override.
/// </summary>
public record StartOrderFulfillment(
    Guid OrderFulfillmentStateId,
    Guid CustomerId,
    decimal TotalAmount);
