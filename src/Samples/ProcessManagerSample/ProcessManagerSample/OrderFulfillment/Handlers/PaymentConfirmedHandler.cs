using Wolverine.Marten;

namespace ProcessManagerSample.OrderFulfillment.Handlers;

/// <summary>
/// Reacts to a <see cref="PaymentConfirmed"/> integration event from the payment service.
/// Records the event on the process stream and, if this was the last gate, appends the
/// terminal <see cref="OrderFulfillmentCompleted"/> event.
/// </summary>
[AggregateHandler]
public static class PaymentConfirmedHandler
{
    public static Events Handle(PaymentConfirmed @event, OrderFulfillmentState state)
    {
        // Completion guard: every continue handler must check this first.
        if (state.IsTerminal) return new Events();

        // Idempotency guard against a duplicate delivery of the same integration event.
        if (state.PaymentConfirmed) return new Events();

        var events = new Events();
        events += @event;

        if (state.ItemsReserved && state.ShipmentConfirmed)
        {
            events += new OrderFulfillmentCompleted(state.Id);
        }

        return events;
    }
}
