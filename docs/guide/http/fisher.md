# Integration with Fisher

The `Wolverine.Http.Fisher` library adds the ability to more deeply integrate Fisher
into Wolverine.HTTP by utilizing information from route arguments.

To install that library, use:

```bash
dotnet add package WolverineFx.Http.Fisher
```

This is the Fisher counterpart to [Wolverine.Http.Marten](/guide/http/marten) and
[Wolverine.Http.Polecat](/guide/http/polecat) — the three packages carry the same attributes over
their respective stores, so the usage below will look familiar if you have used either sibling.

## Passing Fisher Documents to Endpoint Parameters

::: tip
The `[Entity]` attribute is supported by both message handlers and HTTP endpoints for loading documents by identity.
:::

Consider a common case: an HTTP endpoint that works on a Fisher document loaded by the value of one
of the route arguments. Longhand, that is:

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

The `[Entity]` attribute was able to use the "id" route parameter. By default, Wolverine looks first
for a route variable named "invoiceId" (the document type name + "Id"), then falls back to "id". You
can override the matching explicitly:

```cs
[WolverinePost("/invoices/{number}/approve")]
public static IFisherOp Approve([Entity("number")] Invoice invoice)
{
    invoice.Approved = true;
    return FisherOps.Store(invoice);
}
```

If the `Invoice` document does not exist, the route stops and returns a 404. Set `Required` to
`false` to have your handler execute anyway, or use the `OnMissing` property for any other answer —
a `ProblemDetails` body, a thrown exception, or an empty `204` with `OnMissing.EmptyContentWith204`.
See [the full `OnMissing` table](/guide/handlers/persistence#using-entity-for-message-handlers-and-http-endpoints).

## Fisher Aggregate Workflow

HTTP endpoints can play inside the full Wolverine + Fisher combination described in
[the Fisher integration guide](/guide/durability/fisher/).

To opt into the aggregate workflow using a route argument for the aggregate id, use the
`[Aggregate]` attribute on an endpoint method parameter:

```cs
[WolverinePost("/orders/{orderId}/ship"), EmptyResponse]
public static OrderShipped Ship(ShipOrder command, [Aggregate] Order order)
{
    if (order.HasShipped)
        throw new InvalidOperationException("This has already shipped!");

    return new OrderShipped();
}
```

You do not have to supply a command in the request body at all:

```cs
[WolverinePost("/orders/{orderId}/ship2"), EmptyResponse]
public static OrderShipped Ship2([Aggregate] Order order)
{
    return new OrderShipped();
}
```

A couple of notes:

* Return value handling for events follows the same rules as the message handler workflow
* The endpoint returns a 404 response code if the aggregate does not exist
* The aggregate id can be set explicitly, like `[Aggregate("number")]`
* This usage automatically applies the transactional middleware

### Always Enforcing Consistency

`[ConsistentAggregate]` behaves exactly as `[Aggregate]` except that it sets
`AlwaysEnforceConsistency`, so Fisher enforces an optimistic concurrency check on the referenced
stream even when the endpoint appends no events:

```cs
[WolverinePost("/orders/{orderId}/confirm"), EmptyResponse]
public static OrderConfirmed Confirm([ConsistentAggregate] Order order)
{
    return new OrderConfirmed();
}
```

### Overriding Version Discovery

By default, Wolverine looks for a variable named `version` for optimistic concurrency checks. Use
`VersionSource` to point at a different one:

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

## Reading the Latest Version of an Aggregate

To inject the current state of an event sourced aggregate as a parameter without opting into the
write workflow, use the `[ReadAggregate]` attribute:

```cs
[WolverineGet("/orders/latest/{id}")]
public static Order GetLatest(Guid id, [ReadAggregate] Order order) => order;
```
