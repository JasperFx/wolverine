using Wolverine.Marten;

namespace ProcessManagerSample.OrderFulfillment.Handlers;

/// <summary>
/// Reacts to an <see cref="ItemsReserved"/> integration event from the warehouse service.
/// Records the event on the process stream and, if this was the last gate, appends the
/// terminal <see cref="OrderFulfillmentCompleted"/> event.
/// </summary>
[AggregateHandler]
public static class ItemsReservedHandler
{
    public static Events Handle(ItemsReserved @event, OrderFulfillmentState state)
    {
        if (state.IsTerminal) return new Events();
        if (state.ItemsReserved) return new Events();

        var events = new Events();
        events += @event;

        if (state.PaymentConfirmed && state.ShipmentConfirmed)
        {
            events += new OrderFulfillmentCompleted(state.Id);
        }

        return events;
    }
}
