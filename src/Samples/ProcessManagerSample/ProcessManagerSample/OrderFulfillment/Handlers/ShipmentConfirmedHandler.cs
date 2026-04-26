using Wolverine.Marten;

namespace ProcessManagerSample.OrderFulfillment.Handlers;

/// <summary>
/// Reacts to a <see cref="ShipmentConfirmed"/> integration event from the shipping service.
/// Records the event on the process stream and, if this was the last gate, appends the
/// terminal <see cref="OrderFulfillmentCompleted"/> event.
/// </summary>
[AggregateHandler]
public static class ShipmentConfirmedHandler
{
    public static Events Handle(ShipmentConfirmed @event, OrderFulfillmentState state)
    {
        if (state.IsTerminal) return new Events();
        if (state.ShipmentConfirmed) return new Events();

        var events = new Events();
        events += @event;

        if (state.PaymentConfirmed && state.ItemsReserved)
        {
            events += new OrderFulfillmentCompleted(state.Id);
        }

        return events;
    }
}
