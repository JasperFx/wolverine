namespace ProcessManagerViaHandlers.OrderFulfillment;

/// <summary>
/// Starts the order fulfillment process. The caller assigns the identity, which
/// then becomes both the stream id and the correlation id for the remaining steps.
/// Name matches the OrderFulfillmentState convention so Wolverine resolves the stream id
/// without needing a [WriteAggregate("...")] override.
/// The optional <see cref="PaymentTimeoutWindow"/> overrides the default 15-minute window;
/// tests set it to a small value so the scheduler fires within the test window.
/// </summary>
public record StartOrderFulfillment(
    Guid OrderFulfillmentStateId,
    Guid CustomerId,
    decimal TotalAmount,
    TimeSpan? PaymentTimeoutWindow = null);

/// <summary>
/// Compensating command. Cancels an in-flight process and marks the stream terminated
/// so any subsequent integration events are ignored idempotently.
/// </summary>
public record CancelOrderFulfillment(
    Guid OrderFulfillmentStateId,
    string Reason);

/// <summary>
/// Scheduled self-message fired by the start handler via <c>OutgoingMessages.Delay</c>.
/// Handled by <see cref="Handlers.PaymentTimeoutHandler"/>, which cancels the process
/// if payment has not arrived by the time the scheduler dispatches this message.
/// </summary>
public record PaymentTimeout(Guid OrderFulfillmentStateId);
