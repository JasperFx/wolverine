using Wolverine.Marten;

namespace ProcessManagerSample.OrderFulfillment.Handlers;

[AggregateHandler]
public static class CancelOrderFulfillmentHandler
{
    public static Events Handle(CancelOrderFulfillment command, OrderFulfillmentState state)
    {
        if (state.IsTerminal) return new Events();

        var events = new Events();
        events += new OrderFulfillmentCancelled(state.Id, command.Reason);
        return events;
    }
}
