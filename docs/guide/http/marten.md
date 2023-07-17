# Integration with Marten

The http endpoints can play inside the full "critter stack" combination with [Marten](https://martendb.io) with Wolverine's [specific
support for Event Sourcing and CQRS](/guide/durability/marten/event-sourcing).

For some context, let's say that we have the following events and [Marten aggregate](https://martendb.io/events/projections/aggregate-projections.html#aggregate-by-stream) to model the workflow of an `Order`:

<!-- snippet: sample_order_aggregate_for_http -->
<a id='snippet-sample_order_aggregate_for_http'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L10-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_aggregate_for_http' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To append a single event to an event stream from an HTTP endpoint, you can use a return value like so:

<!-- snippet: sample_using_EmptyResponse -->
<a id='snippet-sample_using_emptyresponse'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L77-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_emptyresponse' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or potentially append multiple events using the `Events` type as a return value like this sample:

<!-- snippet: sample_returning_multiple_events_from_http_endpoint -->
<a id='snippet-sample_returning_multiple_events_from_http_endpoint'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L130-L160' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_returning_multiple_events_from_http_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
