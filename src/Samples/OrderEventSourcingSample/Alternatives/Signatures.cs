using Wolverine.Marten;
using Marten.Events;
using Marten.Schema;
using Wolverine.Attributes;

namespace OrderEventSourcingSample.Alternatives;


#region sample_MarkItemReady_with_explicit_identity

public class MarkItemReady
{
    // This attribute tells Wolverine that this property will refer to the
    // Order aggregate
    [Identity]
    public Guid Id { get; init; }

    public string ItemName { get; init; }
}

#endregion

public static class MarkItemReadyHandler
{
    [WolverineIgnore] // just keeping this out of codegen and discovery
    #region sample_MarkItemReadyHandler_with_explicit_stream

    [MartenCommandWorkflow]
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

