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
| A stream's metadata — version, type, timestamps — **unfolded** | [`[StreamState]`](#raw-stream-reads) |
| A stream's raw events, **unfolded** | [`[StreamEvents]`](#raw-stream-reads) |
| To write a document back | `Storage.Store` / `Insert` / `Update` / `Delete` / `Nothing<T>` |
| To append events to a stream | [`Storage.AppendEvents` / `Storage.StartStream`](/guide/handlers/side-effects#event-side-effects) |
| Every document of a type | [`[All]`](#reading-every-document-of-a-type) |
| The store's raw `IQueryable<T>` | [`[Queryable]`](#the-raw-iqueryable-escape-hatch) |
| The event store's write API | [`IEventStoreOperations`](#injecting-the-event-store-operations) |

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

The same attribute works against a document stored as an Amazon S3 object, once its type is
registered with `UseAmazonS3Persistence()` -- see the [Amazon S3 integration](/guide/durability/amazon-s3).
There the identity is mapped to an object key by the key function you registered, rather than by the
store.

By default, Wolverine is assuming that any parameter value marked with `[Entity]` is required, so if the `Todo2` entity was not found in the database, then:

* As a message handler, it will just log that the entity could not be found and otherwise exit cleanly without doing any further processing
* As an HTTP endpoint, the handler would write out a status code of 404 (not found) and exit otherwise

You can choose a different answer with the `OnMissing` property:

| `OnMissing` | Message handler | HTTP endpoint |
|---|---|---|
| `Simple404` (default) | Log it and stop | Empty **404** |
| `ProblemDetailsWith400` | Log it and stop | **400** with a `ProblemDetails` body |
| `ProblemDetailsWith404` | Log it and stop | **404** with a `ProblemDetails` body |
| `EmptyContentWith204` <Badge type="tip" text="6.28" /> | Log it and stop | Empty **204** |
| `ThrowException` | Throws `RequiredDataMissingException` | Throws `RequiredDataMissingException` |

If you need or want any other kind of failure handling on the entity not being found, you'll need to
use explicit code instead, maybe with a `LoadAsync()` "before" method to still keep your main
handler or endpoint method a *pure function*.

### Answering 204 instead of 404 <Badge type="tip" text="6.28" />

A bare 404 is indistinguishable from "you called a Url that does not exist." If you would rather say
"the Url is correct, but there is no body," use `OnMissing.EmptyContentWith204`:

```cs
[WolverineGet("/api/alerts/config/services/{serviceName}")]
public static ServiceAlertOverrides Get(
    [Entity(OnMissing = OnMissing.EmptyContentWith204)] ServiceAlertOverrides overrides)
    => overrides;
```

A request for a `serviceName` that has no overrides answers `204` with an empty body, and the generated
OpenAPI advertises `200` and `204` rather than `200` and `404`.

::: warning
Think about your clients before you reach for this. A 404 puts a miss on the failure branch of every
HTTP client; a 204 puts it on the success branch. Code that does `response.EnsureSuccessStatusCode()`
will start passing, and generated typed clients (NSwag, Kiota, Refit) will map the 204 onto their
success path — so a miss can surface later as a null dereference instead of at the call site. If what
you actually want is a *distinguishable* 404, `OnMissing.ProblemDetailsWith404` names the type and the
identity in a `application/problem+json` body and keeps the failure on the failure branch.
:::

On a `GET` or `QUERY` endpoint, `EmptyContentWith204` also forces the entity to be treated as required
even if you wrote `Required = false`. Running the endpoint body with a null entity so it can return an
empty body anyway buys nothing, and it is the one combination where "not required" and "answer 204"
contradict each other. This does not apply to message handlers or to other HTTP methods.

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

## Reading the First of a Type <Badge type="tip" text="6.28" />

`[Entity]` needs an identity to load by, which means it cannot express the *singleton document* — a type
your system stores exactly one of, looked up by nothing at all. That is what `[FirstOrDefault]` is for. It
is the equivalent of `await session.Query<T>().FirstOrDefaultAsync()`, resolved through whichever
persistence provider owns the type:

```cs
[WolverineGet("/api/alerts/config/metrics/defaults")]
public static MetricsAlertDefaults GetMetricsDefaults([FirstOrDefault] MetricsAlertDefaults? defaults)
    => defaults ?? new MetricsAlertDefaults();
```

The point is storage agnostic code: that handler is valid whether the store behind it is Marten, Polecat,
Fisher, RavenDb, or EF Core, and it replaces the hand written version that had to name a session type:

```cs
// What [FirstOrDefault] replaces -- correct, but pinned to Marten
[WolverineGet("/api/alerts/config/metrics/defaults")]
public static async Task<MetricsAlertDefaults> GetMetricsDefaults(IDocumentSession session)
{
    var defaults = await session.Query<MetricsAlertDefaults>().FirstOrDefaultAsync();
    return defaults ?? new MetricsAlertDefaults();
}
```

Some things to know:

* The parameter is simply `null` when nothing matches, and your handler or endpoint **runs either way**.
  Unlike `[Entity]`, there is no `Required` and no `OnMissing` — a miss here is not an error condition
  worth a 404, it is an ordinary answer to "is there one of these yet?". Write your own null branch,
  usually a `?? new T()` fallback.
* The query is **unfiltered**. If you need a predicate, use a `Before` method, a compiled query, or
  `[FromQuerySpecification]`; ordering semantics across five different LINQ providers is not something
  this attribute tries to promise.
* Usable in both message handlers and HTTP endpoints, and in `Before` / `Validate` methods.
* Supported by Marten, Polecat, Fisher, RavenDb, and EF Core. **CosmosDb is not supported** — see below.

::: warning
CosmosDb cannot support `[FirstOrDefault]`. Wolverine's CosmosDb integration stores every user document
in a single shared `wolverine` container alongside Wolverine's own envelopes and node records, with no
per-type discriminator on user documents, so there is no way to ask for "the first document of type `T`"
without risking a different type entirely. A `[FirstOrDefault]` parameter on a CosmosDb-persisted type
fails at bootstrapping time with an error naming the provider, rather than returning something wrong at
runtime. Load the value explicitly in a `Before` method instead.
:::

::: tip
On Fisher, a document table is created lazily on first write, and querying a type that has never been
written throws rather than returning nothing. That applies to any Fisher query, not just this attribute,
but it is worth knowing if a brand new deployment hits a `[FirstOrDefault]` before anything is stored.
:::

## Reading Every Document of a Type <Badge type="tip" text="6.28" />

Where [`[FirstOrDefault]`](#reading-the-first-of-a-type) gives you one, `[All]` gives you all of them —
the equivalent of `await session.Query<T>().ToListAsync()`, resolved through whichever provider owns the
type:

```cs
[WolverineGet("/api/alerts/config/services")]
public static IReadOnlyList<ServiceAlertOverrides> GetAll([All] IReadOnlyList<ServiceAlertOverrides> overrides)
    => overrides;
```

* The parameter **must** be declared as `IReadOnlyList<T>`. Anything else fails with a message naming the
  parameter and what to change it to. That is the shape Marten and RavenDb return from `ToListAsync()`
  natively, and EF Core's `List<T>` converts to it implicitly, so every provider assigns straight across
  with no copying.
* An empty table yields an empty list, never `null` — so there is no "missing" case and no `OnMissing`.
* The query is unfiltered. This is aimed at **small reference and configuration collections**; reading an
  entire table into memory is a decision, not a default.
* Supported by Marten, Polecat, Fisher, RavenDb and EF Core. **CosmosDb is not supported**, for the same
  reason `[FirstOrDefault]` is not — see that section's warning.

### Batched Reads <Badge type="tip" text="6.28" />

On Marten, Polecat and Fisher, **two or more** batchable reads in the same handler or endpoint are resolved
in a *single database round trip* rather than one query each. Nothing to turn on — write the parameters and
Wolverine batches them:

```cs
public static InventoryCounted Handle(
    CountInventory command,
    [All] IReadOnlyList<Part> parts,
    [All] IReadOnlyList<Supplier> suppliers)
    => new(parts.Count, suppliers.Count);
```

That generates one `CreateBatchQuery()`, enlists both reads, executes once, and then resolves each result —
instead of two separate round trips. `[All]` batches alongside the other batchable operations too, so an
`[All]` next to an `[Entity]` load or a query specification joins the same batch.

A **single** read is deliberately left alone: the batch machinery buys nothing for one query, so a lone
`[All]` still emits the plain `Query<T>().ToListAsync()`.

::: tip
This is why `[All]` is worth preferring over [`[Queryable]`](#the-raw-iqueryable-escape-hatch) when you
genuinely want the whole collection — a queryable you compose yourself cannot participate in the batch.
:::

## The Raw `IQueryable` Escape Hatch <Badge type="tip" text="6.28" />

`[Queryable]` injects the persistence mechanism's own `IQueryable<T>` — Marten's `session.Query<T>()`,
EF Core's `dbContext.Set<T>()`, and so on — into a message handler, HTTP endpoint, or middleware method:

```cs
[WolverineGet("/api/alerts/recent")]
public static async Task<IReadOnlyList<Alert>> GetRecent(
    [Queryable] IQueryable<Alert> alerts, CancellationToken token)
{
    return await alerts
        .Where(x => x.Level == "high")
        .OrderByDescending(x => x.RaisedAt)
        .Take(20)
        .ToListAsync(token);
}
```

::: danger Read this before using `[Queryable]`
This is the escape hatch, and it is a sharp one. Every other attribute on this page describes *what* you
want and leaves the store to satisfy it. This one hands you a provider-specific LINQ implementation.

**It is not portable in practice, even though the type is.** Marten, EF Core, RavenDb and CosmosDb LINQ
providers support very different subsets of LINQ. A query that compiles and runs correctly on one can
throw at *runtime* on another. The concrete example that will catch you: **Marten 9 refuses synchronous
LINQ execution outright**, so

```cs
var names = alerts.Where(x => x.Level == "high").ToArray();   // compiles everywhere
```

works on EF Core and throws `NotSupportedException: As of Marten 9.0, only asynchronous data access is
supported` on Marten. **Always use the async LINQ operators** — `ToListAsync()`, `FirstOrDefaultAsync()`,
`CountAsync()` — and pass the `CancellationToken`.

**It reintroduces the coupling everything else here exists to remove**, and makes the method meaningfully
harder to unit test — you can no longer hand it a list.

**An unbounded query is easy to write by accident.** There is no paging, no limit, and no guard.

**On CosmosDb especially:** Wolverine stores every user document in one shared container with no per-type
discriminator, so an unfiltered queryable can surface documents of entirely other types deserialized as
`T`. Filter on a discriminator of your own.
:::

Prefer `[All]` for a whole small collection, `[Entity]` for a single entity by identity, or a compiled
query / `[FromQuerySpecification]` for anything filtered that you want to stay testable and portable.

## Injecting the Event Store Operations <Badge type="tip" text="6.28" />

A handler, HTTP endpoint, or middleware method can take `JasperFx.Events.IEventStoreOperations` (or the
narrower write-only `IEventOperations`) directly as a parameter, and it resolves to the current session's
`Events` on Marten, Polecat and Fisher alike:

```cs
public static void Handle(RecordLedgerEntry command, IEventStoreOperations events)
{
    events.StartStream(command.Id, new LedgerEntryRecorded(command.Note));
}
```

Because it is the *current session's* operations, the appended events commit with the rest of the
handler's work through the outbox — no `[Transactional]` needed. A handler marked
`[Storage(typeof(IMyStore))]` gets that ancillary store's session instead.

::: tip
Returning [`Storage.AppendEvents()` / `Storage.StartStream()`](/guide/handlers/side-effects#event-side-effects)
is the lower ceremony option and keeps the handler a pure function. Reach for the injected operations when
you need something those two do not express.
:::

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L30-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_write_model_attribute' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L45-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_decider_function_attribute' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L61-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_read_model_attribute' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L75-L86' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_write_model_with_exclusive_locking' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Raw Stream Reads <Badge type="tip" text="6.30" />

`[ReadModel]` gives you a model *folded* from a stream's events. Sometimes folding is exactly the wrong
thing: a timeline endpoint, an audit view, or a "what happened to this order?" screen needs the history
itself, and the fold has already collapsed it. `[StreamState]` and `[StreamEvents]` are the raw reads for
those handlers.

`[StreamState]` resolves the stream's metadata — version, aggregate type, created and updated timestamps —
and `[StreamEvents]` resolves its events as `IReadOnlyList<IEvent>`. Neither one folds anything. Both are
store-agnostic in the same way the rest of this vocabulary is, and the store batches them with any other
batchable load on the same handler into a single round trip:

<!-- snippet: sample_using_stream_state_and_events -->
<a id='snippet-sample_using_stream_state_and_events'></a>
```cs
public static class OrderTimelineHandler
{
    // [StreamState] gives you the stream's metadata -- version, aggregate type, created/updated
    // timestamps -- and [StreamEvents] gives you the raw events, WITHOUT folding either into an
    // aggregate. This is the read [ReadModel] cannot express, because folding has already thrown
    // away the history this handler exists to serve. Both fetches batch into one round trip.
    public static OrderTimeline Handle(
        OrderTimelineQuery query,
        [StreamState] StreamState state,
        [StreamEvents] IReadOnlyList<IEvent> events)
    {
        return new OrderTimeline(state.Version, events.Select(x => x.EventTypeName).ToArray());
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L130-L147' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_stream_state_and_events' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Like `[ReadModel]`, `[StreamState]` takes its required-ness from the parameter's nullable annotation:
`StreamState state` stops the handler when the stream does not exist, and `StreamState? state` leaves
absence to you.

<!-- snippet: sample_stream_state_optional -->
<a id='snippet-sample_stream_state_optional'></a>
```cs
public static class OptionalOrderTimelineHandler
{
    // Nullable annotation decides the default, exactly as it does for [ReadModel]:
    // "StreamState state" is required and stops the handler when the stream does not exist,
    // "StreamState? state" leaves absence to you
    public static OrderTimeline Handle(OrderTimelineQuery query, [StreamState] StreamState? state)
    {
        return new OrderTimeline(state?.Version ?? 0, []);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L168-L181' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stream_state_optional' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning The identity convention is not the one `[Entity]` uses
This is the easiest thing here to get wrong. `[Entity] Order order` can infer an identity member named
`OrderId`, because the parameter's own type names your entity. The parameter type here is `StreamState` —
the *store's* vocabulary, not your aggregate — so there is no aggregate name to infer from, and the
convention degrades to a member literally named `Id`.

For anything else, name the member explicitly:

<!-- snippet: sample_stream_state_with_named_identity -->
<a id='snippet-sample_stream_state_with_named_identity'></a>
```cs
public static class OrderAuditHandler
{
    // The identity convention here is NOT the one [Entity] and [ReadModel] use. Those infer
    // "OrderId" from the parameter's own type; the parameter type here is StreamState, which
    // names the store's vocabulary rather than your aggregate. So a bare [StreamState] resolves
    // only a member literally named "Id" -- name the member explicitly for anything else.
    public static OrderTimeline Handle(
        OrderAuditQuery query,
        [StreamState("OrderId")] StreamState state,
        [StreamEvents("OrderId")] IReadOnlyList<IEvent> events)
    {
        return new OrderTimeline(state.Version, events.Select(x => x.EventTypeName).ToArray());
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L149-L166' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stream_state_with_named_identity' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A miss is not silent. Wolverine throws `InvalidEntityLoadUsageException` when the chain is compiled — at
startup, not at the first request.
:::

#### There is deliberately no `Required` on `[StreamEvents]`

`[StreamState]` has a `Required` property; `[StreamEvents]` does not, and that asymmetry is intentional
rather than an oversight. A missing stream yields an **empty list**, not null, so the null-guard model the
rest of the `[Entity]` family is built on has nothing to test — a guard would either never fire, or would
have to invent a count threshold. And "zero events" is a genuinely different question from "no such
stream."

When a handler needs an existence guard, pair the two. `[StreamState]` reads the same stream in the same
batch and answers the question precisely:

```csharp
public static OrderTimeline Handle(
    OrderTimelineQuery query,
    [StreamState] StreamState state,           // non-nullable, so this is the not-found guard
    [StreamEvents] IReadOnlyList<IEvent> events)
```

#### Choosing a store

Because these parameter types are store vocabulary rather than your own types, Wolverine cannot ask "who
owns this?" the way it can for `[ReadModel] Order`. Resolution goes, in order:

1. An explicit `AggregateType` — `[StreamState(AggregateType = typeof(Order))]` — which identifies the
   owning store and nothing else. It does not change what is read.
2. The ancillary store the chain was routed to by `[Storage(typeof(IMyStore))]`.
3. The single registered event store integration, when there is only one.

With more than one integration registered and no other signal, Wolverine throws at compile time with a
message naming both escape hatches rather than guessing.

There are also Marten-specific spellings, `[MartenStreamState]` and `[MartenStreamEvents]`, which exist
for the same reason [`[ReadAggregate]`](/guide/durability/marten/event-sourcing) does: they **name** their
store instead of resolving one, so they still work in a host that called `AddMarten(...)` without
`IntegrateWithWolverine()`, where nothing ever registers a persistence strategy. Prefer the store-agnostic
`[StreamState]` and `[StreamEvents]` in new code.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EventSourcedModelSamples.cs#L98-L122' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_dcb_model_attribute' title='Start of snippet'>anchor</a></sup>
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

On `[WriteModel]` <Badge type="tip" text="6.27" /> and `[ReadModel]` <Badge type="tip" text="6.27.1" />,
`Required` **defaults to the opposite of the parameter's nullable annotation** — `Order order` is
required and gets a not-found guard, `Order? order` is not and is handed to your method as `null` so
your own null branch runs. Setting `Required` explicitly overrides the annotation either way.

::: warning The store-specific spellings keep the old default
`[WriteAggregate]`, `[ReadAggregate]` and `[Entity]` **do not** infer from the annotation. They all
predate it, and quietly dropping a not-found guard from existing code is a change that only shows up
at runtime — so they keep their unconditional `Required = true`. Say `Required = false` explicitly on
those, or move to `[WriteModel]` / `[ReadModel]`.

In a project compiled with `<Nullable>disable</Nullable>` the annotation cannot be read, so
`[WriteModel]` and `[ReadModel]` also fall back to `Required = true`.
:::

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

::: tip
`EntityDefaults.OnMissing` reaches every attribute that loads data this way — `[Entity]`, `[Document]`,
`[Aggregate]`, `[ReadAggregate]`, `[WriteAggregate]`, `[ReadModel]`, `[WriteModel]`, and the DCB
attributes. Setting it to `OnMissing.EmptyContentWith204` changes the answer for *all* of them, including
aggregate endpoints you may not have been thinking about. Set it on the individual attributes instead if
you only meant a subset.
:::

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
