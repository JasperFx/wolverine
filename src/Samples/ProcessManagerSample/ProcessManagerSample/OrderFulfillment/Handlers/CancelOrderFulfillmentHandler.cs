using Wolverine.Marten;

namespace ProcessManagerSample.OrderFulfillment.Handlers;

/// <summary>
/// Compensating path. Marks the process cancelled with a reason; subsequent integration events
/// are ignored by the completion guard on each continue handler.
/// </summary>
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
