# Integration with Polecat

The `Wolverine.Http.Polecat` library adds the ability to more deeply integrate Polecat
into Wolverine.HTTP by utilizing information from route arguments.

To install that library, use:

```bash
dotnet add package WolverineFx.Http.Polecat
```

## Passing Polecat Documents to Endpoint Parameters

::: tip
The `[Entity]` attribute is supported by both message handlers and HTTP endpoints for loading documents by identity.
:::

Consider this very common use case: you have an HTTP endpoint that needs to work on a Polecat document that will
be loaded using the value of one of the route arguments as that document's identity. In a long hand way, that could
look like this:

```cs
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

Using the `[Entity]` attribute, this becomes much simpler:

```cs
[WolverineGet("/invoices/{id}")]
public static Invoice Get([Entity] Invoice invoice)
{
    return invoice;
}
```

Notice that the `[Entity]` attribute was able to use the "id" route parameter. By default, Wolverine is looking first
for a route variable named "invoiceId" (the document type name + "Id"), then falling back to looking for "id". You can
explicitly override the matching of route argument like so:

```cs
[WolverinePost("/invoices/{number}/approve")]
public static IPolecatOp Approve([Entity("number")] Invoice invoice)
{
    invoice.Approved = true;
    return PolecatOps.Store(invoice);
}
```

In the code above, if the `Invoice` document does not exist, the route will stop and return a status code 404 for Not Found.

If you want your handler executed even if the document does not exist, set `Required` to `false`.

## Polecat Aggregate Workflow

The HTTP endpoints can play inside the full Wolverine + Polecat combination with Wolverine's [specific
support for Event Sourcing and CQRS](/guide/durability/polecat/event-sourcing).

### Using Route Arguments

::: tip
The `[Aggregate]` attribute was originally meant for the "aggregate handler workflow" where Wolverine is interacting with
Polecat with the assumption that it will be appending events to streams and getting you ready for versioning assertions.

If all you need is a read only copy of aggregate data, the `[ReadAggregate]` is a lighter weight option.

Also, the `[WriteAggregate]` attribute has the exact same behavior as the older `[Aggregate]`, but is available in both
message handlers and HTTP endpoints.
:::

To opt into the Wolverine + Polecat "aggregate workflow" using data from route arguments for the aggregate id,
use the `[Aggregate]` attribute on endpoint method parameters:

```cs
[WolverinePost("/orders/{orderId}/ship2"), EmptyResponse]
public static OrderShipped Ship(ShipOrder2 command, [Aggregate] Order order)
{
    if (order.HasShipped)
        throw new InvalidOperationException("This has already shipped!");

    return new OrderShipped();
}
```

Using this version, you no longer have to supply a command in the request body:

```cs
[WolverinePost("/orders/{orderId}/ship3"), EmptyResponse]
public static OrderShipped Ship3([Aggregate] Order order)
{
    return new OrderShipped();
}
```

A couple notes:

* The return value handling for events follows the same rules as shown in the event sourcing section
* The endpoints will return a 404 response code if the aggregate does not exist
* The aggregate id can be set explicitly like `[Aggregate("number")]`
* This usage will automatically apply the transactional middleware

### Using Request Body

::: tip
This usage only requires Wolverine.Polecat and does not require the Wolverine.Http.Polecat library
:::

For context, let's say we have these events and aggregate to model an `Order` workflow:

```cs
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
public record OrderShipped;
public record OrderCreated(Item[] Items);
public record OrderReady;
public record ItemReady(string Name);

public class Order
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public Dictionary<string, Item> Items { get; set; } = new();
    public bool HasShipped { get; set; }

    public void Apply(ItemReady ready)
    {
        Items[ready.Name].Ready = true;
    }

    public bool IsReadyToShip()
    {
        return Items.Values.All(x => x.Ready);
    }
}
```

To append a single event to an event stream from an HTTP endpoint:

```cs
[AggregateHandler]
[WolverinePost("/orders/ship"), EmptyResponse]
public static OrderShipped Ship(ShipOrder command, Order order)
{
    return new OrderShipped();
}
```

Or potentially append multiple events using the `Events` type:

```cs
[AggregateHandler]
[WolverinePost("/orders/itemready")]
public static (OrderStatus, Events) Post(MarkItemReady command, Order order)
{
    var events = new Events();

    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;
        events += new ItemReady(command.ItemName);
    }
    else
    {
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    if (order.IsReadyToShip())
    {
        events += new OrderReady();
    }

    return (new OrderStatus(order.Id, order.IsReadyToShip()), events);
}
```

### Responding with the Updated Aggregate

See the documentation from the message handlers on using [UpdatedAggregate](/guide/durability/polecat/event-sourcing#returning-the-updated-aggregate) for more background on this topic.

To return the updated state of a projected aggregate from Polecat as the HTTP response:

```cs
[AggregateHandler]
[WolverinePost("/orders/{id}/confirm2")]
public static (UpdatedAggregate, Events) ConfirmDifferent(ConfirmOrder command, Order order)
{
    return (
        new UpdatedAggregate(),
        [new OrderConfirmed()]
    );
}
```

## Overriding Version Discovery

By default, Wolverine looks for a variable named `version` for optimistic concurrency checks. Use `VersionSource` to specify a different source:

```cs
[WolverinePost("/orders/{orderId}/ship/{expectedVersion}")]
[EmptyResponse]
public static OrderShipped Ship(
    ShipOrder command,
    [Aggregate(VersionSource = "expectedVersion")] Order order)
{
    return new OrderShipped();
}
```

See [Overriding Version Discovery](/guide/durability/polecat/event-sourcing#overriding-version-discovery) for more details.

## Reading the Latest Version of an Aggregate

If you want to inject the current state of an event sourced aggregate as a parameter into
an HTTP endpoint method, use the `[ReadAggregate]` attribute:

```cs
[WolverineGet("/orders/latest/{id}")]
public static Order GetLatest(Guid id, [ReadAggregate] Order order) => order;
```

If the aggregate doesn't exist, the HTTP request will stop with a 404 status code.
