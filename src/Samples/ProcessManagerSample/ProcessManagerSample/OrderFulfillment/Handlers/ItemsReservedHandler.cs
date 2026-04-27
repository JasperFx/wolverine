using Wolverine.Marten;

namespace ProcessManagerSample.OrderFulfillment.Handlers;

[AggregateHandler]
public static class ItemsReservedHandler
{
    public static IEnumerable<object> Handle(ItemsReserved @event, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        if (state.ItemsReserved) yield break;

        yield return @event;

        if (state.PaymentConfirmed && state.ShipmentConfirmed)
        {
            yield return new OrderFulfillmentCompleted(state.Id);
        }
    }
}
