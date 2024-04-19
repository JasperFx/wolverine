# Integration with Marten

New in Wolverine 1.10.0 is the `Wolverine.Http.Marten` library that adds the ability to more deeply integrate Marten
into Wolverine.HTTP by utilizing information from route arguments.

To install that library, use:

```bash
dotnet add package WolverineFx.Http.Marten
```

## Passing Marten Documents to Endpoint Parameters

Consider this very common use case, you have an HTTP endpoint that needs to work on a Marten document that will
be loaded using the value of one of the route arguments as that document's identity. In a long hand way, that could
look like this:

<!-- snippet: sample_get_invoice_longhand -->
<a id='snippet-sample_get_invoice_longhand'></a>
```cs
{
    [WolverineGet("/invoices/longhand/id")]
    [ProducesResponseType(404)] 
    [ProducesResponseType(200, Type = typeof(Invoice))]
    public static async Task<IResult> GetInvoice(
        Guid id, 
        IQuerySession session, 
        CancellationToken cancellationToken)
    {
        var invoice = await session.LoadAsync<Invoice>(id, cancellationToken);
        if (invoice == null) return Results.NotFound();

        return Results.Ok(invoice);
    }
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L13-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_get_invoice_longhand' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Pretty straightforward, but it's a little annoying to have to scatter in all the attributes for OpenAPI and there's definitely
some repetitive code. So let's introduce the new `[Document]` parameter and look at an exact equivalent for both the
actual functionality and for the OpenAPI metadata:

<!-- snippet: sample_using_document_attribute -->
<a id='snippet-sample_using_document_attribute'></a>
```cs
[WolverineGet("/invoices/{id}")]
public static Invoice Get([Document] Invoice invoice)
{
    return invoice;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L32-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_document_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Notice that the `[Document]` attribute was able to use the "id" route parameter. By default, Wolverine is looking first
for a route variable named "invoiceId" (the document type name + "Id"), then falling back to looking for "id". You can
of course explicitly override the matching of route argument like so:

<!-- snippet: sample_overriding_route_argument_with_document_attribute -->
<a id='snippet-sample_overriding_route_argument_with_document_attribute'></a>
```cs
[WolverinePost("/invoices/{number}/approve")]
public static IMartenOp Approve([Document("number")] Invoice invoice)
{
    invoice.Approved = true;
    return MartenOps.Store(invoice);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L53-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_overriding_route_argument_with_document_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Marten Aggregate Workflow 

The http endpoints can play inside the full "critter stack" combination with [Marten](https://martendb.io) with Wolverine's [specific
support for Event Sourcing and CQRS](/guide/durability/marten/event-sourcing). Originally this has been done
by just mimicking the command handler mechanism and having all the inputs come in through the request body (aggregate id, version).
Wolverine 1.10 added a more HTTP-centric approach using route arguments. 

### Using Route Arguments

To opt into the Wolverine + Marten "aggregate workflow", but use data from route arguments for the aggregate id,
use the new `[Aggregate]` attribute from Wolverine.Http.Marten on endpoint method parameters like shown below:

<!-- snippet: sample_using_aggregate_attribute_1 -->
<a id='snippet-sample_using_aggregate_attribute_1'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L121-L136' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_aggregate_attribute_1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using this version of the "aggregate workflow", you no longer have to supply a command in the request body, so you could
have an endpoint signature like this:

<!-- snippet: sample_using_aggregate_attribute_2 -->
<a id='snippet-sample_using_aggregate_attribute_2'></a>
```cs
[WolverinePost("/orders/{orderId}/ship3"), EmptyResponse]
// The OrderShipped return value is treated as an event being posted
// to a Marten even stream
// instead of as the HTTP response body because of the presence of 
// the [EmptyResponse] attribute
public static OrderShipped Ship3([Aggregate] Order order)
{
    return new OrderShipped();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L138-L150' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_aggregate_attribute_2' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A couple other notes: 

* The return value handling for events follows the same rules as shown in the next section
* The endpoints will return a 404 response code if the aggregate in question does not exist
* The aggregate id can be set explicitly like `[Aggregate("number")]` to match against a route argument named "number", or by default
  the behavior will try to match first on "{camel case name of aggregate type}Id", then a route argument named "id"
* This usage will automatically apply the transactional middleware for Marten

### Using Request Body

::: tip
This usage only requires Wolverine.Marten and does not require the Wolverine.Http.Marten library because
there's nothing happening here in regards to Marten that is using AspNetCore 
:::

For some context, let's say that we have the following events and [Marten aggregate](https://martendb.io/events/projections/aggregate-projections.html#aggregate-by-stream) to model the workflow of an `Order`:

<!-- snippet: sample_order_aggregate_for_http -->
<a id='snippet-sample_order_aggregate_for_http'></a>
```cs
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

    public bool IsReadyToShip()
    {
        return Shipped == null && Items.Values.All(x => x.Ready);
    }

    public bool IsShipped() => Shipped.HasValue;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L11-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_aggregate_for_http' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L106-L119' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_emptyresponse' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L200-L230' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_returning_multiple_events_from_http_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Compiled Query Resource Writer Policy

Marten integration comes with an `IResourceWriterPolicy` policy that handles compiled queries as return types. 
Register it in `WolverineHttpOptions` like this:

<!-- snippet: sample_user_marten_compiled_query_policy -->
<a id='snippet-sample_user_marten_compiled_query_policy'></a>
```cs
opts.UseMartenCompiledQueryResultPolicy();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L166-L168' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_user_marten_compiled_query_policy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you now return a compiled query from an Endpoint the result will get directly streamed to the client as JSON. Short circuiting JSON deserialization.
<!-- snippet: sample_compiled_query_return_endpoint -->
<a id='snippet-sample_compiled_query_return_endpoint'></a>
```cs
[WolverineGet("/invoices/approved")]
public static ApprovedInvoicedCompiledQuery GetApproved()
{
    return new ApprovedInvoicedCompiledQuery();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L64-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_compiled_query_return_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_compiled_query_return_query -->
<a id='snippet-sample_compiled_query_return_query'></a>
```cs
public class ApprovedInvoicedCompiledQuery : ICompiledListQuery<Invoice>
{
    public Expression<Func<IMartenQueryable<Invoice>, IEnumerable<Invoice>>> QueryIs()
    {
        return q => q.Where(x => x.Approved);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L98-L108' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_compiled_query_return_query' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
