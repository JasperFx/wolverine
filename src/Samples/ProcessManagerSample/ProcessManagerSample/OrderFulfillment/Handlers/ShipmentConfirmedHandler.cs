using Wolverine.Marten;

namespace ProcessManagerSample.OrderFulfillment.Handlers;

[AggregateHandler]
public static class ShipmentConfirmedHandler
{
    public static IEnumerable<object> Handle(ShipmentConfirmed @event, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        if (state.ShipmentConfirmed) yield break;

        yield return @event;

        if (state.PaymentConfirmed && state.ItemsReserved)
        {
            yield return new OrderFulfillmentCompleted(state.Id);
        }
    }
}
