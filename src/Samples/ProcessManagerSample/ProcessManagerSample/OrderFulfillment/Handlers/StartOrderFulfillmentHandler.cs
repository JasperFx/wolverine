using Wolverine.Marten;

namespace ProcessManagerSample.OrderFulfillment.Handlers;

/// <summary>
/// Bootstraps a new order fulfillment stream via <see cref="MartenOps.StartStream{T}(System.Guid, object[])"/>.
///
/// The "start" case is intentionally a plain handler rather than an <c>[AggregateHandler]</c>: the default
/// OnMissing behavior on <c>[AggregateHandler]</c> short-circuits when the aggregate does not yet exist, so it
/// cannot be used to create a new stream without overriding that behavior. Continue handlers, which operate on
/// an already-existing stream, use <c>[AggregateHandler]</c> freely.
/// </summary>
public static class StartOrderFulfillmentHandler
{
    public static IStartStream Handle(StartOrderFulfillment command)
    {
        var started = new OrderFulfillmentStarted(
            command.OrderFulfillmentStateId,
            command.CustomerId,
            command.TotalAmount);

        return MartenOps.StartStream<OrderFulfillmentState>(command.OrderFulfillmentStateId, started);
    }
}
