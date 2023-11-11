using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Attributes;
using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace WolverineWebApi.Marten;

#region sample_order_aggregate_for_http

// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);

public record OrderShipped;
public record OrderCreated(Item[] Items);
public record OrderReady;
public interface IShipOrder
{
    Guid OrderId { init; }
}
public record ShipOrder(Guid OrderId) : IShipOrder;
public record ShipOrder2(string Description);
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

    public bool IsShipped() => Shipped.HasValue;
}

#endregion

public record OrderStatus(Guid OrderId, bool IsReady);

public record OrderStarted;

public record StartOrder(string[] Items);

public record StartOrderWithId(Guid Id, string[] Items);

public static class CanShipOrderMiddleWare
{
    #region sample_using_before_on_http_aggregate
    [AggregateHandler]
    public static ProblemDetails Before(IShipOrder command, Order order)
    {
        if (order.IsShipped())
        {
            return new ProblemDetails
            {
                Detail = "Order already shipped",
                Status = 428
            };
        }
        return WolverineContinue.NoProblems;
    }
    #endregion
}

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

    #region sample_using_aggregate_attribute_1

    [WolverinePost("/orders/{orderId}/ship2"), EmptyResponse]
    // The OrderShipped return value is treated as an event being posted
    // to a Marten even stream
    // instead of as the HTTP response body because of the presence of 
    // the [EmptyResponse] attribute
    public static OrderShipped Ship(ShipOrder2 command, [Aggregate] Order order)
    {
        return new OrderShipped();
    }

    #endregion

    #region sample_using_aggregate_attribute_2

    [WolverinePost("/orders/{orderId}/ship3"), EmptyResponse]
    // The OrderShipped return value is treated as an event being posted
    // to a Marten even stream
    // instead of as the HTTP response body because of the presence of 
    // the [EmptyResponse] attribute
    public static OrderShipped Ship3([Aggregate] Order order)
    {
        return new OrderShipped();
    }

    #endregion
    
    [WolverinePost("/orders/{orderId}/ship4"), EmptyResponse]
    // The OrderShipped return value is treated as an event being posted
    // to a Marten even stream
    // instead of as the HTTP response body because of the presence of 
    // the [EmptyResponse] attribute
    public static OrderShipped Ship4([Aggregate] Order order)
    {
        return new OrderShipped();
    }

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
    
    [Transactional]
    [WolverinePost("/orders/create3")]
    public static (CreationResponse, IStartStream) StartOrder3(StartOrder command)
    {
        var items = command.Items.Select(x => new Item { Name = x }).ToArray();
    
        var startStream = MartenOps.StartStream<Order>(new OrderCreated(items));
    
        return (
            new CreationResponse($"/orders/{startStream.StreamId}"),
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
    
    [Transactional] // This can be omitted if you use auto-transactions
    [WolverinePost("/orders/create4")]
    public static (OrderStatus, IStartStream) StartOrder4(StartOrderWithId command)
    {
        var items = command.Items.Select(x => new Item { Name = x }).ToArray();

        // This is unique to Wolverine (we think)
        var startStream = MartenOps
            .StartStream<Order>(command.Id,new OrderCreated(items));

        return (
            new OrderStatus(startStream.StreamId, false),
            startStream
        );
    }

}
