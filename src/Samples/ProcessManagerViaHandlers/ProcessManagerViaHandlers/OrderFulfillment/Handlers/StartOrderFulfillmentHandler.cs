using Wolverine;
using Wolverine.Marten;

namespace ProcessManagerViaHandlers.OrderFulfillment.Handlers;

/// <summary>
/// Bootstraps a new order fulfillment stream via <see cref="MartenOps.StartStream{T}(System.Guid, object[])"/>
/// and schedules a <see cref="PaymentTimeout"/> via <see cref="OutgoingMessages.Delay{T}"/>.
///
/// The "start" case is intentionally a plain handler rather than an <c>[AggregateHandler]</c>: the default
/// OnMissing behavior on <c>[AggregateHandler]</c> short-circuits when the aggregate does not yet exist, so it
/// cannot be used to create a new stream without overriding that behavior. Continue handlers, which operate on
/// an already-existing stream, use <c>[AggregateHandler]</c> freely.
/// </summary>
public static class StartOrderFulfillmentHandler
{
    // Production default. Tests override via StartOrderFulfillment.PaymentTimeoutWindow.
    public static readonly TimeSpan DefaultPaymentTimeoutWindow = TimeSpan.FromMinutes(15);

    public static (IStartStream, OutgoingMessages) Handle(StartOrderFulfillment command)
    {
        var started = new OrderFulfillmentStarted(
            command.OrderFulfillmentStateId,
            command.CustomerId,
            command.TotalAmount);

        var outgoing = new OutgoingMessages();
        outgoing.Delay(
            new PaymentTimeout(command.OrderFulfillmentStateId),
            command.PaymentTimeoutWindow ?? DefaultPaymentTimeoutWindow);

        return (
            MartenOps.StartStream<OrderFulfillmentState>(command.OrderFulfillmentStateId, started),
            outgoing);
    }
}
