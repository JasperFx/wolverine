using Wolverine.Marten;

namespace ProcessManagerViaHandlers.OrderFulfillment.Handlers;

/// <summary>
/// Fires when the scheduled <see cref="PaymentTimeout"/> message is dispatched by Wolverine's scheduler.
/// Cancels the process if payment never confirmed. Idempotent by construction: both guards below make
/// this a silent no-op if payment arrived before the timer fired, or if the process is already terminal.
/// No explicit cancellation of the scheduled message is needed; state decides whether the timeout acts.
/// </summary>
[AggregateHandler]
public static class PaymentTimeoutHandler
{
    public static IEnumerable<object> Handle(PaymentTimeout _, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        if (state.PaymentConfirmed) yield break;
        yield return new OrderFulfillmentCancelled(state.Id, "Payment timed out");
    }
}
