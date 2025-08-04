using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Mvc;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence;

namespace OrderEventSourcingSample;

public record OrderShipped;

public record OrderCreated(Item[] Items);

public record OrderReady;

public record ShipOrder(Guid OrderId);

public record ItemReady(string Name);

#region sample_Order_event_sourced_aggregate

public class Item
{
    public string Name { get; set; }
    public bool Ready { get; set; }
}

public class Order
{
    public Order(OrderCreated created)
    {
        foreach (var item in created.Items) Items[item.Name] = item;
    }

    // This would be the stream id
    public Guid Id { get; set; }

    // This is important, by Marten convention this would
    // be the
    public int Version { get; set; }

    public DateTimeOffset? Shipped { get; private set; }

    public Dictionary<string, Item> Items { get; set; } = new();

    // These methods are used by Marten to update the aggregate
    // from the raw events
    public void Apply(IEvent<OrderShipped> shipped)
    {
        Shipped = shipped.Timestamp;
    }

    public void Apply(ItemReady ready)
    {
        Items[ready.Name].Ready = true;
    }

    public bool IsReadyToShip()
    {
        return Shipped == null && Items.Values.All(x => x.Ready);
    }
}

#endregion

#region sample_MarkItemReady

// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);

#endregion

public class MarkItemController : ControllerBase
{
    #region sample_MarkItemController

    [HttpPost("/orders/itemready")]
    public async Task Post(
        [FromBody] MarkItemReady command,
        [FromServices] IDocumentSession session,
        [FromServices] IMartenOutbox outbox
    )
    {
        // This is important!
        outbox.Enroll(session);

        // Fetch the current value of the Order aggregate
        var stream = await session
            .Events

            // We're also opting into Marten optimistic concurrency checks here
            .FetchForWriting<Order>(command.OrderId, command.Version);

        var order = stream.Aggregate;

        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
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
            // Publish a cascading command to do whatever it takes
            // to actually ship the order
            // Note that because the context here is enrolled in a Wolverine
            // outbox, the message is registered, but not "released" to
            // be sent out until SaveChangesAsync() is called down below
            await outbox.PublishAsync(new ShipOrder(command.OrderId));
            stream.AppendOne(new OrderReady());
        }

        // This will also persist and flush out any outgoing messages
        // registered into the context outbox
        await session.SaveChangesAsync();
    }

    #endregion
}

public class ShipOrderHandler
{
    public async Task Handle1(MarkItemReady command, IDocumentSession session)
    {
        // Fetch the current value of the Order aggregate
        var stream = await session
            .Events
            .FetchForWriting<Order>(command.OrderId);

        var order = stream.Aggregate;

        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
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

        await session.SaveChangesAsync();
    }

    public async Task Handle2(MarkItemReady command, IDocumentSession session)
    {
        // Fetch the current value of the Order aggregate
        var stream = await session
            .Events

            // Explicitly tell Marten the expected, starting version of the
            // event stream
            .FetchForWriting<Order>(command.OrderId, command.Version);

        var order = stream.Aggregate;

        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
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

        await session.SaveChangesAsync();
    }

    public async Task Handle3(MarkItemReady command, IDocumentSession session)
    {
        // Fetch the current value of the Order aggregate
        var stream = await session
            .Events

            // Explicitly tell Marten the expected, starting version of the
            // event stream
            .FetchForExclusiveWriting<Order>(command.OrderId);

        var order = stream.Aggregate;

        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
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

        await session.SaveChangesAsync();
    }

    public Task Handle4(MarkItemReady command, IDocumentSession session)
    {
        return session.Events.WriteToAggregate<Order>(command.OrderId, command.Version, stream =>
        {
            var order = stream.Aggregate;

            if (order.Items.TryGetValue(command.ItemName, out var item))
            {
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
        });
    }
}

public static class MarkItemReadyHandler
{
    #region sample_MarkItemReadyHandler

    [AggregateHandler]
    public static IEnumerable<object> Handle(MarkItemReady command, Order order)
    {
        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
            // Not doing this in a purist way here, but just
            // trying to illustrate the Wolverine mechanics
            item.Ready = true;

            // Mark that the this item is ready
            yield return new ItemReady(command.ItemName);
        }
        else
        {
            // Some crude validation
            throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
        }

        // If the order is ready to ship, also emit an OrderReady event
        if (order.IsReadyToShip())
        {
            yield return new OrderReady();
        }
    }

    #endregion
}

public static class MarkItemReady2Handler
{
    #region sample_MarkItemReadyHandler_with_WriteAggregate
    
    public static IEnumerable<object> Handle(
        // The command
        MarkItemReady command, 
        
        // This time we'll mark the parameter as the "aggregate"
        [WriteAggregate] Order order)
    {
        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
            // Not doing this in a purist way here, but just
            // trying to illustrate the Wolverine mechanics
            item.Ready = true;

            // Mark that the this item is ready
            yield return new ItemReady(command.ItemName);
        }
        else
        {
            // Some crude validation
            throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
        }

        // If the order is ready to ship, also emit an OrderReady event
        if (order.IsReadyToShip())
        {
            yield return new OrderReady();
        }
    }

    #endregion
}

public record Data;

public interface ISomeService
{
    Task<Data> FindDataAsync();
}

public static class MarkItemReadyHandler2
{
    #region sample_using_events_and_messages_from_AggregateHandler

    [AggregateHandler]
    public static async Task<(Events, OutgoingMessages)> HandleAsync(MarkItemReady command, Order order, ISomeService service)
    {
        // All contrived, let's say we need to call some
        // kind of service to get data so this handler has to be
        // async
        var data = await service.FindDataAsync();

        var messages = new OutgoingMessages();
        var events = new Events();

        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
            // Not doing this in a purist way here, but just
            // trying to illustrate the Wolverine mechanics
            item.Ready = true;

            // Mark that the this item is ready
            events += new ItemReady(command.ItemName);
        }
        else
        {
            // Some crude validation
            throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
        }

        // If the order is ready to ship, also emit an OrderReady event
        if (order.IsReadyToShip())
        {
            events += new OrderReady();
            messages.Add(new ShipOrder(order.Id));
        }

        // This results in both new events being captured
        // and potentially the ShipOrder message going out
        return (events, messages);
    }

    #endregion
}

#region sample_validation_on_aggregate_being_missing_in_aggregate_handler_workflow

public static class ValidatedMarkItemReadyHandler
{
    public static IEnumerable<object> Handle(
        // The command
        MarkItemReady command,

        // In HTTP this will return a 404 status code and stop
        // the request if the Order is not found
        
        // In message handlers, this will log that the Order was not found,
        // then stop processing. The message would be effectively
        // discarded
        [WriteAggregate(Required = true)] Order order) => [];

    [WolverineHandler]
    public static IEnumerable<object> Handle2(
        // The command
        MarkItemReady command,

        // In HTTP this will return a 400 status code and 
        // write out a ProblemDetails response with a default message explaining
        // the data that could not be found
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith400)] Order order) => [];
    
    [WolverineHandler]
    public static IEnumerable<object> Handle3(
        // The command
        MarkItemReady command,

        // In HTTP this will return a 404 status code and 
        // write out a ProblemDetails response with a default message explaining
        // the data that could not be found
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith404)] Order order) => [];

    
    [WolverineHandler]
    public static IEnumerable<object> Handle4(
        // The command
        MarkItemReady command,

        // In HTTP this will return a 400 status code and 
        // write out a ProblemDetails response with a custom message.
        // Wolverine will substitute in the order identity into the message for "{0}"
        // In message handlers, Wolverine will log using your custom message then discard the message
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith404, MissingMessage = "Cannot find Order {0}")] Order order) => [];

}

#endregion