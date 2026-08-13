# Persistence Helpers

Philosophically, Wolverine is trying to enable you to write the message handlers or HTTP endpoint
methods with low ceremony code that's easy to test and easy to reason about. To that end, Wolverine
has quite a few tricks to utilize your persistence tooling from your handler or HTTP endpoint code
without having to directly couple your behavioral code to persistence infrastructure:

* The [storage action side effect model](/guide/handlers/side-effects.html#storage-side-effects) for pure function handlers that involve database "writes"
* The [aggregate handler workflow](/guide/durability/marten/event-sourcing) with Marten for highly testable CQRS + Event Sourcing systems
* Specific [integration with Marten and Wolverine.HTTP](/guide/http/marten)

These all speak one vocabulary, and none of it names your database:

| You want | Use |
|---|---|
| A document or entity loaded for you | `[Entity]` |
| An event sourced model loaded for **writing**, with concurrency protection | `[WriteModel]` |
| An event sourced model's current state, read only | `[ReadModel]` |
| The whole method to be an event sourced command handler | `[DeciderFunction]` |
| An event sourced model spanning several streams, matched by tag | `[DcbModel]` |
| To write a document back | `Storage.Store` / `Insert` / `Update` / `Delete` / `Nothing<T>` |

## Automatically Loading Entities to Method Parameters <Badge type="tip" text="3.6" />

A common need when building Wolverine message handlers or HTTP endpoints is to need to load
an entity object based on an identity value in either the message itself, the HTTP request body, or
an HTTP route argument. In these cases, you'll generally pluck the correct value out of the 
message or route arguments, then call into an EF Core `DbContext` or a Marten/RavenDb `IDocumentSession`
to load the entity for you before proceeding on with your work. Since this usage is so common,
Wolverine has the `[Wolverine.Persistence.Entity]` attribute to just do that for you and have the right entity "pushed" into
your message handler. 

Here's a simple example of a message handler that's also a valid Wolverine.HTTP endpoint using this attribute. First though,
the message type and/or HTTP request body:

<!-- snippet: sample_rename_todo -->
<a id='snippet-sample_rename_todo'></a>
```cs
public record RenameTodo(string Id, string Name);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Todos/Todo2.cs#L23-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rename_todo' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and the handler & endpoint code handling that message type:

<!-- snippet: sample_using_entity_attribute -->
<a id='snippet-sample_using_entity_attribute'></a>
```cs
// Use "Id" as the default member
[WolverinePost("/api/todo/update")]
public static Update<Todo2> Handle(
    // The first argument is always the incoming message
    RenameTodo command, 
    
    // By using this attribute, we're telling Wolverine
    // to load the Todo entity from the configured
    // persistence of the app using a member on the
    // incoming message type
    [Entity] Todo2 todo)
{
    // Do your actual business logic
    todo.Name = command.Name;
    
    // Tell Wolverine that you want this entity
    // updated in persistence
    return Storage.Update(todo);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Todos/Todo2.cs#L54-L75' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_entity_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the code above, the `Todo2` argument would be filled by trying to load that `Todo2` entity
from persistence using the value of `RenameTodo.Id`. If you were using Marten as your persistence
mechanism, this would be using `IDocumentSession.LoadAsync<Todo2>(id)` to load the entity with the RavenDb usage being similar. If
you were using EF Core and had an `Todo2DbContext` service registered in your system, it would
be using `Todo2DbContext.FindAsync<Todo2>(id)`. 

By default, Wolverine is assuming that any parameter value marked with `[Entity]` is required, so if the `Todo2` entity was not found in the database, then:

* As a message handler, it will just log that the entity could not be found and otherwise exit cleanly without doing any further processing
* As an HTTP endpoint, the handler would write out a status code of 404 (not found) and exit otherwise

If you need or want any other kind of failure handling on the entity not being found, you'll need to
use explicit code instead, maybe with a `LoadAsync()` "before" method to still keep your main
handler or endpoint method a *pure function*. 

If you genuinely don't need the `[Entity]` value to be required, you can do this instead:

<!-- snippet: sample_using_not_required_entity_attribute -->
<a id='snippet-sample_using_not_required_entity_attribute'></a>
```cs
[WolverinePost("/api/todo/maybecomplete")]
public static IStorageAction<Todo2> Handle(MaybeCompleteTodo command, [Entity(Required = false)] Todo2? todo)
{
    if (todo == null) return Storage.Nothing<Todo2>();
    todo.IsComplete = true;
    return Storage.Update(todo);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Todos/Todo2.cs#L142-L151' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_not_required_entity_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

So far, all of the examples have depended on a fall back to looking for either a case insensitive match "id"  
match on the message members for message handlers or the route arguments, then request input members
for HTTP endpoints. Wolverine will also look for "[Entity Type Name]Id", so in the case of `Todo2`, it would
look as well for a more specific `Todo2Id` member or route argument for the identity value. 

You can of course override this by just telling Wolverine what member name or route argument name
should have the identity like this:

<!-- snippet: sample_specifying_the_exact_route_argument -->
<a id='snippet-sample_specifying_the_exact_route_argument'></a>
```cs
// Okay, I still used "id", but it *could* be something different here!
[WolverineGet("/api/todo/{id}")]
public static Todo2 Get([Entity("id")] Todo2 todo) => todo;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Todos/Todo2.cs#L153-L158' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_specifying_the_exact_route_argument' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you have any conflict between whether the identity should be found on either the route arguments
or request body, you can specify the identity value source through the `EntityAttribute.ValueSource` property
to one of these values:

<!-- snippet: sample_valuesource -->
<a id='snippet-sample_valuesource'></a>
```cs
public enum ValueSource
{
    /// <summary>
    /// This value can be sourced by any mechanism that matches the name. This is the default.
    /// </summary>
    Anything,
    
    /// <summary>
    /// The value should be sourced by a property or field on the message type or HTTP request type
    /// </summary>
    InputMember,
    
    /// <summary>
    /// The value should be sourced by a route argument of an HTTP request
    /// </summary>
    RouteValue,
    
    /// <summary>
    /// The value should be sourced by a query string parameter of an HTTP request
    /// </summary>
    FromQueryString,

    /// <summary>
    /// The value should be sourced by an HTTP request header or an Envelope header in message handlers
    /// </summary>
    Header,

    /// <summary>
    /// The value should be sourced from a claim on the ClaimsPrincipal. Only supported in HTTP endpoints.
    /// </summary>
    Claim,

    /// <summary>
    /// The value should be sourced from the return value of a named static method on the handler or endpoint class
    /// </summary>
    Method
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Attributes/ModifyChainAttribute.cs#L18-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_valuesource' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Event Sourced Models <Badge type="tip" text="6.26" />

`[Entity]` resolves a *document* from whatever persistence your application configured. Its
counterparts for an *event sourced* model are `[WriteModel]` and `[ReadModel]`, and they work the
same way: you say what the parameter is for, and Wolverine works out which store owns it from the
persistence you already registered. The same handler code is valid whether that store is Marten or
Polecat.

Use `[WriteModel]` when the handler is going to emit events. Wolverine loads the model's event
stream with concurrency protection, hands the current state to your method, and appends whatever
events you return back to that stream:

<!-- snippet: sample_using_write_model_attribute -->
<a id='snippet-sample_using_write_model_attribute'></a>
```cs
public static class ShipOrderHandler
{
    // [WriteModel] loads the Order's event stream with concurrency protection, hands you
    // the current state, and appends whatever events you return back to that same stream.
    // Nothing here names an event store -- the same handler is valid on Marten or Polecat.
    public static OrderShipped Handle(ShipOrder command, [WriteModel] Order order)
    {
        return new OrderShipped(DateTimeOffset.UtcNow);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L29-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_write_model_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Use `[DeciderFunction]` for the same workflow at the method or class level, where the model's
identity is read off the incoming command rather than off a marked parameter. The name is the
functional event sourcing one — a decider is `decide(command, state) -> events`, which is exactly
what this workflow gives you: Wolverine supplies the `state` and persists the `events`, and your
method is a pure function in between.

<!-- snippet: sample_using_decider_function_attribute -->
<a id='snippet-sample_using_decider_function_attribute'></a>
```cs
// [DeciderFunction] is the method (or class) level form: it reads the model's identity off the
// incoming command -- MarkItemReady.OrderId here -- rather than off a marked parameter. The name
// is the functional event sourcing one: decide(command, state) -> events.
[DeciderFunction]
public static class MarkItemReadyHandler
{
    public static OrderItemReady Handle(MarkItemReady command, Order order)
    {
        return new OrderItemReady(command.Item);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L44-L58' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_decider_function_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Use `[ReadModel]` when the handler only needs to *look at* the model. It resolves the current state
through the store's `FetchLatest()` API, takes no stream lock, and expects no events back:

<!-- snippet: sample_using_read_model_attribute -->
<a id='snippet-sample_using_read_model_attribute'></a>
```cs
public static class ReadOrderStatusHandler
{
    // [ReadModel] resolves the model's current state through the store's FetchLatest() API.
    // No stream lock is taken, and Wolverine does not expect any events back.
    public static OrderStatusReport Handle(ReadOrderStatus query, [ReadModel] Order order)
    {
        return new OrderStatusReport(order.Shipped);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L60-L72' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_read_model_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

By default `[WriteModel]` and `[DeciderFunction]` use an optimistic concurrency check at the point
of commit. Opt into an exclusive lock on the stream instead with `ModelConcurrencyStyle.Exclusive`:

<!-- snippet: sample_write_model_with_exclusive_locking -->
<a id='snippet-sample_write_model_with_exclusive_locking'></a>
```cs
public static class ShipOrderExclusivelyHandler
{
    public static OrderShipped Handle(ShipOrder command,
        [WriteModel(LoadStyle = ModelConcurrencyStyle.Exclusive)] Order order)
    {
        return new OrderShipped(DateTimeOffset.UtcNow);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L74-L85' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_write_model_with_exclusive_locking' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Dynamic Consistency Boundaries <Badge type="tip" text="6.27" />

`[WriteModel]` and `[DeciderFunction]` are both about *one* stream. When the decision spans several —
"is this seat still free for this screening, and is this customer under their booking limit?" —
`[DcbModel]` is the same workflow over a **Dynamic Consistency Boundary**: a model projected from
every stream whose events match a tag query, with the store asserting at commit that no matching
event has landed in the meantime.

Because the boundary is a query rather than a stream id, the handler declares it in a `Load()` (or
`LoadAsync()` / `Before()` / `BeforeAsync()`) method returning an `EventTagQuery`:

<!-- snippet: sample_using_dcb_model_attribute -->
<a id='snippet-sample_using_dcb_model_attribute'></a>
```cs
public static class ReserveSeatHandler
{
    // A Dynamic Consistency Boundary is not one stream, so you say which events it spans with a
    // Load() (or Before()) method returning an EventTagQuery. Wolverine passes it to the store's
    // FetchForWritingByTags<T>().
    public static EventTagQuery Load(ReserveSeat command)
        => EventTagQuery.For(command.ScreeningId).Or(command.CustomerId);

    // [DcbModel] hands you the model projected from every event the query matched, and appends
    // what you return through the boundary -- with the store checking at commit that no matching
    // event has been written since. Nothing here names an event store.
    public static SeatReserved Handle(ReserveSeat command, [DcbModel] SeatAvailability availability)
    {
        if (availability.SeatsLeft <= 0)
        {
            throw new InvalidOperationException("The screening is sold out");
        }

        return new SeatReserved(command.ScreeningId, command.CustomerId);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L97-L121' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_dcb_model_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Mark the parameter as `IEventBoundary<T>` instead of `T` if you want the boundary handle itself —
its `Events`, its `LastSeenSequence`, and `AppendOne()` / `AppendMany()` for appending imperatively
rather than by returning events.

::: warning
DCB is newer than the single-stream workflow and not every event store integration implements it.
`[DcbModel]` against a store that does not will tell you so at codegen time.
:::

Identity resolution follows the same conventions as `[Entity]`, in this order: an explicit
`[WriteModel("orderId")]`, then a member marked with `[JasperFx.Identity]` on the message, then a
`[Model Type Name]Id` member, then `id`, then a single member of the model's strong typed id type.
The `[Identity]` step is the one to reach for when the identity lives on a member whose name says
something else — it is declared on the message, where it is true regardless of which handler form
reads it.

Missing-model behavior matches `[Entity]` as well: `Required`, `MissingMessage` and `OnMissing` mean
what they mean there, and both attributes honor the
[global entity defaults](#global-entity-defaults) below.

On `[WriteModel]`, `Required` **defaults to the opposite of the parameter's nullable annotation**
<Badge type="tip" text="6.27" /> — `Order order` is required and gets a not-found guard, `Order? order`
is not and is handed to your method as `null` so your own null branch runs. Setting `Required`
explicitly overrides the annotation either way.

::: warning Every return value is an event
Under `[WriteModel]` and `[DeciderFunction]` alike, anything the method returns that is not
recognized as something else is **appended to the model's event stream**. That includes a value you
may have meant to publish as a cascading message — it will land in the stream instead, quietly.

To do both, return a tuple: the events collection is appended and the other member is published.

```cs
public static (IReadOnlyList<object>, OrderShipmentNotice) Handle(
    ShipOrder command, [WriteModel] Order order)
{
    return ([new OrderShipped(DateTimeOffset.UtcNow)], new OrderShipmentNotice(command.OrderId));
}
```
:::

::: tip
Every store integration also ships its own name for these — `[WriteAggregate]`, `[ReadAggregate]`,
`[AggregateHandler]` and `[BoundaryModel]` in both `Wolverine.Marten` and `Wolverine.Polecat`, plus
`[Aggregate]` in the matching `Wolverine.Http.*` package. As of Wolverine 6.26 (6.27 for
`[BoundaryModel]`) those all inherit from the attributes above and behave identically, so existing
code needs no change. Prefer the store-agnostic names in new code.

One thing to know if you mix them: `Wolverine.Marten` and `Wolverine.Polecat` each have their own
public `ConcurrencyStyle` enum, which is why the core one is `ModelConcurrencyStyle`. A file that
imports both `Wolverine.Marten` and `Wolverine.Persistence.EventSourcing` sees both names, and they
do not collide.
:::

## Global Entity Defaults <Badge type="tip" text="5.16" />

If you want consistent entity-missing behavior across your entire application without having to set `OnMissing` or
`MaybeSoftDeleted` on every single `[Entity]`, `[Document]`, `[Aggregate]`, `[ReadAggregate]`, or `[WriteAggregate]`
attribute, you can configure global defaults through `WolverineOptions.EntityDefaults`:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Set global defaults for all entity-loading attributes
        opts.EntityDefaults.OnMissing = OnMissing.ProblemDetailsWith404;
        opts.EntityDefaults.MaybeSoftDeleted = false;
    }).StartAsync();
```

With the configuration above, every `[Entity]` parameter that does not explicitly set `OnMissing` will use
`ProblemDetailsWith404` instead of the built-in `Simple404` default. Likewise, every `[Entity]` parameter that
does not explicitly set `MaybeSoftDeleted` will treat soft-deleted entities as missing.

You can still override the global default on any individual attribute:

```cs
public static class MyHandler
{
    // This handler uses the global default for OnMissing
    public static MyResult Handle(MyCommand command, [Entity] MyEntity entity)
    {
        // ...
    }

    // This handler explicitly overrides to ThrowException regardless of the global default
    public static MyResult Handle(MyOtherCommand command,
        [Entity(OnMissing = OnMissing.ThrowException)] MyEntity entity)
    {
        // ...
    }
}
```

The resolution order is: **Explicit attribute value > Global default > Built-in default** (`Simple404` / `true`).

Some other facts to know about `[Entity]` usage:

* Supported by the Marten, EF Core, and RavenDb integration
* For EF Core usage, Wolverine has to be able to figure out which `DbContext` type persists the entity type of the parameter
* In all cases, Wolverine is trying to "know" what the identity type for the entity type is (`Guid`? `int`? Something else?) from the underlying persistence tooling and use that to help parse route arguments as needed
* `[Entity]` cannot support any kind of composite key or identity
* `[Entity]` can be used for both HTTP endpoints and message handler methods
* `[Entity]` can be used for `Before` / `Validate` methods in compound handlers
* If an `[Entity]` attribute is used in the main handler or endpoint method, you can still resolve the same entity type as a parameter to a `Before` method without needing to use the attribute again

::: tip
As with other kinds of Wolverine "magic", lean on the [pre-generated code](/guide/codegen) to let Wolverine explain
what it's doing with your method signatures.
:::
