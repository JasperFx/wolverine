using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq;
using Marten.Pagination;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;

namespace WolverineWebApi.Marten;

#region sample_order_aggregate_for_http

// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);

public record OrderShipped;
public record OrderCreated(Item[] Items);
public record OrderReady;
public record OrderConfirmed;
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
    // For JSON serialization
    public Order(){}
    
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
    public bool HasShipped { get; set; }

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

    public void Apply(OrderConfirmed confirmed)
    {
        IsConfirmed = true;
    }

    public bool IsConfirmed { get; set; }

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

public record ConfirmOrder(Guid OrderId);

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
        if (order.HasShipped)
            throw new InvalidOperationException("This has already shipped!");

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

    #region sample_using_aggregate_attribute_query_parameter
    
    [WolverinePost("/orders/ship/from-query"), EmptyResponse]
    // The OrderShipped return value is treated as an event being posted
    // to a Marten even stream
    // instead of as the HTTP response body because of the presence of
    // the [EmptyResponse] attribute
    public static OrderShipped ShipFromQuery([FromQuery] Guid id, [Aggregate] Order order)
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

    [AggregateHandler]
    [WolverinePost("/orders/{id}/confirm")]
    public static (AcceptResponse, Events) Confirm(ConfirmOrder command, Order order)
    {
        return (
            new AcceptResponse($"/orders/{order.Id}"),
            [new OrderConfirmed()]
        );
    }

    #region sample_returning_updated_aggregate_as_response_from_http_endpoint

    [AggregateHandler]
    [WolverinePost("/orders/{id}/confirm2")]
    // The updated version of the Order aggregate will be returned as the response body
    // from requesting this endpoint at runtime
    public static (UpdatedAggregate, Events) ConfirmDifferent(ConfirmOrder command, Order order)
    {
        return (
            new UpdatedAggregate(),
            [new OrderConfirmed()]
        );
    }

    #endregion
    
    [AggregateHandler]
    [WolverinePost("/orders/{id}/confirm3")]
    // The updated version of the Order aggregate will be returned as the response body
    // from requesting this endpoint at runtime
    public static (UpdatedAggregate, OrderConfirmed) ConfirmDifferent2(ConfirmOrder command, Order order)
    {
        return (
            new UpdatedAggregate(),
            new OrderConfirmed()
        );
    }

    #region sample_using_ReadAggregate_in_HTTP

    [WolverineGet("/orders/latest/{id}")]
    public static Order GetLatest(Guid id, [ReadAggregate] Order order) => order;

    #endregion
    
    [WolverineGet("/orders/V1.0/latest/{id}")]
    public static Order GetLatestV1(Guid id, [ReadAggregate] Order order) => order;
    
    [WolverineGet("/orders/latest/from-query")]
    public static Order GetLatestFromQuery([FromQuery] Guid id, [ReadAggregate] Order order) => order;
}

#region sample_using_[FromQuery]_binding

// If you want every value to be optional, use public, settable
// properties and a no-arg public constructor
public class OrderQuery
{
    public int PageSize { get; set; } = 10;
    public int PageNumber { get; set; } = 1;
    public bool? HasShipped { get; set; }
}

// Or -- and I'm not sure how useful this really is, use a record:
public record OrderQueryAlternative(int PageSize, int PageNumber, bool HasShipped);

public static class QueryOrdersEndpoint
{
    [WolverineGet("/api/orders/query")]
    public static Task<IPagedList<Order>> Query(
        // This will be bound from query string values in the HTTP request
        [FromQuery] OrderQuery query, 
        IQuerySession session,
        CancellationToken token)
    {
        IQueryable<Order> queryable = session.Query<Order>()
            // Just to make the paging deterministic
            .OrderBy(x => x.Id);

        if (query.HasShipped.HasValue)
        {
            queryable = query.HasShipped.Value 
                ? queryable.Where(x => x.Shipped.HasValue) 
                : queryable.Where(x => !x.Shipped.HasValue);
        }

        // Marten specific Linq helper
        return queryable.ToPagedListAsync(query.PageNumber, query.PageSize, token);
    }
}


#endregion

#region sample_showing_concurrency_exception_moving_directly_to_DLQ

public static class MarkItemReadyHandler
{
    // This will let us specify error handling policies specific
    // to only this message handler
    public static void Configure(HandlerChain chain)
    {
        // Can't ever process this message, so send it directly 
        // to the DLQ
        // Do not pass Go, do not collect $200...
        chain.OnException<ConcurrencyException>()
            .MoveToErrorQueue();
        
        // Or instead...
        // Can't ever process this message, so just throw it away
        // Do not pass Go, do not collect $200...
        chain.OnException<ConcurrencyException>()
            .Discard();
    }
    
    public static IEnumerable<object> Post(
        MarkItemReady command, 
        
        // Wolverine + Marten will assert that the Order stream
        // in question has not advanced from command.Version
        [WriteAggregate] Order order)
    {
        // process the message and emit events
        yield break;
    }
}

#endregion