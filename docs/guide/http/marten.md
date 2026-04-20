# Integration with Marten

New in Wolverine 1.10.0 is the `Wolverine.Http.Marten` library that adds the ability to more deeply integrate Marten
into Wolverine.HTTP by utilizing information from route arguments.

To install that library, use:

```bash
dotnet add package WolverineFx.Http.Marten
```

## Passing Marten Documents to Endpoint Parameters

::: tip
The `[Document]` attribute is still valid, but it's the exact same behavior as the generalized
`[Entity]` attribute that is supported by message handlers as well.
:::

::: info
Strong typed identifiers are supported for this usage as of Wolverine 5.0
:::

Consider this very common use case, you have an HTTP endpoint that needs to work on a Marten document that will
be loaded using the value of one of the route arguments as that document's identity. In a long hand way, that could
look like this:

<!-- snippet: sample_get_invoice_longhand -->
<a id='snippet-sample_get_invoice_longhand'></a>
```cs
{
    [WolverineGet("/invoices/longhand/{id}")]
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L14-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_get_invoice_longhand' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L32-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_document_attribute' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L51-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_overriding_route_argument_with_document_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the code above, if the `Invoice` document does not exist, the route will stop and return a status code 404 for Not Found.  

If you, for whatever reason, want your handler executed even if the document does not exist, then you can set the `DocumentAttribute.Required` property to `false`.

:::info
Starting with Wolverine 3 `DocumentAttribute.Required = true` is the default behavior.
In previous versions the default value was `false`.
:::

However, if the document is soft-deleted your endpoint will still be executed.

If you want soft-deleted documents to be treated as `NULL` for a endpoint, you can set `MaybeSoftDeleted` to `false`.  
In combination with `Required = true` that means the endpoint will return 404 for missing and soft-deleted documents.

<!-- snippet: sample_using_document_with_maybesoftdeleted -->
<a id='snippet-sample_using_document_with_maybesoftdeleted'></a>
```cs
[WolverineGet("/invoices/soft-delete/{id}")]
public static Invoice GetSoftDeleted([Document(Required = true, MaybeSoftDeleted = false)] Invoice invoice)
{
    return invoice;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L61-L67' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_document_with_maybesoftdeleted' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Marten Aggregate Workflow

The http endpoints can play inside the full "critter stack" combination with [Marten](https://martendb.io) with Wolverine's [specific
support for Event Sourcing and CQRS](/guide/durability/marten/event-sourcing). Originally this has been done
by just mimicking the command handler mechanism and having all the inputs come in through the request body (aggregate id, version).
Wolverine 1.10 added a more HTTP-centric approach using route arguments. 

Because folks always want to insert strong typed identifiers in every possible nook and cranny of their application code,
Wolverine 5.0 introduced support for using these custom value types as the stream and/or aggregate identity
in all usages of the aggregate handler workflow with Wolverine.HTTP.

### Using Route Arguments

::: tip
The `[Aggregate]` attribute was originally meant for the "aggregate handler workflow" where Wolverine is interacting with
Marten with the assumption that it will be appending events to Marten streams and getting you ready for versioning assertions.

If all you need is a read only copy of Marten aggregate data, the `[ReadAggregate]` is a lighter weight option. 

Also, the `[WriteAggregate]` attribute has the exact same behavior as the older `[Aggregate]`, but is available in both
message handlers and HTTP endpoints. You may want to prefer `[WriteAggregate]` just to be more clear in the code about
what's happening.
:::

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L146-L160' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_aggregate_attribute_1' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L162-L173' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_aggregate_attribute_2' title='Start of snippet'>anchor</a></sup>
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
    public string Name { get; set; } = null!;
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L18-L89' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_aggregate_for_http' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To append a single event to an event stream from an HTTP endpoint, you can use a return value like so:

<!-- snippet: sample_using_emptyresponse -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L122-L134' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_emptyresponse' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L236-L265' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_returning_multiple_events_from_http_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Responding with the Updated Aggregate

See the documentation from the message handlers on using [UpdatedAggregate](/guide/durability/marten/event-sourcing.html#returning-the-updated-aggregate) for more background on this topic. 

To return the updated state of a projected aggregate from Marten as the HTTP response from an endpoint using
the aggregate handler workflow, return the `UpdatedAggregate` marker type as the first "response value" of 
your HTTP endpoint like so:

<!-- snippet: sample_returning_updated_aggregate_as_response_from_http_endpoint -->
<a id='snippet-sample_returning_updated_aggregate_as_response_from_http_endpoint'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L293-L306' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_returning_updated_aggregate_as_response_from_http_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you should happen to have a message handler or HTTP endpoint signature that uses multiple event streams,
but you want the `UpdatedAggregate` to **only** apply to one of the streams, you can use the `UpdatedAggregate<T>`
to tip off Wolverine about that like in this sample:

<!-- snippet: sample_makepurchasehandler -->
<a id='snippet-sample_makepurchasehandler'></a>
```cs
public static class MakePurchaseHandler
{
    // See how we used the generic version
    // of UpdatedAggregate to tell Wolverine we 
    // want *only* the XAccount as the response
    // from this handler
    public static UpdatedAggregate<XAccount> Handle(
        MakePurchase command,

        [WriteAggregate] IEventStream<XAccount> account,

        [WriteAggregate] IEventStream<Inventory> inventory)
    {
        if (command.Number > inventory.Aggregate!.Quantity ||
            (command.Number * inventory.Aggregate.UnitPrice) > account.Aggregate!.Balance)
        {
            // Do Nothing!
            return new UpdatedAggregate<XAccount>();
        }
        
        account.AppendOne(new ItemPurchased(command.InventoryId, command.Number, inventory.Aggregate.UnitPrice));
        inventory.AppendOne(new Drawdown(command.Number));
        
        return new UpdatedAggregate<XAccount>();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/AggregateHandlerWorkflow/mixed_aggregate_handler_with_multiple_streams.cs#L86-L114' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_makepurchasehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
Wolverine can't (yet) handle a signature with multiple event streams of the same aggregate type and
`UpdatedAggregate`. 
:::

## Overriding Version Discovery <Badge type="tip" text="5.17" />

By default, Wolverine looks for a variable named `version` (from route arguments, query string, or request body) for
optimistic concurrency checks. In multi-stream scenarios, you can use the `VersionSource` property on `[Aggregate]` or
`[WriteAggregate]` to specify a different source:

```cs
// Version from route argument
[WolverinePost("/orders/{orderId}/ship/{expectedVersion}")]
[EmptyResponse]
public static OrderShipped Ship(
    ShipOrder command,
    [Aggregate(VersionSource = "expectedVersion")] Order order)
{
    return new OrderShipped();
}

// Version from request body member
public record ShipOrderWithVersion(Guid OrderId, long ExpectedVersion);

[WolverinePost("/orders/ship-versioned")]
[EmptyResponse]
public static OrderShipped Ship(
    ShipOrderWithVersion command,
    [Aggregate(VersionSource = nameof(ShipOrderWithVersion.ExpectedVersion))] Order order)
{
    return new OrderShipped();
}
```

See [Overriding Version Discovery](/guide/durability/marten/event-sourcing.html#overriding-version-discovery) in the
aggregate handler workflow documentation for more details and multi-stream examples.

## Custom Identity Resolution <Badge type="tip" text="5.25" />

By default, the `[Aggregate]` attribute resolves the stream identity from route arguments, query string parameters,
or request body properties. Starting in 5.25, you can use additional value sources to resolve the aggregate identity from
headers, claims, or computed methods. These same properties are available on all `WolverineParameterAttribute` subclasses
(`[Aggregate]`, `[WriteAggregate]`, `[ReadAggregate]`, etc.).

### From a Request Header

Use `FromHeader` to resolve the identity from an HTTP request header:

```cs
[WolverinePost("/orders/ship")]
[EmptyResponse]
public static OrderShipped Ship(
    ShipOrder command,
    [Aggregate(FromHeader = "X-Order-Id")] Order order)
{
    return new OrderShipped();
}
```

In message handlers, `FromHeader` reads from `Envelope.Headers` instead.

### From a Claim

Use `FromClaim` to resolve the identity from the authenticated user's claims. This is only
supported in HTTP endpoints:

```cs
[WolverinePost("/profile/update")]
[EmptyResponse]
public static ProfileUpdated UpdateProfile(
    UpdateProfile command,
    [Aggregate(FromClaim = "profile-id")] UserProfile profile)
{
    return new ProfileUpdated();
}
```

### From a Static Method

Use `FromMethod` to resolve the identity from a static method on the endpoint class. The method's
parameters are resolved via method injection (services, `ClaimsPrincipal`, etc.):

```cs
public static class UpdateAccountConfigEndpoint
{
    // Wolverine discovers this method and calls it to resolve the aggregate ID
    public static Guid ResolveId(ClaimsPrincipal user)
    {
        return AccountConfig.CompositeId(user.FindFirst("tenant")?.Value);
    }

    [WolverinePost("/account/config/update")]
    [EmptyResponse]
    public static AccountConfigUpdated Handle(
        UpdateAccountConfig command,
        [Aggregate(FromMethod = "ResolveId")] AccountConfig config)
    {
        return new AccountConfigUpdated();
    }
}
```

### From a Route Argument

Use `FromRoute` as a more explicit alternative to the constructor parameter:

```cs
[WolverinePost("/orders/{orderId}/ship")]
[EmptyResponse]
public static OrderShipped Ship(
    ShipOrder command,
    [Aggregate(FromRoute = "orderId")] Order order)
{
    return new OrderShipped();
}
```

## Reading the Latest Version of an Aggregate

::: info
This is using Marten's [FetchLatest(https://martendb.io/events/projections/read-aggregates.html#fetchlatest) API]() and is limited to single stream
projections. 
:::

If you want to inject the current state of an event sourced aggregate as a parameter into
an HTTP endpoint method, use the `[ReadAggregate]` attribute like this:

<!-- snippet: sample_using_readaggregate_in_http -->
<a id='snippet-sample_using_readaggregate_in_http'></a>
```cs
[WolverineGet("/orders/latest/{id}")]
public static Order GetLatest(Guid id, [ReadAggregate] Order order) => order;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L320-L324' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_readaggregate_in_http' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If the aggregate doesn't exist, the HTTP request will stop with a 404 status code. 
The aggregate/stream identity is found with the same rules as the `[Entity]` or `[Aggregate]` attributes:

1. You can specify a particular request body property name or route argument
2. Look for a request body property or route argument named "EntityTypeId"
3. Look for a request body property or route argument named "Id" or "id"

### Compiled Query Resource Writer Policy

Marten integration comes with an `IResourceWriterPolicy` policy that handles compiled queries as return types. 
Register it in `WolverineHttpOptions` like this:

<!-- snippet: sample_user_marten_compiled_query_policy -->
<a id='snippet-sample_user_marten_compiled_query_policy'></a>
```cs
opts.UseMartenCompiledQueryResultPolicy();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L282-L285' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_user_marten_compiled_query_policy' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L69-L75' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_compiled_query_return_endpoint' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L105-L114' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_compiled_query_return_query' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Streaming JSON Responses <Badge type="tip" text="5.32" />

[`Marten.AspNetCore`](https://martendb.io/documents/aspnetcore.html) ships three
typed return values — `StreamOne<T>`, `StreamMany<T>`, and `StreamAggregate<T>` —
that write Marten's raw JSON directly to the HTTP response. The JSON never
round-trips through a .NET object and the framework's serializer, so there's no
deserialize/serialize overhead.

Each type also supplies correct OpenAPI metadata (`Produces<T>`, `Produces(404)`
where appropriate) via `IEndpointMetadataProvider`, so Swashbuckle, NSwag, and
Minimal-API's built-in OpenAPI generator all see the right response shape.

The types implement `IResult`, so Wolverine.Http dispatches them through its
existing `ResultWriterPolicy` — **no extra Wolverine-specific configuration is
needed**. Just `using Marten.AspNetCore;` in your endpoint file and return one.

### When to use which

| Type                   | Source                                           | Shape returned | 404? |
| ---------------------- | ------------------------------------------------ | -------------- | ---- |
| `StreamOne<T>`         | `IQueryable<T>` — regular Marten document query  | Single `T`     | yes  |
| `StreamMany<T>`        | `IQueryable<T>` — regular Marten document query  | JSON array `T[]` | no (empty array = 200) |
| `StreamAggregate<T>`   | `IDocumentSession` + stream id — event-sourced   | Single `T`     | yes  |

**Key difference — `StreamOne<T>` vs `StreamAggregate<T>`**:

- **`StreamOne<T>`** is for regular Marten documents — plain objects persisted via
  `session.Store()` and queried with `session.Query<T>()`. The query hits the
  document table directly.
- **`StreamAggregate<T>`** is for event-sourced aggregates. Marten rebuilds the
  latest aggregate state by folding events from the event store (or reads a
  projected snapshot if you have one configured). Use this when `T` is an
  event-sourced aggregate, not a stored document.

### `StreamOne<T>` — single document with 404 on miss

```csharp
using Marten.AspNetCore;

[WolverineGet("/invoices/{id}")]
public static StreamOne<Invoice> Get(Guid id, IQuerySession session)
    => new(session.Query<Invoice>().Where(x => x.Id == id));
```

Returns `200 application/json` with the JSON body on a hit, `404` on a miss.
`Content-Length` and `Content-Type` are set automatically.

### `StreamMany<T>` — JSON array

```csharp
[WolverineGet("/invoices/approved")]
public static StreamMany<Invoice> Approved(IQuerySession session)
    => new(session.Query<Invoice>().Where(x => x.Approved));
```

Returns `200 application/json` with a JSON array body. An empty result set
returns `[]`, not `404`.

### `StreamAggregate<T>` — event-sourced aggregate (latest)

```csharp
[WolverineGet("/orders/{id}")]
public static StreamAggregate<Order> Get(Guid id, IDocumentSession session)
    => new(session, id);
```

Returns `200 application/json` with the JSON of the latest projected aggregate
state, or `404` if no stream exists for the supplied id. The constructor also
accepts `string` ids for stores configured with string-keyed streams.

### Customizing status code and content type

All three types expose init-only properties for overriding defaults:

```csharp
[WolverinePost("/invoices")]
public static StreamOne<Invoice> Create(CreateInvoice cmd, IQuerySession session)
    => new(session.Query<Invoice>().Where(x => x.Id == cmd.InvoiceId))
    {
        OnFoundStatus = StatusCodes.Status201Created,
        ContentType = "application/vnd.myapi.invoice+json"
    };
```

### When to prefer streaming over returning `T`

Reach for these types when:

- The response is large (big documents, long arrays) — avoids allocating the
  deserialized graph and re-serializing it
- You need fine-grained control over status code and content type without
  wrapping in `IResult`
- You want a concise, typed endpoint signature that still produces accurate
  OpenAPI metadata

For small responses where the query result is already going to be materialized
(to make a decision, for example), a plain `T` return is fine.
