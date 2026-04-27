using Wolverine.Marten;

namespace ProcessManagerViaHandlers.OrderFulfillment.Handlers;

[AggregateHandler]
public static class CancelOrderFulfillmentHandler
{
    public static IEnumerable<object> Handle(CancelOrderFulfillment command, OrderFulfillmentState state)
    {
        if (state.IsTerminal) yield break;
        yield return new OrderFulfillmentCancelled(state.Id, command.Reason);
    }
}
