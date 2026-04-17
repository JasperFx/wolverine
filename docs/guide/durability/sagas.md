# Sagas

::: tip
For practical examples of building sagas with Wolverine, see the blog posts
[Low Ceremony Sagas with Wolverine](https://jeremydmiller.com/2024/08/20/low-ceremony-sagas-with-wolverine/) and
[Multi Step Workflows with the Critter Stack](https://jeremydmiller.com/2024/10/01/multi-step-workflows-with-the-critter-stack/).
:::

::: tip
To be honest, we're just not going to get hung up on "process manager" vs. "saga" here. The key point is that what
Wolverine is calling a "saga" really just means a long running, multi-step process where you need to track some state
between the steps. If that annoys Greg Young, then ¯\_(ツ)_/¯.
:::

As is so common in these docs, I would direct you to this from the old "EIP" book: [Process Manager](http://www.enterpriseintegrationpatterns.com/patterns/messaging/ProcessManager.html). A stateful saga in Wolverine is used
to coordinate long running workflows or to break large, logical transactions into a series of smaller steps. A stateful saga
in Wolverine consists of a couple parts:

1. A saga state document type that is persisted between saga messages that must inherit from the `Wolverine.Saga` type. This will also be your handler type for all messages
   that directly impact the saga
2. Messages that would update the saga state when handled
3. A saga persistence strategy registered in Wolverine that knows how to load and persist the saga state documents
4. An identity for the saga state in order to save, load, or delete the current saga state

## Your First Saga

*See the [OrderSagaSample](https://github.com/JasperFx/wolverine/tree/main/src/Samples/OrderSagaSample) project in GitHub for all the
sample code in this section.*

Jumping right into an example, consider a very simple order management service that will have steps to:

* Create a new order
* Complete the order
* Or alternatively, delete new orders if they have not been completed within 1 minute

For the moment, I’m going to ignore the underlying persistence and just focus on the Wolverine message handlers to implement the order saga workflow with this simplistic saga code:

<!-- snippet: sample_order_saga -->
<a id='snippet-sample_order_saga'></a>
```cs
public record StartOrder(string OrderId);

public record CompleteOrder(string Id);

// This message will always be scheduled to be delivered after
// a one minute delay
public record OrderTimeout(string Id) : TimeoutMessage(1.Minutes());

public class Order : Saga
{
    public string? Id { get; set; }

    // This method would be called when a StartOrder message arrives
    // to start a new Order
    public static (Order, OrderTimeout) Start(StartOrder order, ILogger<Order> logger)
    {
        logger.LogInformation("Got a new order with id {Id}", order.OrderId);

        // creating a timeout message for the saga
        return (new Order{Id = order.OrderId}, new OrderTimeout(order.OrderId));
    }

    // Apply the CompleteOrder to the saga
    public void Handle(CompleteOrder complete, ILogger<Order> logger)
    {
        logger.LogInformation("Completing order {Id}", complete.Id);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }

    // Delete this order if it has not already been deleted to enforce a "timeout"
    // condition
    public void Handle(OrderTimeout timeout, ILogger<Order> logger)
    {
        logger.LogInformation("Applying timeout to order {Id}", timeout.Id);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }

    public static void NotFound(CompleteOrder complete, ILogger<Order> logger)
    {
        logger.LogInformation("Tried to complete order {Id}, but it cannot be found", complete.Id);
    }

}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/OrderSaga.cs#L6-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_saga' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A few explanatory notes on this code before we move on to detailed documentation:

* Wolverine leans a bit on type and naming conventions to discover message handlers and to “know” how to call these message handlers. Some folks will definitely not like the magic, but this approach leads to substantially less code and arguably complexity compared to existing .Net tools
* Wolverine supports the idea of [scheduled messages](/guide/messaging/message-bus.html#scheduling-message-delivery-or-execution), and the new `TimeoutMessage` base class we used up there is just a shorthand way to utilize that support for “saga timeout” conditions
* Wolverine generally tries to adapt to your application code rather that using mandatory adapter interfaces
* Subclassing `Saga` is meaningful first as this tells Wolverine "hey, this stateful type should be treated as a saga" for [handler discovery](/guide/handlers/discovery), but also for communicating
  to Wolverine that a logical saga is complete and should be deleted

Now, to add saga persistence, I'm going to lean on the [Marten integration](/guide/durability/marten) with Wolverine and use this bootstrapping for our little order web service:

<!-- snippet: sample_bootstrapping_order_saga_sample -->
<a id='snippet-sample_bootstrapping_order_saga_sample'></a>
```cs
using Marten;
using JasperFx;
using JasperFx.Resources;
using OrderSagaSample;
using Wolverine;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// Not 100% necessary, but enables some extra command line diagnostics
builder.Host.ApplyJasperFxExtensions();

// Adding Marten
builder.Services.AddMarten(opts =>
    {
        var connectionString = builder.Configuration.GetConnectionString("Marten");
        opts.Connection(connectionString!);
        opts.DatabaseSchemaName = "orders";
    })

    // Adding the Wolverine integration for Marten.
    .IntegrateWithWolverine();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Do all necessary database setup on startup
builder.Services.AddResourceSetupOnStartup();

// The defaults are good enough here
builder.Host.UseWolverine();

var app = builder.Build();

// Just delegating to Wolverine's local command bus for all
app.MapPost("/start", (StartOrder start, IMessageBus bus) => bus.InvokeAsync(start));
app.MapPost("/complete", (CompleteOrder complete, IMessageBus bus) => bus.InvokeAsync(complete));
app.MapGet("/all", (IQuerySession session) => session.Query<Order>().ToListAsync());
app.MapGet("/", (HttpResponse response) =>
{
    response.Headers.Add("Location", "/swagger");
    response.StatusCode = 301;
}).ExcludeFromDescription();

app.UseSwagger();
app.UseSwaggerUI();

return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/Program.cs#L1-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_order_saga_sample' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The call to `IServiceCollection.AddMarten().IntegrateWithWolverine()` adds the Marten backed saga persistence to your application. No other configuration
is necessary. See the [Marten integration](/guide/durability/marten.html#saga-storage) for a little more information about using Marten backed sagas.

## How it works

::: warning
Do not call `IMessageBus.InvokeAsync()` within a `Saga` related handler to execute a command on that same `Saga`. You will be acting
on old or missing data. Utilize cascading messages for subsequent work. 
:::

Wolverine is wrapping some generated code around your `Saga.Start()` and `Saga.Handle()` methods for loading and persisting the state. Here's a (mildly cleaned up) version
of the generated code for starting the `Order` saga shown above:

```cs
public class StartOrderHandler133227374 : MessageHandler
{
    private readonly OutboxedSessionFactory _outboxedSessionFactory;
    private readonly ILogger<Order> _logger;

    public StartOrderHandler133227374(OutboxedSessionFactory outboxedSessionFactory, ILogger<Order> logger)
    {
        _outboxedSessionFactory = outboxedSessionFactory;
        _logger = logger;
    }

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var startOrder = (StartOrder)context.Envelope.Message;
        await using var documentSession = _outboxedSessionFactory.OpenSession(context);
        (var outgoing1, var outgoing2) = Order.Start(startOrder, _logger);
        
        // Register the document operation with the current session
        documentSession.Insert(outgoing1);
        
        // Outgoing, cascaded message
        await context.EnqueueCascadingAsync(outgoing2).ConfigureAwait(false);
        
        // Commit the unit of work
        await documentSession.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }
}
```

And here's the code that's generated for the `CompleteOrder` command from the sample above:

```cs
public class CompleteOrderHandler1228388417 : MessageHandler
{
    private readonly OutboxedSessionFactory _outboxedSessionFactory;
    private readonly ILogger<Order> _logger;

    public CompleteOrderHandler1228388417(OutboxedSessionFactory outboxedSessionFactory, ILogger<Order> logger)
    {
        _outboxedSessionFactory = outboxedSessionFactory;
        _logger = logger;
    }
    
    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        await using var documentSession = _outboxedSessionFactory.OpenSession(context);
        var completeOrder = (CompleteOrder)context.Envelope.Message;
        string sagaId = context.Envelope.SagaId ?? completeOrder.Id;
        if (string.IsNullOrEmpty(sagaId)) throw new IndeterminateSagaStateIdException(context.Envelope);
        
        // Try to load the existing saga document
        var order = await documentSession.LoadAsync<Order>(sagaId, cancellation).ConfigureAwait(false);
        if (order == null)
        {
            throw new UnknownSagaException(typeof(Order), sagaId);
        }

        else
        {
            order.Handle(completeOrder, _logger);
            if (order.IsCompleted())
            {
                // Register the document operation with the current session
                documentSession.Delete(order);
            }
            else
            {
                
                // Register the document operation with the current session
                documentSession.Update(order);
            }
            
            // Commit all pending changes
            await documentSession.SaveChangesAsync(cancellation).ConfigureAwait(false);
        }

    }
}
```

## Saga Message Identity

::: warning
The automatic saga id tracking on messaging **only** works when the saga already exists and you are handling
a message to an existing saga. In the case of creating a new `Saga` and needing to publish outgoing messages related
to that `Saga` in the same logical transaction, you will have to embed the new `Saga` identity into the outgoing message bodies.
:::

In the case of two Wolverine applications sending messages between themselves, or a single Wolverine
application messaging itself in regards to an existing ongoing saga, Wolverine will quietly track
the saga id through headers. In most other cases, you will need to expose the saga identity
directly on the incoming messages.

To do that, Wolverine determines what public member of the saga message refers to the saga
identity. In order of precedence, Wolverine first looks for a member decorated with the
`[SagaIdentity]` attribute like this:

<!-- snippet: sample_toyontray -->
<a id='snippet-sample_toyontray'></a>
```cs
public class ToyOnTray
{
    // There's always *some* reason to deviate,
    // so you can use this attribute to tell Wolverine
    // that this property refers to the Id of the
    // Saga state document
    [SagaIdentity] public int OrderId { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HappyMealSaga.cs#L251-L261' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_toyontray' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

After that, you can also use a new `[SagaIdentityFrom]` (as of 5.9) attribute on~~~~ a handler parameter:

<!-- snippet: sample_using_sagaidentityfrom -->
<a id='snippet-sample_using_sagaidentityfrom'></a>
```cs
public class SomeSaga
{
    public Guid Id { get; set; }

    public void Handle([SagaIdentityFrom(nameof(SomeSagaMessage5.Hello))] SomeSagaMessage5 message) { }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Persistence/Sagas/saga_id_member_determination.cs#L63-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_sagaidentityfrom' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Next, Wolverine looks for a member named "{saga type name}Id." In the case of our `Order`
saga type, that would be a public member named `OrderId` as shown in this code:

```csharp
public record StartOrder(string OrderId);
```

And lastly, Wolverine looks for a public member named `Id` like this one:

```csharp
public record CompleteOrder(string Id);
```

## Strong-Typed Identifiers <Badge type="tip" text="5.x" />

Wolverine supports strong-typed identifiers (record structs or classes wrapping a primitive) as the saga identity.
The type must expose a `TryParse(string?, out T)` static method so Wolverine can recover the identity from the
envelope header when a message does not carry the ID directly on its body.

```csharp
// Strong-typed ID wrapping Guid
public record struct OrderSagaId(Guid Value)
{
    public static OrderSagaId New() => new(Guid.NewGuid());

    public static bool TryParse(string? input, out OrderSagaId result)
    {
        if (Guid.TryParse(input, out var guid))
        {
            result = new OrderSagaId(guid);
            return true;
        }
        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

public class OrderSaga : Saga
{
    public OrderSagaId Id { get; set; }

    public static OrderSaga Start(StartOrder cmd)
        => new() { Id = cmd.OrderId };

    // Messages that carry the ID on the body work automatically
    public void Handle(ShipOrder cmd) { /* ... */ }

    // Messages without the ID field read it from the envelope header
    public void Handle(OrderTimeout timeout) { /* ... */ }
}

public record StartOrder(OrderSagaId OrderId);
public record ShipOrder(OrderSagaId OrderSagaId);
public record OrderTimeout; // no saga ID field — read from envelope
```

::: tip
When the message type does not expose the saga ID as a field, Wolverine propagates the identity automatically through
the `SagaId` envelope header. The cascaded messages emitted from within a saga handler will have this header set
for you. In your own integration tests you can supply it via `envelope.SagaId = id.ToString()`.
:::

::: warning
Strong-typed identifiers backed by a third-party source-generator (e.g. [StronglyTypedId](https://github.com/andrewlock/StronglyTypedId))
are supported. The generated `TryParse` method on those types satisfies the requirement above.
:::

## Starting a Saga

::: tip
In all the cases where you return a `Saga` object from a handler method to denote the start of a new `Saga`, your code should
set the identity for the new `Saga`.
:::

To start a new saga, you have a couple options. You can use a static `Start()` or `StartAsync()` handler method on the `Saga` type itself
like this one on an `OrderSaga`:

<!-- snippet: sample_starting_a_saga_inside_a_handler -->
<a id='snippet-sample_starting_a_saga_inside_a_handler'></a>
```cs
// This method would be called when a StartOrder message arrives
// to start a new Order
public static (Order, OrderTimeout) Start(StartOrder order, ILogger<Order> logger)
{
    logger.LogInformation("Got a new order with id {Id}", order.OrderId);

    // creating a timeout message for the saga
    return (new Order{Id = order.OrderId}, new OrderTimeout(order.OrderId));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/OrderSaga.cs#L22-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_starting_a_saga_inside_a_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
The automatic saga id tracking on messaging **only** works when the saga already exists and you are handling
a message to an existing saga. In the case of creating a new `Saga` and needing to publish outgoing messages related
to that `Saga` in the same logical transaction, you will have to embed the new `Saga` identity into the outgoing message bodies.
:::

You can also simply return one or more `Saga` type objects from a handler method as shown below where `Reservation` is a Wolverine saga:

<!-- snippet: sample_reservation_saga -->
<a id='snippet-sample_reservation_saga'></a>
```cs
public class Reservation : Saga
{
    public string? Id { get; set; }

    // Apply the CompleteReservation to the saga
    public void Handle(BookReservation book, ILogger<Reservation> logger)
    {
        logger.LogInformation("Completing Reservation {Id}", book.Id);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }

    // Delete this Reservation if it has not already been deleted to enforce a "timeout"
    // condition
    public void Handle(ReservationTimeout timeout, ILogger<Reservation> logger)
    {
        logger.LogInformation("Applying timeout to Reservation {Id}", timeout.Id);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/SagaExample.cs#L73-L98' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_reservation_saga' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and the handler that would start the new saga:

<!-- snippet: sample_return_saga_from_handler -->
<a id='snippet-sample_return_saga_from_handler'></a>
```cs
public class StartReservationHandler
{
    public static (
        // Outgoing message
        ReservationBooked,

        // Starts a new Saga
        Reservation,

        // Additional message cascading for the new saga
        ReservationTimeout) Handle(StartReservation start)
    {
        return (
            new ReservationBooked(start.ReservationId, DateTimeOffset.UtcNow),
            new Reservation { Id = start.ReservationId },
            new ReservationTimeout(start.ReservationId)
            );
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/SagaExample.cs#L51-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_return_saga_from_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Method Conventions

::: tip
Note that there are several different legal synonyms for "Handle" or "Consume." This is due
to early attempts to make Wolverine backward compatible with its ancestor tooling. Just pick one
name or style in your application and use that consistently throughout.
:::

The following method names are meaningful in `Saga` types:

| Name                                 | Description                                                                                                         |
|--------------------------------------|---------------------------------------------------------------------------------------------------------------------|
| `Start`, `Starts`                    | Only called if the identified saga does not already exist *and* the incoming message contains the new saga identity |
| `StartOrHandle`, `StartsOrHandles`   | Called if the identified saga regardless of whether the saga already exists or is new |
| `Handle`, `Handles`                  | Called only when the identified saga already exists |
| `Consume`, `Consumes`                | Called only when the identified saga already exists |
| `Orchestrate`, `Orchestrates`        | Called only when the identified saga already exists |
| `NotFound`                           | Only called if the identified saga does not already exist, and there is no matching `Start` handler for the incoming message |

Note that only `Start`, `Starts`, or `NotFound` methods can be static methods because these methods logically assume that the
identified `Saga` does not yet exist. Wolverine as of 4.6 will assert that other named `Saga` methods are instance
methods to try to head off confusion.

## When Sagas are Not Found

::: warning
You need to explicitly use the `NotFound()` convention for Wolverine to quietly ignore messages related to a `Saga`
that cannot be found. As an example, if you receive a "timeout" message for an active `Saga` that has been completed and
deleted, you will need to implement `NotFound(message)` **even if it is an empty, do nothing method** just so Wolverine
will not blow up with an exception (not) helpfully telling you the requested `Saga` cannot be found.
:::

If you receive a command message against a `Saga` that no longer exists, Wolverine will throw an `Exception` unless
you explicitly handle the "not found" case. To do so for a particular command type -- and note that Wolverine does not
do any magic handling today based on abstractions -- you can implement a public static method called `NotFound` on your
`Saga` class for a particular message type that will take action against that incoming message as shown below:

<!-- snippet: sample_using_not_found -->
<a id='snippet-sample_using_not_found'></a>
```cs
public static void NotFound(CompleteOrder complete, ILogger<Order> logger)
{
    logger.LogInformation("Tried to complete order {Id}, but it cannot be found", complete.Id);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/OrderSaga.cs#L60-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_not_found' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that you will have to explicitly use `IMessageBus` as an argument to a `NotFound` method to send out any messages
to potentially take action on a missing saga if you so wish.

## Marking a Saga as Complete

When a `Saga` workflow is complete, call the `MarkCompleted()` method as shown in the following method
to let Wolverine know that the `Saga` can be safely deleted:

<!-- snippet: sample_using_saga_mark_completed -->
<a id='snippet-sample_using_saga_mark_completed'></a>
```cs
// Apply the CompleteOrder to the saga
public void Handle(CompleteOrder complete, ILogger<Order> logger)
{
    logger.LogInformation("Completing order {Id}", complete.Id);

    // That's it, we're done. Delete the saga state after the message is done.
    MarkCompleted();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/OrderSaga.cs#L35-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_saga_mark_completed' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Timeout Messages

You may frequently want to create "timeout" messages as part of a `Saga` to enforce time limitations. This can be done
with scheduled messages in Wolverine, but because this usage is so common with `Saga` implementations and because
Wolverine really wants you to be able to use pure functions as much as possible, you can subclass the Wolverine `TimeoutMessage`
for any logical message that will be scheduled in the future like so:

<!-- snippet: sample_ordertimeout -->
<a id='snippet-sample_ordertimeout'></a>
```cs
// This message will always be scheduled to be delivered after
// a one minute delay
public record OrderTimeout(string Id) : TimeoutMessage(1.Minutes());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/OrderSaga.cs#L11-L16' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ordertimeout' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That `OrderTimeout` message can be published with normal cascaded messages (or by calling `IMessageBus.PublishAsync()` if you prefer)
like so:

<!-- snippet: sample_starting_a_saga_inside_a_handler -->
<a id='snippet-sample_starting_a_saga_inside_a_handler'></a>
```cs
// This method would be called when a StartOrder message arrives
// to start a new Order
public static (Order, OrderTimeout) Start(StartOrder order, ILogger<Order> logger)
{
    logger.LogInformation("Got a new order with id {Id}", order.OrderId);

    // creating a timeout message for the saga
    return (new Order{Id = order.OrderId}, new OrderTimeout(order.OrderId));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/OrderSaga.cs#L22-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_starting_a_saga_inside_a_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And the handler for the message type is just a normal handler signature:

<!-- snippet: sample_handling_a_timeout_message -->
<a id='snippet-sample_handling_a_timeout_message'></a>
```cs
// Delete this order if it has not already been deleted to enforce a "timeout"
// condition
public void Handle(OrderTimeout timeout, ILogger<Order> logger)
{
    logger.LogInformation("Applying timeout to order {Id}", timeout.Id);

    // That's it, we're done. Delete the saga state after the message is done.
    MarkCompleted();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/OrderSaga.cs#L47-L58' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handling_a_timeout_message' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Saga Concurrency

Both the Marten and EF Core backed saga support has built in support for optimistic concurrency checks on persisting
a saga after handling a command. See [Dealing with Concurrency](/tutorials/concurrency) and especially the 
[partitioned sequential messaging](/tutorials/concurrency) and its option for "inferred" message grouping to maybe completely
side step concurrency issues with saga message handling. 

## Lightweight Saga Storage <Badge type="tip" text="3.0" />

The Wolverine integration with either Sql Server or PostgreSQL comes with a lightweight saga storage mechanism
where Wolverine will happily stand up a database table per `Saga` type in your configured envelope storage database and
merely store the saga state as serialized JSON (System.Text.Json is used for serialization in all cases). There's 
a handful of things to know about this:

* The automatic migration of lightweight saga tables can be disabled by the [AutoBuildMessageStorageOnStartup](/guide/durability/managing.html#disable-automatic-storage-migration)
  flag
* The lightweight saga storage supports optimistic concurrency by default and will throw a `SagaConcurrencyException` in
  the case of a `Saga` being modified by another `Saga` command while the current command is being processed
* The lightweight saga storage is supported by both the [PostgreSQL](/guide/durability/postgresql.html) and [Sql Server](/guide/durability/sqlserver.html) integration
* If the Marten integration is active, Marten will take precedence for the `Saga` storage for each type
* If the EF Core integration is active, the EF Core `DbContext` backed `Saga` persistence will take precedence *if* Wolverine
  can find a `DbContext` that has a mapping for that `Saga` type
* Wolverine's default table naming convention is just "{Saga class name}_saga"

To either control the saga table names or to ensure that the lightweight tables are part of Wolverine's offline database migration
capabilities, you can manually register saga types at configuration time:

<!-- snippet: sample_manually_adding_saga_types -->
<a id='snippet-sample_manually_adding_saga_types'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.AddSagaType<RedSaga>("red");
        opts.AddSagaType(typeof(BlueSaga),"blue");
        
        
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "color_sagas");
        opts.Services.AddResourceSetupOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Sagas/configuring_saga_table_storage.cs#L22-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_manually_adding_saga_types' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that this manual registration is not necessary at development time or if you're content to just let Wolverine
handle database migrations at runtime.

### SQL Server String Identity and nvarchar

By default, Wolverine's lightweight saga storage uses Weasel's inferred column type for the saga identity column.
For `string` identities on SQL Server, this results in a `varchar(100)` primary key column. However, ADO.NET's
`SqlClient` binds .NET `string` parameters as `nvarchar` (unicode) by default. This mismatch between a `varchar`
column and `nvarchar` query parameters forces SQL Server to perform implicit conversions, which prevents index
seeks and can cause significant performance degradation.

To fix this, you can opt in to an `nvarchar(100)` identity column when registering a saga type:

```cs
opts.AddSagaType<MySaga>(useNVarCharForStringId: true);

// or with a custom table name
opts.AddSagaType<MySaga>("my_saga_table", useNVarCharForStringId: true);
```

This only affects string-identified sagas using Wolverine's lightweight SQL Server saga storage. It has no effect
on Guid/int/long identities, and does not apply to Marten, EF Core, or PostgreSQL saga persistence.

::: warning
Enabling this option on an existing database will trigger a schema migration from `varchar(100)` to `nvarchar(100)`
on the saga table's primary key column.
:::

## Overriding Logging

We recently had a question about how to turn down logging levels for `Saga` message processing when the log
output was getting too verbose. `Saga` types are officially message handlers to the Wolverine internals, so you can 
still use the `public static void Configure(HandlerChain)` mechanism for one off configurations to every message handler
method on the `Saga` like this:

<!-- snippet: sample_overriding_logging_on_saga -->
<a id='snippet-sample_overriding_logging_on_saga'></a>
```cs
public class RevisionedSaga : Wolverine.Saga
{
    // This works just the same as on any other message handler
    // type
    public static void Configure(HandlerChain chain)
    {
        chain.ProcessingLogLevel = LogLevel.None;
        chain.SuccessLogLevel = LogLevel.None;
    }
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Saga/RevisionedSaga.cs#L80-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_overriding_logging_on_saga' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or if you wanted to just do it globally, something like this approach:

<!-- snippet: sample_turn_down_logging_for_sagas -->
<a id='snippet-sample_turn_down_logging_for_sagas'></a>
```cs
public class TurnDownLoggingOnSagas : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var sagaChain in chains.OfType<SagaChain>())
        {
            sagaChain.ProcessingLogLevel = LogLevel.None;
            sagaChain.SuccessLogLevel = LogLevel.None;
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/SagaChainPolicies.cs#L26-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_turn_down_logging_for_sagas' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and register that policy something like this:

<!-- snippet: sample_configuring_chain_policy_on_sagas -->
<a id='snippet-sample_configuring_chain_policy_on_sagas'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Policies.Add<TurnDownLoggingOnSagas>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/SagaChainPolicies.cs#L15-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_chain_policy_on_sagas' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Multiple Sagas Handling the Same Message Type

By default, Wolverine does not allow multiple saga types to handle the same message type and will throw an `InvalidSagaException` at startup if this is detected. However, there are valid architectural reasons to have multiple, independent saga workflows react to the same event — for example, an `OrderPlaced` event might start both a `ShippingSaga` and a `BillingSaga`.

To enable this, set `MultipleHandlerBehavior` to `Separated`:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

        // Your persistence configuration here (Marten, EF Core, etc.)
    }).StartAsync();
```

When `Separated` mode is active, Wolverine creates an independent handler chain for each saga type, routed to its own local queue. Each saga independently manages its own lifecycle — loading, creating, updating, and deleting state — without interfering with the other.

Here is an example with two sagas that both start from an `OrderPlaced` message but complete independently:

```cs
// Shared message that both sagas react to
public record OrderPlaced(Guid OrderPlacedId, string ProductName);

// Messages specific to each saga
public record OrderShipped(Guid ShippingSagaId);
public record PaymentReceived(Guid BillingSagaId);

public class ShippingSaga : Saga
{
    public Guid Id { get; set; }
    public string ProductName { get; set; } = string.Empty;

    public static ShippingSaga Start(OrderPlaced message)
    {
        return new ShippingSaga
        {
            Id = message.OrderPlacedId,
            ProductName = message.ProductName
        };
    }

    public void Handle(OrderShipped message)
    {
        MarkCompleted();
    }
}

public class BillingSaga : Saga
{
    public Guid Id { get; set; }
    public string ProductName { get; set; } = string.Empty;

    public static BillingSaga Start(OrderPlaced message)
    {
        return new BillingSaga
        {
            Id = message.OrderPlacedId,
            ProductName = message.ProductName
        };
    }

    public void Handle(PaymentReceived message)
    {
        MarkCompleted();
    }
}
```

When an `OrderPlaced` message is published, both sagas will be started independently. Completing one saga (e.g., by sending `OrderShipped`) does not affect the other.

::: warning
In `Separated` mode, messages routed to multiple sagas must be **published** (via `SendAsync` or `PublishAsync`), not **invoked** (via `InvokeAsync`). `InvokeAsync` bypasses message routing and will not reach the separated saga endpoints.
:::

## Resequencer Saga

Wolverine supports the [Resequencer](https://www.enterpriseintegrationpatterns.com/patterns/messaging/Resequencer.html) pattern
out of the box through the `ResequencerSaga<T>` base class. This is useful when you need to process messages in a specific order,
but they may arrive out of sequence.

Messages must implement the `SequencedMessage` interface:

```cs
public interface SequencedMessage
{
    int? Order { get; }
}
```

Then subclass `ResequencerSaga<T>` instead of `Saga`:

```cs
public record StartMyWorkflow(Guid Id);

public record MySequencedCommand(Guid SagaId, int? Order) : SequencedMessage;

public class MyWorkflowSaga : ResequencerSaga<MySequencedCommand>
{
    public Guid Id { get; set; }

    public static MyWorkflowSaga Start(StartMyWorkflow cmd)
    {
        return new MyWorkflowSaga { Id = cmd.Id };
    }

    public void Handle(MySequencedCommand cmd)
    {
        // This will only be called when messages arrive in the correct order,
        // or when out-of-order messages are replayed after gaps are filled
    }
}
```

### How It Works

Wolverine generates a `ShouldProceed()` guard around your `Handle`/`Orchestrate` methods:

- If `Order` is `null` or `0`, the message bypasses the guard and is handled immediately
- If `Order` equals `LastSequence + 1` (the next expected sequence), the handler executes normally and `LastSequence` advances
- If `Order` is greater than `LastSequence + 1` (a gap exists), the message is added to the `Pending` list and the handler is **not** called
- When a gap-filling message arrives, any consecutive pending messages are automatically re-published to be handled in order

The saga state is **always** persisted regardless of whether the handler was called, because the `Pending` list and `LastSequence` may have changed.

### Key Properties

| Property | Description |
|----------|-------------|
| `LastSequence` | The highest sequence number that has been processed in order |
| `Pending` | Messages received out of order, waiting for earlier messages to arrive |

### Concurrency Considerations

When using `ResequencerSaga`, we recommend also using [Partitioned Sequential Messaging](/guide/messaging/partitioning) to manage potential concurrency conflicts. When `UseInferredMessageGrouping()` is enabled, Wolverine automatically detects `SequencedMessage` types and uses the `Order` property as the group id for partitioning. Messages with `null` order values receive a random group id so they are distributed independently.

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.MessagePartitioning
            // Automatically infers grouping from saga identity AND
            // SequencedMessage.Order for resequencer sagas
            .UseInferredMessageGrouping();
    }).StartAsync();
```

## Scatter-Gather Pattern

::: tip
For a detailed walkthrough of a real-world multi-step saga workflow with fan-out and result collection, see the blog post
[Multi Step Workflows with the Critter Stack](https://jeremydmiller.com/2024/10/01/multi-step-workflows-with-the-critter-stack/).
:::

The [Scatter-Gather](https://www.enterpriseintegrationpatterns.com/patterns/messaging/BroadcastAggregate.html) pattern
(sometimes called "fan-out/fan-in") is a common messaging pattern where you:

1. **Scatter**: Fan out requests to multiple services or handlers in parallel
2. **Gather**: Collect the responses and aggregate them into a final result
3. **Complete**: When all responses have arrived (or a timeout fires), emit the aggregated result

In frameworks like Apache Camel or MuleSoft, scatter-gather is a first-class routing pattern with
dedicated DSL support. In Wolverine, you implement it naturally as a **saga** — the saga state tracks
which responses have arrived, and the saga completes when all expected responses are collected.

### Example: Price Comparison

Consider a price comparison service that fans out quote requests to multiple suppliers and aggregates
the results:

```cs
// Messages
public record RequestQuotes(Guid QuoteId, string ProductName, string[] Suppliers);

public record RequestSupplierQuote(Guid QuoteId, string Supplier, string ProductName);

public record SupplierQuoteResponse(Guid QuoteId, string Supplier, decimal Price);

public record QuoteTimeout(Guid QuoteId) : TimeoutMessage(30.Seconds());

public record AllQuotesCollected(Guid QuoteId, Dictionary<string, decimal> Quotes);
```

```cs
public class QuoteCollection : Saga
{
    public Guid Id { get; set; }
    public string ProductName { get; set; } = "";
    public int ExpectedResponses { get; set; }
    public Dictionary<string, decimal> CollectedQuotes { get; set; } = new();

    // Scatter: start the saga and fan out requests to each supplier
    public static (QuoteCollection, OutgoingMessages) Start(RequestQuotes request)
    {
        var saga = new QuoteCollection
        {
            Id = request.QuoteId,
            ProductName = request.ProductName,
            ExpectedResponses = request.Suppliers.Length
        };

        var messages = new OutgoingMessages();
        foreach (var supplier in request.Suppliers)
        {
            messages.Add(new RequestSupplierQuote(
                request.QuoteId, supplier, request.ProductName));
        }

        // Schedule a timeout in case some suppliers never respond
        messages.Add(new QuoteTimeout(request.QuoteId));

        return (saga, messages);
    }

    // Gather: collect each supplier response
    public AllQuotesCollected? Handle(SupplierQuoteResponse response)
    {
        CollectedQuotes[response.Supplier] = response.Price;

        // Check if all responses have arrived
        if (CollectedQuotes.Count >= ExpectedResponses)
        {
            MarkCompleted();
            return new AllQuotesCollected(Id, CollectedQuotes);
        }

        // Still waiting for more responses
        return null;
    }

    // Timeout: emit whatever we have and complete the saga
    public AllQuotesCollected Handle(QuoteTimeout timeout)
    {
        MarkCompleted();
        return new AllQuotesCollected(Id, CollectedQuotes);
    }
}
```

The key points in this pattern:

- **`Start()` returns `OutgoingMessages`** to fan out individual requests to each supplier. Each
  `RequestSupplierQuote` message will be handled independently — possibly by different services
  or by different instances of the same service.
- **`Handle(SupplierQuoteResponse)` collects results** into the saga state. When all expected
  responses arrive, the saga emits the aggregated `AllQuotesCollected` message and marks itself
  complete.
- **`Handle(QuoteTimeout)` provides a safety net** — if some suppliers are slow or unresponsive,
  the timeout fires and the saga completes with whatever quotes it has collected. The `TimeoutMessage`
  base class schedules the message for future delivery automatically.
- **Returning `null`** from a handler method means "no cascading message" — the saga state is still
  persisted, but no downstream work is triggered.

### Variations

**Fire-and-forget scatter** — If you don't need to collect responses (e.g., sending notifications
to multiple channels), the saga can simply fan out messages in `Start()` and immediately
`MarkCompleted()`:

```cs
public static (NotificationSaga, OutgoingMessages) Start(
    SendNotifications request)
{
    var saga = new NotificationSaga { Id = request.Id };
    saga.MarkCompleted();

    var messages = new OutgoingMessages();
    messages.Add(new SendEmail(request.Id, request.Message));
    messages.Add(new SendSms(request.Id, request.Message));
    messages.Add(new SendPushNotification(request.Id, request.Message));

    return (saga, messages);
}
```

**Partial results** — If you want to emit intermediate results as responses arrive, return a
cascading message from each `Handle()` call:

```cs
public QuoteUpdated Handle(SupplierQuoteResponse response)
{
    CollectedQuotes[response.Supplier] = response.Price;

    if (CollectedQuotes.Count >= ExpectedResponses)
    {
        MarkCompleted();
    }

    // Emit an update after every response
    return new QuoteUpdated(Id, CollectedQuotes);
}
```

## Saga Concurrency

::: warning
Concurrency is the most common source of subtle bugs in saga implementations. If two messages for the
same saga instance are processed simultaneously, they can overwrite each other's state changes. Take
time to understand the strategies below and choose the right one for your workload.
:::

When multiple messages targeting the same saga arrive at the same time — which is especially common
in the scatter-gather pattern where multiple responses arrive in quick succession — you need a
strategy to prevent lost updates. Wolverine provides several approaches, and you can combine them.

For a deeper discussion of concurrency strategies in Wolverine, see the
[Dealing with Concurrency](/tutorials/concurrency) tutorial.

### Optimistic Concurrency with Revisioning

If your saga persistence supports optimistic concurrency (Marten and EF Core both do), you can
implement the `IRevisioned` interface on your saga class. Wolverine will automatically check the
version on save and throw a `ConcurrencyException` if the saga was modified by another handler
between load and save:

```cs
public class QuoteCollection : Saga, IRevisioned
{
    public Guid Id { get; set; }
    public int Version { get; set; }

    // ... handlers as before
}
```

Pair this with a retry policy on the handler chain to automatically retry on conflict:

```cs
public static void Configure(HandlerChain chain)
{
    chain.OnException<ConcurrencyException>()
        .RetryWithCooldown(100.Milliseconds(), 250.Milliseconds());
}
```

This approach works well when conflicts are **infrequent** — each retry re-loads the saga state and
re-applies the handler, so the second attempt will see the first handler's changes.

### Partitioned Sequential Messaging

For sagas that receive a high volume of messages per instance (like the scatter-gather pattern with
many suppliers), optimistic concurrency retries can become expensive. A better approach is to ensure
that messages for the same saga instance are processed **sequentially** using
[Partitioned Sequential Messaging](/guide/messaging/partitioning):

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.MessagePartitioning
            .UseInferredMessageGrouping();
    }).StartAsync();
```

When `UseInferredMessageGrouping()` is enabled, Wolverine automatically detects saga identity
properties on messages and uses them as the partition key. All messages with the same saga ID are
routed to the same local queue partition, guaranteeing they are processed one at a time. This
eliminates concurrency conflicts entirely for saga messages, at the cost of limiting parallelism
per saga instance.

::: tip
Partitioned sequential messaging is the recommended default strategy for sagas that use the
scatter-gather pattern. It provides the strongest correctness guarantees with the least complexity.
:::

### Combining Strategies

In practice, you may want both:

- **Partitioned messaging** for the local processing of saga messages within a single application
  node, ensuring no two handlers for the same saga run concurrently on the same node
- **Optimistic concurrency** as a safety net in multi-node deployments, where messages could
  occasionally be processed on different nodes targeting the same saga

```cs
public class QuoteCollection : Saga, IRevisioned
{
    public Guid Id { get; set; }
    public int Version { get; set; }

    // Optimistic concurrency retry as a safety net
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<ConcurrencyException>()
            .RetryWithCooldown(100.Milliseconds(), 250.Milliseconds());
    }

    // ... handlers
}
```

```cs
// Plus partitioned messaging for local ordering
opts.MessagePartitioning.UseInferredMessageGrouping();
```

## Testing Sagas

::: tip
Wolverine's [TrackedSession](/guide/testing) support already provides everything you need for saga testing. Unlike NServiceBus (which has a dedicated `SagaScenarioTestingLibrary` with virtual storage) or Rebus (which has `SagaFixture`), Wolverine takes the approach that sagas are just message handlers with persisted state — so the same `TrackedSession` tools you use for all Wolverine integration testing work for sagas too.
:::

### Setting Up a Saga Test

The simplest way to test a saga is to spin up an `IHost` with your saga type registered, then use `InvokeMessageAndWaitAsync()` or `SendMessageAndWaitAsync()` to drive the saga through its states. The tracked session will wait for all cascading messages to complete before returning control to your test.

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Register your saga and any related handler types
        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType(typeof(Order));
    }).StartAsync();
```

::: tip
For production applications with a real persistence backend, use `RunWolverineInSoloMode()` and `DisableAllExternalWolverineTransports()` for faster test startup, just as described in the [integration testing guide](/guide/http/integration-testing).
:::

### Testing State Transitions

To verify a saga moves through states correctly, send the starting message, then send subsequent messages and load the saga state to check properties:

```cs
[Fact]
public async Task saga_state_is_updated_across_messages()
{
    var sagaId = Guid.NewGuid();

    // Start the saga
    await host.InvokeMessageAndWaitAsync(
        new StartOrder(sagaId.ToString()));

    // Send a follow-up message
    await host.SendMessageAndWaitAsync(
        new CompleteOrder(sagaId.ToString()));
}
```

When using the in-memory saga persistence (the default with no persistence configured), you can load saga state directly:

```cs
var persistor = host.Services
    .GetRequiredService<InMemorySagaPersistor>();
var state = persistor.Load<Order>(sagaId);

// Saga was completed and deleted
state.ShouldBeNull();
```

With Marten, use the document session:

```cs
using var session = host.Services
    .GetRequiredService<IDocumentStore>()
    .LightweightSession();
var state = await session.LoadAsync<Order>(sagaId);
```

### Testing Saga Completion

When a saga calls `MarkCompleted()`, Wolverine deletes the saga state after the handler finishes. To verify a saga has completed, load its state and assert it's null:

```cs
[Fact]
public async Task saga_is_deleted_after_completion()
{
    var id = Guid.NewGuid();

    // Start the saga
    await host.InvokeMessageAndWaitAsync(new StartOrder(id.ToString()));

    // Complete it
    await host.InvokeMessageAndWaitAsync(new CompleteOrder(id.ToString()));

    // Verify the saga state has been deleted
    var persistor = host.Services
        .GetRequiredService<InMemorySagaPersistor>();
    persistor.Load<Order>(id).ShouldBeNull();
}
```

### Testing Cascading Messages from Sagas

Saga handlers frequently return cascading messages — either as return values or through tuple returns. The `ITrackedSession` returned from `InvokeMessageAndWaitAsync()` and `SendMessageAndWaitAsync()` lets you inspect every message that was sent or executed during the test:

```cs
[Fact]
public async Task cascading_messages_carry_the_saga_id()
{
    var id = Guid.NewGuid();

    // Start the saga
    await host.InvokeMessageAndWaitAsync(new StartCascadingTest(id));

    // Trigger a handler that returns a cascading message
    var tracked = await host.SendMessageAndWaitAsync(
        new TriggerCascade(id));

    // Verify the cascaded message was executed
    var cascadedEnvelopes = tracked.Executed.Envelopes()
        .Where(x => x.Message is CascadedMessage)
        .ToArray();

    cascadedEnvelopes.ShouldNotBeEmpty();

    // Verify the saga ID was propagated to cascaded messages
    foreach (var envelope in cascadedEnvelopes)
    {
        envelope.SagaId.ShouldBe(id.ToString());
    }
}
```

This is particularly useful for verifying that cascading messages from a saga carry the correct `SagaId` in the envelope, which is how Wolverine routes follow-up messages back to the correct saga instance.

### Testing Timeouts and Scheduled Messages

Wolverine sagas support timeout messages via the `TimeoutMessage` base class. When a saga's `Start()` method returns a `TimeoutMessage`, Wolverine schedules it for future delivery. In tests, you can verify the timeout was scheduled and even play it back immediately:

```cs
// Recall the Order saga Start method returns both the saga
// and a scheduled OrderTimeout message:
//
// public static (Order, OrderTimeout) Start(StartOrder order)
// {
//     return (new Order { Id = order.OrderId },
//             new OrderTimeout(order.OrderId));
// }

[Fact]
public async Task timeout_message_is_scheduled_on_start()
{
    var tracked = await host
        .InvokeMessageAndWaitAsync(new StartOrder("order-1"));

    // Verify the timeout was scheduled
    tracked.Scheduled
        .MessagesOf<OrderTimeout>()
        .ShouldNotBeEmpty();
}

[Fact]
public async Task play_back_scheduled_timeout()
{
    var tracked = await host
        .InvokeMessageAndWaitAsync(new StartOrder("order-1"));

    // Fast-forward: immediately execute any scheduled messages
    await tracked.PlayScheduledMessagesAsync(
        TimeSpan.FromSeconds(10));

    // After timeout, the saga should be completed and deleted
    var persistor = host.Services
        .GetRequiredService<InMemorySagaPersistor>();
    persistor.Load<Order>("order-1").ShouldBeNull();
}
```

The `PlayScheduledMessagesAsync()` method on `ITrackedSession` is the key here — it lets you "fast forward" scheduled messages in tests without waiting for real time to pass.

### Testing the NotFound Path

Wolverine sagas support a static `NotFound()` method that handles messages when no matching saga state exists. This is a common pattern for handling race conditions or out-of-order message delivery:

```cs
// On the Order saga:
// public static void NotFound(CompleteOrder complete, ILogger<Order> logger)
// {
//     logger.LogInformation("Order {Id} not found", complete.Id);
// }

[Fact]
public async Task not_found_handler_is_invoked_for_missing_saga()
{
    // Send a message for a saga that doesn't exist
    await host.InvokeMessageAndWaitAsync(
        new CompleteOrder("nonexistent-order"));

    // No exception thrown — the NotFound handler was invoked
}
```

### Advanced: TrackActivity() for Saga Tests

For more complex saga testing scenarios, use the fluent `TrackActivity()` API. This is especially useful when you need to:

- Track activity across multiple hosts (e.g., testing sagas that publish messages to external transports)
- Ignore specific background message types that interfere with tracking
- Override timeouts for long-running saga tests
- Coordinate with Marten async projections

```cs
var tracked = await host.TrackActivity()
    .Timeout(TimeSpan.FromSeconds(30))
    .DoNotAssertOnExceptionsDetected()
    .ExecuteAndWaitAsync(async context =>
    {
        await context.SendAsync(new StartOrder("order-1"));
    });

// Inspect all message activity
var allMessages = tracked.Sent.AllMessages().ToArray();
```

### How This Compares to Other Frameworks

| Feature | NServiceBus | Rebus | Wolverine |
|---------|-------------|-------|-----------|
| **Testing API** | `SagaScenarioTestingLibrary` with virtual storage | `SagaFixture<T>` with in-memory storage | `TrackedSession` + real or in-memory persistence |
| **Virtual time** | Built-in time advancement | Manual timeout delivery | `PlayScheduledMessagesAsync()` on `ITrackedSession` |
| **Message assertions** | Saga-specific assert methods | `fixture.AssertSagaData()` | `tracked.Sent.SingleMessage<T>()`, `tracked.Executed.MessagesOf<T>()` |
| **State inspection** | Via virtual saga storage | Via `SagaFixture.Data` | Load state directly from persistence (in-memory, Marten, EF Core, etc.) |
| **External messages** | Separate test transport | Separate fake transport | `DisableAllExternalWolverineTransports()` + `tracked.Sent` inspection |

The key difference is that Wolverine doesn't require a separate saga-specific testing library. Since sagas are just message handlers with persisted state, the same `TrackedSession` tooling that tests any Wolverine handler works identically for sagas — including full visibility into cascading messages, scheduled messages, and saga ID propagation.
