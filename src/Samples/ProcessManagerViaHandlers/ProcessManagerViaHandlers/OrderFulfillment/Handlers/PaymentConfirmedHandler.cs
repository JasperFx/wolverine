using Wolverine.Marten;

namespace ProcessManagerViaHandlers.OrderFulfillment.Handlers;

[AggregateHandler]
public static class PaymentConfirmedHandler
{
    public static IEnumerable<object> Handle(PaymentConfirmed @event, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        if (state.PaymentConfirmed) yield break;

        yield return @event;

        if (state.ItemsReserved && state.ShipmentConfirmed)
        {
            yield return new OrderFulfillmentCompleted(state.Id);
        }
    }
}
