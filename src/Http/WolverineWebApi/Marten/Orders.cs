using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Attributes;
using Wolverine.Http;
using Wolverine.Marten;

namespace WolverineWebApi.Marten;

#region sample_order_aggregate_for_http

// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);

public record OrderShipped;
public record OrderCreated(Item[] Items);
public record OrderReady;
public record ShipOrder(Guid OrderId);
public record ItemReady(string Name);

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

public record OrderStatus(Guid OrderId, bool IsReady);

public record OrderStarted;

public record StartOrder(string[] Items);


public static class MarkItemEndpoint
{
    #region sample_using_EmptyResponse

    [AggregateHandler]
    [WolverinePost("/orders/ship"), EmptyResponse]
    // The OrderShipped return value is treated as an event being posted
    // to a Marten even stream
    // instead of as the HTTP response body because of the presence of 
    // the [EmptyResponse] attribute
    public static OrderShipped Ship(ShipOrder command, Order order)
    {
        return new OrderShipped();
    }

    #endregion

    [Transactional]
    [WolverinePost("/orders/create")]
    public static OrderStatus StartOrder(StartOrder command, IDocumentSession session)
    {
        var items = command.Items.Select(x => new Item { Name = x }).ToArray();
        var orderId = session.Events.StartStream<Order>(new OrderCreated(items)).Id;

        return new OrderStatus(orderId, false);
    }

    [Transactional]
    [WolverinePost("/orders/create2")]
    public static (OrderStatus, IStartStream) StartOrder2(StartOrder command, IDocumentSession _)
    {
        var items = command.Items.Select(x => new Item { Name = x }).ToArray();

        var startStream = MartenOps.StartStream<Order>(new OrderCreated(items));

        return (
            new OrderStatus(startStream.StreamId, false),
            startStream
        );
    }

    #region sample_returning_multiple_events_from_http_endpoint

    [AggregateHandler]
    [WolverinePost("/orders/itemready")]
    public static (OrderStatus, Events) Post(MarkItemReady command, Order order)
    {
        var events = new Events();
        
        if (order.Items.TryGetValue(command.ItemName, out var item))
        {
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
        }

        return (new OrderStatus(order.Id, order.IsReadyToShip()), events);
    }

    #endregion

}
