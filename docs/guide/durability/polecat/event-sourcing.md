# Aggregate Handlers and Event Sourcing

::: tip
Only use the "aggregate handler workflow" if you are wanting to potentially write new events to an existing event stream. If all you
need in a message handler or HTTP endpoint is a read-only copy of an event streamed aggregate from Polecat, use the `[ReadAggregate]` attribute
instead that has a little bit lighter weight runtime within Polecat.
:::

The Wolverine + Polecat combination is optimized for efficient and productive development using a [CQRS architecture style](https://martinfowler.com/bliki/CQRS.html) with Polecat's event sourcing support.
Specifically, let's dive into the responsibilities of a typical command handler in a CQRS with event sourcing architecture:

1. Fetch any current state of the system that's necessary to evaluate or validate the incoming event
2. *Decide* what events should be emitted and captured in response to an incoming event
3. Manage concurrent access to system state
4. Safely commit the new events
5. Selectively publish some of the events based on system needs to other parts of your system or even external systems
6. Instrument all of the above

And then lastly, you're going to want some resiliency and selective retry capabilities for concurrent access violations or just normal infrastructure hiccups.

Let's jump right into an example order management system. I'm going to model the order workflow with this aggregate model:

```cs
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

    // This is important, by convention this would
    // be the version
    public int Version { get; set; }

    public DateTimeOffset? Shipped { get; private set; }

    public Dictionary<string, Item> Items { get; set; } = new();

    // These methods are used by Polecat to update the aggregate
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

At a minimum, we're going to want a command handler for this command message that marks an order item as ready to ship:

```cs
// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
```

Wolverine supports the [Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider)
pattern with Polecat using the `[AggregateHandler]` middleware.
Using that middleware, we get this slim code:

```cs
[AggregateHandler]
public static IEnumerable<object> Handle(MarkItemReady command, Order order)
{
    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;

        // Mark that the this item is ready
        yield return new ItemReady(command.ItemName);
    }
    else
    {
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    // If the order is ready to ship, also emit an OrderReady event
    if (order.IsReadyToShip())
    {
        yield return new OrderReady();
    }
}
```

In the case above, Wolverine is wrapping middleware around our basic command handler to:

1. Fetch the appropriate `Order` aggregate matching the command
2. Append any new events returned from the handle method to the Polecat event stream for this `Order`
3. Saves any outstanding changes and commits the Polecat unit of work

::: warning
There are some open imperfections with Wolverine's code generation against the `[WriteAggregate]` and `[ReadAggregate]`
usage. For best results, only use these attributes on a parameter within the main HTTP endpoint method and not in `Validate/Before/Load` methods.
:::

::: info
The `[Aggregate]` and `[WriteAggregate]` attributes _require the requested stream and aggregate to be found by default_, meaning that the handler or HTTP
endpoint will be stopped if the requested data is not found. You can explicitly mark individual attributes as `Required=false`.
:::

Alternatively, there is also the newer `[WriteAggregate]` usage:

```cs
public static IEnumerable<object> Handle(
    MarkItemReady command,
    [WriteAggregate] Order order)
{
    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;
        yield return new ItemReady(command.ItemName);
    }
    else
    {
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    if (order.IsReadyToShip())
    {
        yield return new OrderReady();
    }
}
```

The `[WriteAggregate]` attribute also opts into the "aggregate handler workflow", but is placed at the parameter level
instead of the class level. This was added to extend the "aggregate handler workflow" to operations that involve multiple
event streams in one transaction.

::: tip
`[WriteAggregate]` works equally on message handlers as it does on HTTP endpoints.
:::

## Validation on Stream Existence

By default, the "aggregate handler workflow" does no validation on whether or not the identified event stream actually
exists at runtime. You can protect against missing streams:

```cs
public static class ValidatedMarkItemReadyHandler
{
    public static IEnumerable<object> Handle(
        MarkItemReady command,

        // In HTTP this will return a 404 status code and stop
        // In message handlers, this will log and discard the message
        [WriteAggregate(Required = true)] Order order) => [];

    [WolverineHandler]
    public static IEnumerable<object> Handle2(
        MarkItemReady command,
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith400)] Order order) => [];

    [WolverineHandler]
    public static IEnumerable<object> Handle3(
        MarkItemReady command,
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith404)] Order order) => [];

    [WolverineHandler]
    public static IEnumerable<object> Handle4(
        MarkItemReady command,
        [WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith404, MissingMessage = "Cannot find Order {0}")] Order order) => [];
}
```

### Handler Method Signatures

The aggregate workflow command handler method signature needs to follow these rules:

* Either explicitly use the `[AggregateHandler]` attribute on the handler method **or use the `AggregateHandler` suffix** on the message handler type
* The first argument should be the command type
* The 2nd argument should be the aggregate -- either the aggregate itself (`Order`) or wrapped in the `IEventStream<T>` type (`IEventStream<Order>`):

```cs
[AggregateHandler]
public static void Handle(MarkItemReady command, IEventStream<Order> stream)
{
    var order = stream.Aggregate;

    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;
        stream.AppendOne(new ItemReady(command.ItemName));
    }
    else
    {
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    if (order.IsReadyToShip())
    {
        stream.AppendOne(new OrderReady());
    }
}
```

As for the return values from these handler methods, you can use:

* It's legal to have **no** return values if you are directly using `IEventStream<T>` to append events
* `IEnumerable<object>` or `object[]` to denote events to append to the current event stream
* `IAsyncEnumerable<object>` will also be treated as events to append
* `Events` to denote a list of events
* `OutgoingMessages` to refer to additional command messages to be published that should *not* be captured as events
* `ISideEffect` objects
* Any other type would be considered to be a separate event type

Here's an alternative using `Events`:

```cs
[AggregateHandler]
public static async Task<(Events, OutgoingMessages)> HandleAsync(MarkItemReady command, Order order, ISomeService service)
{
    var data = await service.FindDataAsync();

    var messages = new OutgoingMessages();
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
        messages.Add(new ShipOrder(order.Id));
    }

    return (events, messages);
}
```

### Determining the Aggregate Identity

Wolverine is trying to determine a public member on the command type that refers to the identity
of the aggregate type. You've got two options, either use the implied naming convention
where the `OrderId` property is assumed to be the identity of the `Order` aggregate:

```cs
// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
```

Or decorate a public member on the command class with the `[Identity]` attribute:

```cs
public class MarkItemReady
{
    [Identity] public Guid Id { get; init; }
    public string ItemName { get; init; }
}
```

## Forwarding Events

See [Event Forwarding](./event-forwarding) for more information.

## Returning the Updated Aggregate

A common use case has been to respond with the now updated state of the projected
aggregate that has just been updated by appending new events.

Wolverine.Polecat has a special response type for message handlers or HTTP endpoints we can use as a directive to tell Wolverine
to respond with the latest state of a projected aggregate as part of the command execution:

```cs
[AggregateHandler]
public static (UpdatedAggregate, Events) Handle(MarkItemReady command, Order order)
{
    var events = new Events();

    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;
        events.Add(new ItemReady(command.ItemName));
    }
    else
    {
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    if (order.IsReadyToShip())
    {
        events.Add(new OrderReady());
    }

    return (new UpdatedAggregate(), events);
}
```

The `UpdatedAggregate` type is just a directive to Wolverine to generate the necessary code to call `FetchLatest` and respond with that:

```cs
public static Task<Order> update_and_get_latest(IMessageBus bus, MarkItemReady command)
{
    return bus.InvokeAsync<Order>(command);
}
```

You can also use `UpdatedAggregate` as the response body of an HTTP endpoint with Wolverine.HTTP [as shown here](/guide/http/polecat#responding-with-the-updated-aggregate).

### Passing the Aggregate to Before/Validate/Load Methods

The "[compound handler](/guide/handlers/#compound-handlers)" feature is fully supported within the aggregate handler workflow. You can pass the aggregate type as an argument to any `Before` / `LoadAsync` / `Validate` method:

```cs
public record RaiseIfValidated(Guid LetterAggregateId);

public static class RaiseIfValidatedHandler
{
    public static HandlerContinuation Validate(LetterAggregate aggregate) =>
        aggregate.ACount == 0 ? HandlerContinuation.Continue : HandlerContinuation.Stop;

    [AggregateHandler]
    public static IEnumerable<object> Handle(RaiseIfValidated command, LetterAggregate aggregate)
    {
        yield return new BEvent();
    }
}
```

## Reading the Latest Version of an Aggregate

If you want to inject the current state of an event sourced aggregate as a parameter into
a message handler method strictly for information and don't need the heavier "aggregate handler workflow," use the `[ReadAggregate]` attribute:

```cs
public record FindAggregate(Guid Id);

public static class FindLettersHandler
{
    public static LetterAggregateEnvelope Handle(FindAggregate command, [ReadAggregate] LetterAggregate aggregate)
    {
        return new LetterAggregateEnvelope(aggregate);
    }
}
```

If the aggregate doesn't exist, the HTTP request will stop with a 404 status code.
The aggregate/stream identity is found with these rules:

1. You can specify a particular request body property name or route argument
2. Look for a request body property or route argument named "EntityTypeId"
3. Look for a request body property or route argument named "Id" or "id"

## Targeting Multiple Streams at Once

It's possible to use the "aggregate handler workflow" while needing to append events to more than one event stream at a time.

::: tip
You can use read only views of event streams through `[ReadAggregate]` at will, and that will use
Polecat's `FetchLatest()` API underneath. For appending to multiple streams, use `IEventStream<T>` directly.
:::

```cs
public record TransferMoney(Guid FromId, Guid ToId, double Amount);

public static class TransferMoneyHandler
{
    [WolverinePost("/accounts/transfer")]
    public static void Handle(
        TransferMoney command,

        [WriteAggregate(nameof(TransferMoney.FromId))] IEventStream<Account> fromAccount,

        [WriteAggregate(nameof(TransferMoney.ToId))] IEventStream<Account> toAccount)
    {
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}
```

### Finer-Grained Optimistic Concurrency in Multi-Stream Operations

When a handler uses multiple `[WriteAggregate]` parameters, Wolverine automatically applies version discovery only
to the **first** aggregate parameter. To opt a secondary stream into optimistic concurrency checking, use `VersionSource`:

```cs
public record TransferMoney(Guid FromId, Guid ToId, decimal Amount,
    long FromVersion, long ToVersion);

public static class TransferMoneyHandler
{
    public static void Handle(
        TransferMoney command,

        [WriteAggregate(nameof(TransferMoney.FromId),
            VersionSource = nameof(TransferMoney.FromVersion))]
        IEventStream<Account> fromAccount,

        [WriteAggregate(nameof(TransferMoney.ToId),
            VersionSource = nameof(TransferMoney.ToVersion))]
        IEventStream<Account> toAccount)
    {
        if (fromAccount.Aggregate.Amount >= command.Amount)
        {
            fromAccount.AppendOne(new Withdrawn(command.Amount));
            toAccount.AppendOne(new Debited(command.Amount));
        }
    }
}
```

## Enforcing Consistency Without New Events

The `AlwaysEnforceConsistency` option tells Polecat to perform an optimistic concurrency check on the stream even if no events
are appended:

```cs
[AggregateHandler(AlwaysEnforceConsistency = true)]
public static class MyAggregateHandler
{
    public static void Handle(DoSomething command, IEventStream<MyAggregate> stream)
    {
        // Even if no events are appended, Polecat will verify
        // the stream version hasn't changed since it was fetched
    }
}
```

For convenience, there is a `[ConsistentAggregateHandler]` attribute that automatically sets `AlwaysEnforceConsistency = true`.

### Parameter-level usage with `[ConsistentAggregate]`

```cs
public static class MyHandler
{
    public static void Handle(DoSomething command,
        [ConsistentAggregate] IEventStream<MyAggregate> stream)
    {
        // AlwaysEnforceConsistency is automatically true
    }
}
```

## Overriding Version Discovery

By default, Wolverine discovers a version member on your command type by looking for a property or field named `Version`
of type `int` or `long`. The `VersionSource` property lets you explicitly specify which member supplies the expected stream version:

```cs
public record TransferMoney(Guid FromId, Guid ToId, decimal Amount, long FromVersion);

[AggregateHandler(VersionSource = nameof(TransferMoney.FromVersion))]
public static class TransferMoneyHandler
{
    public static IEnumerable<object> Handle(TransferMoney command, Account account)
    {
        yield return new Withdrawn(command.Amount);
    }
}
```

For HTTP endpoints, `VersionSource` can resolve from route arguments, query string parameters, or request body members:

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

## Strong Typed Identifiers

You can use strong typed identifiers from tools like [Vogen](https://github.com/SteveDunn/Vogen) and [StronglyTypedId](https://github.com/andrewlock/StronglyTypedId)
within the "Aggregate Handler Workflow." You can also use hand rolled value types that wrap either `Guid` or `string`
as long as they conform to Polecat's rules about value type identifiers.

```cs
public record IncrementStrongA(LetterId Id);

public static class StrongLetterHandler
{
    public static AEvent Handle(IncrementStrongA command, [WriteAggregate] StrongLetterAggregate aggregate)
    {
        return new();
    }
}
```

## Natural Keys

Polecat supports [natural keys](/events/natural-keys) on aggregates, allowing you to look up event streams by a domain-meaningful identifier (like an order number) instead of the internal stream id. Wolverine's aggregate handler workflow fully supports natural keys, letting you route commands to the correct aggregate using a business identifier.

### Defining the Aggregate with a Natural Key

First, define your aggregate with a `[NaturalKey]` property and mark the methods that set the key with `[NaturalKeySource]`:

<!-- snippet: sample_wolverine_polecat_natural_key_aggregate -->
<a id='snippet-sample_wolverine_polecat_natural_key_aggregate'></a>
```cs
public record PcNkOrderNumber(string Value);

public class PcNkOrderAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public PcNkOrderNumber OrderNum { get; set; } = null!;

    public decimal TotalAmount { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public bool IsComplete { get; set; }

    [NaturalKeySource]
    public void Apply(PcNkOrderCreated e)
    {
        OrderNum = e.OrderNumber;
        CustomerName = e.CustomerName;
    }

    public void Apply(PcNkItemAdded e)
    {
        TotalAmount += e.Price;
    }

    public void Apply(PcNkOrderCompleted e)
    {
        IsComplete = true;
    }
}

public record PcNkOrderCreated(PcNkOrderNumber OrderNumber, string CustomerName);
public record PcNkItemAdded(string ItemName, decimal Price);
public record PcNkOrderCompleted;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PolecatTests/natural_key_aggregate_handler_workflow.cs#L123-L160' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_polecat_natural_key_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Using Natural Keys in Command Handlers

When your command carries the natural key value instead of a stream id, Wolverine can resolve it automatically. The command property should match the aggregate's natural key type:

<!-- snippet: sample_wolverine_polecat_natural_key_commands -->
<a id='snippet-sample_wolverine_polecat_natural_key_commands'></a>
```cs
public record AddPcNkOrderItem(PcNkOrderNumber OrderNum, string ItemName, decimal Price);
public record AddPcNkOrderItems(PcNkOrderNumber OrderNum, (string Name, decimal Price)[] Items);
public record CompletePcNkOrder(PcNkOrderNumber OrderNum);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PolecatTests/natural_key_aggregate_handler_workflow.cs#L162-L168' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_polecat_natural_key_commands' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine uses the natural key type on the command property to call `FetchForWriting<TAggregate, TNaturalKey>()` under the covers, resolving the stream by the natural key in a single database round-trip.

### Handler Examples

Here are the handlers that process those commands, using `[WriteAggregate]` and `IEventStream<T>`:

<!-- snippet: sample_wolverine_polecat_natural_key_handlers -->
<a id='snippet-sample_wolverine_polecat_natural_key_handlers'></a>
```cs
public static class PcNkOrderHandler
{
    public static PcNkItemAdded Handle(AddPcNkOrderItem command,
        [WriteAggregate] PcNkOrderAggregate aggregate)
    {
        return new PcNkItemAdded(command.ItemName, command.Price);
    }

    public static IEnumerable<object> Handle(AddPcNkOrderItems command,
        [WriteAggregate] PcNkOrderAggregate aggregate)
    {
        foreach (var (name, price) in command.Items)
        {
            yield return new PcNkItemAdded(name, price);
        }
    }

    public static void Handle(CompletePcNkOrder command,
        [WriteAggregate] IEventStream<PcNkOrderAggregate> stream)
    {
        stream.AppendOne(new PcNkOrderCompleted());
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PolecatTests/natural_key_aggregate_handler_workflow.cs#L170-L196' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_polecat_natural_key_handlers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For more details on how natural keys work at the Polecat level, see the [Polecat natural keys documentation](/events/natural-keys).

## Dynamic Consistency Boundary (DCB)

::: tip
The [Dynamic Consistency Boundary](https://dcb.events/) pattern enables event-sourced handlers to work across **multiple event streams simultaneously** within a single consistency boundary. This is essential for domain logic that naturally spans multiple entities.
:::

Traditional aggregate handlers work with a single event stream at a time. But some business decisions require state from multiple streams — for example, subscribing a student to a course requires checking both the student's enrollment history and the course's capacity. The DCB pattern solves this by loading events from multiple streams based on **event tags**, projecting them into a single aggregate state, and appending new events atomically.

### How It Works

1. A `Load()` or `Before()` method returns an `EventTagQuery` that specifies which tagged events to load
2. Polecat loads all matching events and projects them into your aggregate type using the standard `Apply()` methods
3. Your handler receives the projected state and makes decisions
4. Returned events are appended atomically through the `IEventBoundary<T>` interface

### Example: University Course Subscription

This example is ported from the [AxonIQ DCB demo](https://dcb.events/). A student subscribing to a course must enforce rules spanning both the student and course boundaries:

- Student must be enrolled in faculty
- Student can't subscribe to more than 3 courses
- Course must exist and have vacant spots
- Student not already subscribed

First, define your domain events and strong-typed IDs:

<!-- snippet: sample_wolverine_dcb_university_ids -->
<a id='snippet-sample_wolverine_dcb_university_ids'></a>
```cs
namespace MartenTests.Dcb.University;

/// <summary>
/// Strong-typed ID for a course. Uses string value with "Course:" prefix.
/// </summary>
public record CourseId(string Value)
{
    public static CourseId Random() => new($"Course:{Guid.NewGuid()}");
    public static CourseId Of(string raw) => new(raw.StartsWith("Course:") ? raw : $"Course:{raw}");
    public override string ToString() => Value;
}

/// <summary>
/// Strong-typed ID for a student. Uses string value with "Student:" prefix.
/// </summary>
public record StudentId(string Value)
{
    public static StudentId Random() => new($"Student:{Guid.NewGuid()}");
    public static StudentId Of(string raw) => new(raw.StartsWith("Student:") ? raw : $"Student:{raw}");
    public override string ToString() => Value;
}

/// <summary>
/// Strong-typed ID for the faculty. Single-instance in this demo.
/// </summary>
public record FacultyId(string Value)
{
    public static readonly FacultyId Default = new("Faculty:ONLY_FACULTY_ID");
    public static FacultyId Of(string raw) => new(raw.StartsWith("Faculty:") ? raw : $"Faculty:{raw}");
    public override string ToString() => Value;
}

/// <summary>
/// Composite ID for a student-course subscription.
/// </summary>
public record SubscriptionId(CourseId CourseId, StudentId StudentId);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Dcb/University/UniversityIds.cs#L1-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_dcb_university_ids' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_wolverine_dcb_university_events -->
<a id='snippet-sample_wolverine_dcb_university_events'></a>
```cs
namespace MartenTests.Dcb.University;

public record CourseCreated(FacultyId FacultyId, CourseId CourseId, string Name, int Capacity);

public record CourseRenamed(FacultyId FacultyId, CourseId CourseId, string Name);

public record CourseCapacityChanged(FacultyId FacultyId, CourseId CourseId, int Capacity);

public record StudentEnrolledInFaculty(FacultyId FacultyId, StudentId StudentId, string FirstName, string LastName);

public record StudentSubscribedToCourse(FacultyId FacultyId, StudentId StudentId, CourseId CourseId);

public record StudentUnsubscribedFromCourse(FacultyId FacultyId, StudentId StudentId, CourseId CourseId);

public record AllCoursesFullyBookedNotificationSent(FacultyId FacultyId);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Dcb/University/UniversityEvents.cs#L1-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_dcb_university_events' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Next, define the aggregate state that spans both boundaries. This single type projects events tagged with either a `CourseId` or `StudentId`:

<!-- snippet: sample_wolverine_dcb_subscription_state -->
<a id='snippet-sample_wolverine_dcb_subscription_state'></a>
```cs
namespace MartenTests.Dcb.University;
/// Built from events tagged with BOTH CourseId and StudentId.
/// This is the core DCB pattern — the consistency boundary spans multiple streams.
///
/// Ported from the Axon SubscribeStudentToCourseCommandHandler.State which uses
/// EventCriteria.either() to load events matching CourseId OR StudentId.
/// </summary>
public class SubscriptionState
{
    public CourseId? CourseId { get; private set; }
    public int CourseCapacity { get; private set; }
    public int StudentsSubscribedToCourse { get; private set; }

    public StudentId? StudentId { get; private set; }
    public int CoursesStudentSubscribed { get; private set; }
    public bool AlreadySubscribed { get; private set; }

    public void Apply(CourseCreated e)
    {
        CourseId = e.CourseId;
        CourseCapacity = e.Capacity;
    }

    public void Apply(CourseCapacityChanged e)
    {
        CourseCapacity = e.Capacity;
    }

    public void Apply(StudentEnrolledInFaculty e)
    {
        StudentId = e.StudentId;
    }

    public void Apply(StudentSubscribedToCourse e)
    {
        if (e.CourseId == CourseId)
            StudentsSubscribedToCourse++;
        if (e.StudentId == StudentId)
            CoursesStudentSubscribed++;
        if (e.StudentId == StudentId && e.CourseId == CourseId)
            AlreadySubscribed = true;
    }

    public void Apply(StudentUnsubscribedFromCourse e)
    {
        if (e.CourseId == CourseId)
            StudentsSubscribedToCourse--;
        if (e.StudentId == StudentId)
            CoursesStudentSubscribed--;
        if (e.StudentId == StudentId && e.CourseId == CourseId)
            AlreadySubscribed = false;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Dcb/University/SubscriptionState.cs#L1-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_dcb_subscription_state' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Using the `[BoundaryModel]` Attribute

The `[BoundaryModel]` attribute on a handler parameter triggers the DCB workflow. Your handler class must include a `Load()` (or `LoadAsync()`, `Before()`, `BeforeAsync()`) method that returns an `EventTagQuery`:

<!-- snippet: sample_wolverine_dcb_boundary_model_handler -->
<a id='snippet-sample_wolverine_dcb_boundary_model_handler'></a>
```cs
public static class BoundaryModelSubscribeStudentHandler
{
    public const int MaxCoursesPerStudent = 3;

    public static EventTagQuery Load(BoundaryModelSubscribeStudentToCourse command)
        => EventTagQuery
            .For(command.CourseId)
            .AndEventsOfType<CourseCreated, CourseCapacityChanged, StudentSubscribedToCourse, StudentUnsubscribedFromCourse>()
            .Or(command.StudentId)
            .AndEventsOfType<StudentEnrolledInFaculty, StudentSubscribedToCourse, StudentUnsubscribedFromCourse>();

    public static StudentSubscribedToCourse Handle(
        BoundaryModelSubscribeStudentToCourse command,
        [BoundaryModel]
        SubscriptionState state)
    {
        if (state.StudentId == null)
            throw new InvalidOperationException("Student with given id never enrolled the faculty");

        if (state.CoursesStudentSubscribed >= MaxCoursesPerStudent)
            throw new InvalidOperationException("Student subscribed to too many courses");

        if (state.CourseId == null)
            throw new InvalidOperationException("Course with given id does not exist");

        if (state.StudentsSubscribedToCourse >= state.CourseCapacity)
            throw new InvalidOperationException("Course is fully booked");

        if (state.AlreadySubscribed)
            throw new InvalidOperationException("Student already subscribed to this course");

        return new StudentSubscribedToCourse(FacultyId.Default, command.StudentId, command.CourseId);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Dcb/University/BoundaryModelSubscribeStudentToCourse.cs#L8-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_dcb_boundary_model_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `EventTagQuery` uses a fluent API to define which events to load:

- `EventTagQuery.For(tag)` — start with a tag value (e.g., a `CourseId`)
- `.AndEventsOfType<T1, T2, ...>()` — filter to specific event types for that tag
- `.Or(tag)` — add another tag to query (e.g., a `StudentId`)

Polecat loads all events matching **any** of the tag criteria, projects them into your aggregate using the standard `Apply()` methods, and provides the result to your handler.

### Using `IEventBoundary<T>` Directly

For more control over event appending, you can accept `IEventBoundary<T>` as a parameter instead of the aggregate type:

```cs
public static void Handle(
    SubscribeStudentToCourse command,
    [BoundaryModel] IEventBoundary<SubscriptionState> boundary)
{
    var state = boundary.Aggregate;

    // validation logic...

    boundary.AppendOne(new StudentSubscribedToCourse(
        FacultyId.Default, command.StudentId, command.CourseId));
}
```

### Return Value Handling

The DCB workflow supports the same return value patterns as the standard aggregate handler workflow:

- Single event objects are appended via `boundary.AppendOne()`
- `IEnumerable<object>` or `Events` collections are appended via `boundary.AppendMany()`
- `IAsyncEnumerable<object>` events are appended one at a time
- `OutgoingMessages` and `ISideEffect` are handled as cascading messages, not events

### Validation on Boundary Existence

Use the `Required` property to enforce that the projected aggregate state is not null:

```cs
public static StudentSubscribedToCourse Handle(
    SubscribeStudentToCourse command,
    [BoundaryModel(Required = true)] SubscriptionState state)
{
    // state is guaranteed to be non-null
    // ...
}
```
