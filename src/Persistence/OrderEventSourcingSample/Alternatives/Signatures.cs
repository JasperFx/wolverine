using Marten.Events;
using Marten.Schema;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;

namespace OrderEventSourcingSample.Alternatives;

#region sample_MarkItemReady_with_explicit_identity

public class MarkItemReady
{
    // This attribute tells Wolverine that this property will refer to the
    // Order aggregate
    [Identity] public Guid Id { get; init; }

    public string ItemName { get; init; }
}

#endregion

public static class MarkItemReadyHandler
{
    [WolverineIgnore] // just keeping this out of codegen and discovery

    #region sample_MarkItemReadyHandler_with_explicit_stream

    [AggregateHandler]
    public static void Handle(OrderEventSourcingSample.MarkItemReady command, IEventStream<Order> stream)
    {
        var order = stream.Aggregate;

        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
            // Not doing this in a purist way here, but just
            // trying to illustrate the Wolverine mechanics
            item.Ready = true;

            // Mark that the this item is ready
            stream.AppendOne(new ItemReady(command.ItemName));
        }
        else
        {
            // Some crude validation
            throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
        }

        // If the order is ready to ship, also emit an OrderReady event
        if (order.IsReadyToShip())
        {
            stream.AppendOne(new OrderReady());
        }
    }

    #endregion
}


public static class MarkItemReadyHandler3
{
    [WolverineIgnore] // just keeping this out of codegen and discovery

    #region sample_MarkItemReadyHandler_with_response_for_updated_aggregate

    [AggregateHandler]
    public static (
        // Just tells Wolverine to use Marten's FetchLatest API to respond with
        // the updated version of Order that reflects whatever events were appended
        // in this command
        UpdatedAggregate, 
        
        // The events that should be appended to the event stream for this order
        Events) Handle(OrderEventSourcingSample.MarkItemReady command, Order order)
    {
        var events = new Events();
        
        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
            // Not doing this in a purist way here, but just
            // trying to illustrate the Wolverine mechanics
            item.Ready = true;

            // Mark that the this item is ready
            events.Add(new ItemReady(command.ItemName));
        }
        else
        {
            // Some crude validation
            throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
        }

        // If the order is ready to ship, also emit an OrderReady event
        if (order.IsReadyToShip())
        {
            events.Add(new OrderReady());
        }

        return (new UpdatedAggregate(), events);
    }

    #endregion

    #region sample_using_UpdatedAggregate_with_invoke_async

    public static Task<Order> update_and_get_latest(IMessageBus bus, MarkItemReady command)
    {
        // This will return the updated version of the Order
        // aggregate that incorporates whatever events were appended
        // in the course of processing the command
        return bus.InvokeAsync<Order>(command);
    }

    #endregion
}

