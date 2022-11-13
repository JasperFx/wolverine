# Sagas

As is so common in these docs, I would direct you to this from the old "EIP" book: [Process Manager](http://www.enterpriseintegrationpatterns.com/patterns/messaging/ProcessManager.html). A stateful saga in Wolverine is used
to coordinate long running workflows or to break large, logical transactions into a series of smaller steps. A stateful saga
in Wolverine consists of a couple parts:

1. A saga state document type that is persisted between saga messages that must inherit from the `Wolverine.Saga` type. This will also be your handler type for all messages
   that directly impact the saga
2. Messages that would update the saga state when handled
3. A saga persistence strategy registered in Wolverine that knows how to load and persist the saga state documents
4. An identity for the saga state in order to save, load, or delete the current saga state

## Your First Saga

*See the [OrderSagaSample](https://github.com/JasperFx/wolverine/tree/master/src/Samples/OrderSagaSample) project in GitHub for all the
sample code in this section.*

Jumping right into an example, consider a very simple order management service that will have steps to:

* Create a new order
* Complete the order
* Or alternatively, delete new orders if they have not been completed within 1 minute

For the moment, I’m going to ignore the underlying persistence and just focus on the Wolverine message handlers to implement the order saga workflow with this simplistic saga code:

<!-- snippet: sample_Order_saga -->
<a id='snippet-sample_order_saga'></a>
```cs
public record StartOrder(string OrderId);

public record CompleteOrder(string Id);

public record OrderTimeout(string Id) : TimeoutMessage(1.Minutes());

public class Order : Saga
{
    public string? Id { get; set; }

    // This method would be called when a StartOrder message arrives
    // to start a new Order
    public OrderTimeout Start(StartOrder order, ILogger<Order> logger)
    {
        Id = order.OrderId; // defining the Saga Id.

        logger.LogInformation("Got a new order with id {Id}", order.OrderId);
        // creating a timeout message for the saga
        return new OrderTimeout(order.OrderId);
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
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/OrderSaga.cs#L6-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_saga' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A few explanatory notes on this code before we move on to detailed documentation:

* Wolverine leans a bit on type and naming conventions to discover message handlers and to “know” how to call these message handlers. Some folks will definitely not like the magic, but this approach leads to substantially less code and arguably complexity compared to existing .Net tools
* Wolverine supports the idea of [scheduled messages](/guide/messaging/scheduled), and the new `TimeoutMessage` base class we used up there is just a shorthand way to utilize that support for “saga timeout” conditions
* Wolverine generally tries to adapt to your application code rather that using mandatory adapter interfaces
* Subclassing `Saga` is meaningful first as this tells Wolverine "hey, this stateful type should be treated as a saga" for [handler discovery](/guide/handlers/discovery), but also for communicating
  to Wolverine that a logical saga is complete and should be deleted

Now, to add saga persistence, I'm going to lean on the [Marten integration](/guide/durability/marten) with Wolverine and use this bootstrapping for our little order web service:

<!-- snippet: sample_bootstrapping_order_saga_sample -->
<a id='snippet-sample_bootstrapping_order_saga_sample'></a>
```cs
using Wolverine.Marten;
using Marten;
using Oakton;
using Oakton.Resources;
using OrderSagaSample;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// Not 100% necessary, but enables some extra command line diagnostics
builder.Host.ApplyOaktonExtensions();

// Adding Marten
builder.Services.AddMarten(opts =>
    {
        var connectionString = builder.Configuration.GetConnectionString("Marten");
        opts.Connection(connectionString);
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
app.MapPost("/start", (StartOrder start, ICommandBus bus) => bus.InvokeAsync(start));
app.MapPost("/complete", (CompleteOrder start, ICommandBus bus) => bus.InvokeAsync(start));
app.MapGet("/all", (IQuerySession session) => session.Query<Order>().ToListAsync());
app.MapGet("/", (HttpResponse response) =>
{
    response.Headers.Add("Location", "/swagger");
    response.StatusCode = 301;
}).ExcludeFromDescription();

app.UseSwagger();
app.UseSwaggerUI();

return await app.RunOaktonCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderSagaSample/Program.cs#L1-L58' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_order_saga_sample' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The call to `IServiceCollection.AddMarten().IntegrateWithWolverine()` adds the Marten backed saga persistence to your application. No other configuration
is necessary. See the [Marten integration](/guide/durability/marten.html#saga-storage) for a little more information about using Marten backed sagas.

## Saga Message Identity

In the case of two Wolverine applications sending messages between themselves, or a single Wolverine
application messaging itself in regards to an existing ongoing saga, Wolverine will quietly track
the saga id through headers. In most other cases, you will need to expose the saga identity
directly on the incoming messages.

To do that, Wolverine determines what public member of the saga message refers to the saga
identity. In order of precedence, Wolverine first looks for a member decorated with the
`[SagaIdentity]` attribute like this:

<!-- snippet: sample_ToyOnTray -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HappyMealSaga.cs#L248-L257' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_toyontray' title='Start of snippet'>anchor</a></sup>
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

## Starting a Saga

TODO -- do this first: https://github.com/JasperFx/wolverine/issues/751

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
// todo -- more later on Create


## When Sagas are Not Found

In the case

## Marking a Saga as Complete




## Timeout Messages
