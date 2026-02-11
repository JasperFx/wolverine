# Migrating to Wolverine

This guide is for developers coming to Wolverine from other .NET messaging and mediator frameworks. Whether you're
using MassTransit, NServiceBus, MediatR, Rebus, or Brighter, this document covers the key conceptual differences,
practical migration paths, and best practices for adopting Wolverine.

::: tip
Wolverine is a unified framework that handles both in-process mediator usage *and* asynchronous messaging with external
brokers. If you're currently using MediatR *plus* a separate messaging framework, Wolverine can replace both with a
single set of conventions.
:::

::: warning
Wolverine does **not** support interfaces or abstract types as message types for the purpose of routing or handler
discovery. All message types must be **concrete classes or records**. If your current system publishes messages as
interfaces (a common pattern in MassTransit and NServiceBus), you will need to convert these to concrete types. During
a gradual migration, use `opts.Policies.RegisterInteropMessageAssembly(assembly)` to help Wolverine map from
interface-based messages to concrete types.

However, and especially if you are pursing a [Modular Monolith Architecture](/tutorials/modular-monolith), you can still do ["sticky" assignments](/guide/handlers/sticky) of
Wolverine message handlers to specific listener endpoints. 
:::

## Wolverine vs "IHandler of T" Frameworks

Almost every popular .NET messaging and mediator framework follows the "IHandler of T" pattern -- your handlers must
implement a framework interface or inherit from a framework base class. This includes MassTransit's `IConsumer<T>`,
NServiceBus's `IHandleMessages<T>`, MediatR's `IRequestHandler<T>`, Rebus's `IHandleMessages<T>`, and Brighter's
`RequestHandler<T>`.

Wolverine takes a fundamentally different approach: **convention over configuration**. Your handlers are plain C#
methods with no required interfaces, base classes, or attributes. Wolverine infers everything from method signatures.

### The Interface-Based Pattern

Every "IHandler of T" framework follows roughly the same pattern:

```csharp
// MassTransit
public class OrderConsumer : IConsumer<SubmitOrder>
{
    public async Task Consume(ConsumeContext<SubmitOrder> context) { ... }
}

// NServiceBus
public class OrderHandler : IHandleMessages<SubmitOrder>
{
    public async Task Handle(SubmitOrder message, IMessageHandlerContext context) { ... }
}

// MediatR
public class OrderHandler : IRequestHandler<SubmitOrder, OrderResult>
{
    public async Task<OrderResult> Handle(SubmitOrder request, CancellationToken ct) { ... }
}

// Rebus
public class OrderHandler : IHandleMessages<SubmitOrder>
{
    public async Task Handle(SubmitOrder message) { ... }
}

// Brighter
public class OrderHandler : RequestHandler<SubmitOrder>
{
    public override SubmitOrder Handle(SubmitOrder command)
    {
        // ... must call base.Handle(command) to continue pipeline
        return base.Handle(command);
    }
}
```

In every case you must:
1. Implement a specific interface or inherit from a specific base class
2. Register that handler with the framework (sometimes automatic, sometimes manual)
3. Inject dependencies through the constructor
4. Use the framework's context object to publish or send additional messages

### The Wolverine Way

::: tip
Unlike some other messaging frameworks, Wolverine does **not** require you to explicitly register message
handlers against a specific listener endpoint like a Rabbit MQ queue or an Azure Service Bus subscription.
:::

Wolverine discovers handlers through naming conventions. The [best practice](/introduction/best-practices) is to write
handlers as **pure functions** -- static methods that take in data and return decisions:

```csharp
// No interface, no base class, static method, pure function
public static class SubmitOrderHandler
{
    // First parameter = message type (by convention)
    // Return value = cascading message (published automatically)
    // IDocumentSession = dependency injected as method parameter
    public static OrderSubmitted Handle(SubmitOrder command, IDocumentSession session)
    {
        session.Store(new Order(command.OrderId));
        return new OrderSubmitted(command.OrderId);
    }
}
```

This is possible because Wolverine uses **runtime code generation** to build optimized execution pipelines at startup.
Rather than resolving handlers from an IoC container at runtime and invoking them through interface dispatch, Wolverine
generates C# code that directly calls your methods, injects dependencies, and handles cascading messages -- all with
minimal allocations and clean exception stack traces.

::: tip
It's an imperfect world, and Wolverine's code generation strategy can easily be an issue in production resource utilization,
but fear not! Wolverine has [some mechanisms to avoid that problem](http://localhost:5050/guide/codegen.html#generating-code-ahead-of-time) easily in real projection usage.
:::

### Why Pure Functions Matter

The Wolverine team strongly recommends writing handlers as pure functions whenever possible. A pure function:

- Takes in all its inputs as parameters (the message, loaded entities, injected services)
- Returns its outputs explicitly (cascading messages, side effects, storage operations)
- Has no hidden side effects (no injecting `IMessageBus` deep in the call stack to secretly publish messages)

This matters for **testability**: pure function handlers can be unit tested with zero mocking infrastructure:

```csharp
[Fact]
public void submit_order_publishes_submitted_event()
{
    var result = SubmitOrderHandler.Handle(
        new SubmitOrder("ABC-123"),
        someSession);

    result.OrderId.ShouldBe("ABC-123");
}
```

Compare this to the typical "IHandler of T" test that requires mocking the framework context, the message bus,
repositories, and verifying mock interactions.

### Railway Programming

Wolverine supports a form of [Railway Programming](/tutorials/railway-programming) through its compound handler support.
By using `Before`, `Validate`, or `Load` methods alongside the main `Handle` method, you can separate the "sad path"
(validation failures, missing data) from the "happy path" (business logic):

```csharp
public static class ShipOrderHandler
{
    // Runs first -- handles the "sad path"
    public static async Task<(HandlerContinuation, Order?)> LoadAsync(
        ShipOrder command, IDocumentSession session)
    {
        var order = await session.LoadAsync<Order>(command.OrderId);
        return order == null
            ? (HandlerContinuation.Stop, null)
            : (HandlerContinuation.Continue, order);
    }

    // Pure function -- only runs on the "happy path"
    public static ShipmentCreated Handle(ShipOrder command, Order order)
    {
        return new ShipmentCreated(order.Id, order.ShippingAddress);
    }
}
```

Returning `HandlerContinuation.Stop` from a `Before`/`Validate`/`Load` method aborts processing before the main handler
executes. For HTTP endpoints, you can return `ProblemDetails` instead for RFC 7807 compliant error responses.

### Middleware: Runtime Pipeline vs Compile-Time Code Generation

::: info
If you're familiar with NServiceBus's concept of "Behaviors," that concept was originally taken directly from
[FubuMVC's `BehaviorGraph` model](https://fubumvc.github.io) that allowed you to attach middleware strategies
on a message type by message type basis through a mix of explicit configuration and user defined conventions or policies.

Wolverine itself was started as a "next generation, .NET Core"
successor to the earlier FubuMVC and its FubuTransportation messaging bus add on. However, where the NServiceBus team improved
the admittedly grotesque inefficiency of FubuMVC through more efficient usage of `Expression` compilation to lambda functions,
Wolverine beat the same problems through [its code generation model](/guide/codegen). 
:::

In "IHandler of T" frameworks, middleware wraps handler execution at runtime:

| Framework | Middleware Model |
|-----------|-----------------|
| MassTransit | `IFilter<T>` with `next.Send(context)` |
| NServiceBus | `Behavior<TContext>` with `await next()` |
| MediatR | `IPipelineBehavior<TRequest, TResponse>` with `await next()` |
| Rebus | `IIncomingStep` / `IOutgoingStep` with `await next()` |
| Brighter | Attribute-driven decorators, must call `base.Handle()` |

All of these apply middleware to **every** message regardless of whether the middleware is relevant, then use
runtime conditional logic to skip irrelevant cases.

Wolverine's [middleware](/guide/handlers/middleware) is fundamentally different. It uses compile-time code generation -- your middleware methods are woven directly into the generated handler code at startup:

```csharp
public class AuditMiddleware
{
    public static void Before(ILogger logger, Envelope envelope)
    {
        logger.LogInformation("Processing {MessageType}", envelope.MessageType);
    }

    public static void Finally(ILogger logger, Envelope envelope)
    {
        logger.LogInformation("Completed {MessageType}", envelope.MessageType);
    }
}
```

A critical advantage is that Wolverine middleware can be **selectively applied on a message type by message type basis**:

```csharp
// Apply only to messages in a specific namespace
opts.Policies.AddMiddleware<AuditMiddleware>(chain =>
    chain.MessageType.IsInNamespace("MyApp.Commands"));

// Apply only to messages implementing a marker interface
opts.Policies.AddMiddleware<ValidationMiddleware>(chain =>
    chain.MessageType.CanBeCastTo<IValidatable>());

// Apply to a specific message type
opts.Policies.AddMiddleware<SpecialMiddleware>(chain =>
    chain.MessageType == typeof(ImportantCommand));
```

This means middleware that only applies to certain message types is never even included in the generated code for other
handlers. No runtime conditional checks, no wasted allocations, and much cleaner exception stack traces.

### Comparison Table

| Aspect | MassTransit | NServiceBus | MediatR | Rebus | Brighter | Wolverine |
|--------|------------|-------------|---------|-------|----------|-----------|
| Handler contract | `IConsumer<T>` | `IHandleMessages<T>` | `IRequestHandler<T>` | `IHandleMessages<T>` | `RequestHandler<T>` base class | None (convention) |
| Static handlers | No | No | No | No | No | Yes |
| Method injection | No | No | No | No | No | Yes |
| Pure function style | Difficult | Difficult | Difficult | Difficult | Difficult | First-class |
| Return values as messages | No | No | Response only | No | Pipeline chain | Cascading messages |
| Middleware model | Runtime filters | Runtime behaviors | Runtime pipeline | Runtime steps | Attribute decorators | Compile-time codegen |
| Per-message-type middleware | Via consumer definition | Via pipeline stage | No (all handlers) | No (global) | Yes (per-handler attributes) | Yes (policy filters) |
| In-process mediator | No (use MediatR) | No | Yes | No | Yes | Yes (`InvokeAsync`) |
| Async messaging | Yes | Yes | No | Yes | Yes (with Darker) | Yes |
| Transactional outbox | Yes (w/ EF Core) | Yes | No | No | Yes | Yes (built-in) |
| Railway programming | No | No | No | No | No | Yes (compound handlers) |
| License | Apache 2.0 | Commercial | Apache 2.0 | MIT | MIT | MIT |

## From MassTransit

If you're migrating from [MassTransit](https://masstransit.io/), Wolverine has built-in
[interoperability](/tutorials/interop#interop-with-masstransit) for RabbitMQ, Azure Service Bus, and Amazon SQS/SNS,
enabling a gradual migration where both frameworks exchange messages during the transition.

### Handlers

**MassTransit** `IConsumer<T>` with `ConsumeContext<T>`:

```csharp
public class SubmitOrderConsumer : IConsumer<SubmitOrder>
{
    private readonly IOrderRepository _repo;

    public SubmitOrderConsumer(IOrderRepository repo)
    {
        _repo = repo;
    }

    public async Task Consume(ConsumeContext<SubmitOrder> context)
    {
        await _repo.Save(new Order(context.Message.OrderId));
        await context.Publish(new OrderSubmitted { OrderId = context.Message.OrderId });
    }
}
```

**Wolverine** equivalent as a pure function:

```csharp
public static class SubmitOrderHandler
{
    public static (OrderSubmitted, Storage.Insert<Order>) Handle(SubmitOrder command)
    {
        var order = new Order(command.OrderId);
        return (
            new OrderSubmitted(command.OrderId),  // cascading message
            Storage.Insert(order)                  // side effect
        );
    }
}
```

### Error Handling

**MassTransit** layers separate middleware:

```csharp
cfg.ReceiveEndpoint("orders", e => {
    e.UseMessageRetry(r => r.Interval(5, TimeSpan.FromSeconds(1)));
    e.UseDelayedRedelivery(r => r.Intervals(
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)));
    e.UseInMemoryOutbox();
});
```

**Wolverine** uses declarative [error handling policies](/guide/handlers/error-handling):

```csharp
opts.Policies.Failures.Handle<DbException>()
    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds())
    .Then.MoveToErrorQueue();

opts.Policies.Failures.Handle<TimeoutException>()
    .ScheduleRetry(5.Minutes(), 15.Minutes(), 30.Minutes());
```

### Sagas

MassTransit state machines (`MassTransitStateMachine<T>`) require a complete rewrite. Consumer sagas
(`ISaga`/`InitiatedBy<T>`/`Orchestrates<T>`) map more directly:

| MassTransit | Wolverine |
|-------------|-----------|
| `InitiatedBy<T>` | `Start(T)` method |
| `Orchestrates<T>` | `Handle(T)` or `Orchestrate(T)` method |
| `InitiatedByOrOrchestrates<T>` | `StartOrHandle(T)` method |
| `SetCompletedWhenFinalized()` | `MarkCompleted()` |
| `SagaStateMachineInstance` | `Saga` base class, state as properties |

### Send/Publish

| Operation | MassTransit | Wolverine |
|-----------|------------|-----------|
| Command | `Send<T>()` via `ISendEndpointProvider` | `SendAsync<T>()` via `IMessageBus` |
| Event | `Publish<T>()` via `IPublishEndpoint` | `PublishAsync<T>()` via `IMessageBus` |
| In-process | N/A (separate MediatR) | `InvokeAsync<T>()` via `IMessageBus` |
| Request/response | `IRequestClient<T>` | `InvokeAsync<TResponse>()` |

### Configuration

**MassTransit**:
```csharp
services.AddMassTransit(x => {
    x.AddConsumer<SubmitOrderConsumer>();
    x.UsingRabbitMq((context, cfg) => {
        cfg.Host("localhost");
        cfg.ConfigureEndpoints(context);
    });
});
```

**Wolverine**:
```csharp
builder.Host.UseWolverine(opts =>
{
    opts.UseRabbitMq(r => r.HostName = "localhost").AutoProvision();
    opts.ListenToRabbitQueue("orders");
    opts.PublishMessage<OrderSubmitted>().ToRabbitExchange("events");
});
```

### Transport Interoperability

Enable MassTransit interop on an endpoint-by-endpoint basis:

```csharp
opts.ListenToRabbitQueue("incoming")
    .DefaultIncomingMessage<SubmitOrder>()
    .UseMassTransitInterop();

opts.Policies.RegisterInteropMessageAssembly(typeof(SharedMessages).Assembly);
```

Supported transports: RabbitMQ, Azure Service Bus, Amazon SQS/SNS. See the full
[interoperability guide](/tutorials/interop#interop-with-masstransit).

### Migration Checklist

**Phase 1: Coexistence**
- [ ] Add Wolverine and transport NuGet packages alongside MassTransit
- [ ] Configure `UseWolverine()` in your host setup
- [ ] Enable `UseMassTransitInterop()` on endpoints exchanging messages
- [ ] Register shared assemblies: `opts.Policies.RegisterInteropMessageAssembly(assembly)`
- [ ] Convert interface-based message types to concrete classes or records
- [ ] Write new handlers in Wolverine while existing MassTransit consumers run

**Phase 2: Handler Migration**
- [ ] Convert `IConsumer<T>` to Wolverine convention handlers
- [ ] Replace `ConsumeContext<T>` with method parameter injection
- [ ] Replace `context.Publish()` with return values (cascading messages)
- [ ] Refactor toward pure functions
- [ ] Convert `IFilter<T>` middleware to Wolverine [conventional middleware](/guide/handlers/middleware)
- [ ] Rewrite retry config to Wolverine error handling policies

**Phase 3: Saga Migration**
- [ ] Convert consumer sagas to Wolverine `Saga` with `Start`/`Handle` methods
- [ ] Rewrite state machine sagas as Wolverine saga classes
- [ ] Configure saga persistence (Marten, EF Core, SQL Server, etc.)

**Phase 4: Cleanup**
- [ ] Remove MassTransit interop, packages, and registration code
- [ ] Enable Wolverine's [transactional outbox](/guide/durability/)
- [ ] Consider [pre-generated types](/guide/codegen) for production performance

## From NServiceBus

If you're migrating from [NServiceBus](https://particular.net/nservicebus), Wolverine has built-in
[interoperability](/tutorials/interop#interop-with-nservicebus) for RabbitMQ, Azure Service Bus, and Amazon SQS/SNS.
NServiceBus's wire protocol is quite similar to Wolverine's, so interop tends to work cleanly.

::: info
NServiceBus requires a commercial license for production use. Wolverine is MIT licensed and free for all use.
:::

### Handlers

**NServiceBus** `IHandleMessages<T>`:

```csharp
public class SubmitOrderHandler : IHandleMessages<SubmitOrder>
{
    private readonly IOrderRepository _repo;

    public SubmitOrderHandler(IOrderRepository repo)
    {
        _repo = repo;
    }

    public async Task Handle(SubmitOrder message, IMessageHandlerContext context)
    {
        await _repo.Save(new Order(message.OrderId));
        await context.Publish(new OrderSubmitted { OrderId = message.OrderId });
    }
}
```

**Wolverine** equivalent as a pure function:

```csharp
public static class SubmitOrderHandler
{
    public static OrderSubmitted Handle(SubmitOrder command, IDocumentSession session)
    {
        session.Store(new Order(command.OrderId));
        return new OrderSubmitted(command.OrderId);
    }
}
```

Key migration steps:
- Remove the `IHandleMessages<T>` interface
- Change the `Handle(T message, IMessageHandlerContext context)` signature to `Handle(T message, ...dependencies...)`
- Replace `context.Send()` / `context.Publish()` with return values (cascading messages)
- Consider making handlers static with method injection

### Commands vs Events

NServiceBus enforces a strict distinction between commands (`ICommand`) and events (`IEvent`) with marker interfaces.
Commands can only be `Send()`, events can only be `Publish()`.

Wolverine has no such enforcement. Any concrete type can be a message. The distinction between `SendAsync()` (expects
at least one subscriber, throws if none) and `PublishAsync()` (silently succeeds with no subscribers) is behavioral,
not based on the message type.

### Error Handling / Recoverability

**NServiceBus** uses a two-tier retry model:

```csharp
var recoverability = endpointConfiguration.Recoverability();
recoverability.Immediate(i => i.NumberOfRetries(3));
recoverability.Delayed(d => {
    d.NumberOfRetries(2);
    d.TimeIncrease(TimeSpan.FromSeconds(15));
});
recoverability.AddUnrecoverableException<ValidationException>();
```

**Wolverine** provides per-exception-type [error handling policies](/guide/handlers/error-handling):

```csharp
opts.Policies.Failures.Handle<DbException>()
    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds())
    .Then.ScheduleRetry(15.Seconds(), 30.Seconds())
    .Then.MoveToErrorQueue();

opts.Policies.Failures.Handle<ValidationException>()
    .MoveToErrorQueue(); // skip all retries
```

Wolverine's approach gives you finer-grained control: different exception types can have entirely different retry
strategies, and actions are chainable (retry inline, then schedule, then dead letter).

### Sagas

| NServiceBus | Wolverine |
|-------------|-----------|
| `Saga<TSagaData>` base class | `Saga` base class (no generic parameter) |
| Separate `ContainSagaData` class | State properties directly on saga class |
| `IAmStartedByMessages<T>` | `Start(T)` / `Starts(T)` method |
| `IHandleMessages<T>` on saga | `Handle(T)` / `Orchestrate(T)` method |
| `ConfigureHowToFindSaga()` | Convention: `[SagaIdentity]`, `{SagaType}Id`, `SagaId`, or `Id` |
| `MarkAsComplete()` | `MarkCompleted()` |
| `IHandleTimeouts<T>` | Scheduled messages (use `ScheduleAsync()`) |
| `RequestTimeout<T>()` | Return a `DelayedMessage<T>` from handler |

### Pipeline Behaviors

**NServiceBus** `Behavior<TContext>`:

```csharp
public class LogBehavior : Behavior<IIncomingLogicalMessageContext>
{
    public override async Task Invoke(
        IIncomingLogicalMessageContext context, Func<Task> next)
    {
        Console.WriteLine("Before");
        await next();
        Console.WriteLine("After");
    }
}
```

**Wolverine** middleware with per-message-type filtering:

```csharp
public class LogMiddleware
{
    public static void Before(ILogger logger, Envelope envelope)
    {
        logger.LogInformation("Before {Type}", envelope.MessageType);
    }

    public static void After(ILogger logger, Envelope envelope)
    {
        logger.LogInformation("After {Type}", envelope.MessageType);
    }
}

// Apply only to specific message types
opts.Policies.AddMiddleware<LogMiddleware>(chain =>
    chain.MessageType.IsInNamespace("MyApp.ImportantMessages"));
```

NServiceBus behaviors are singletons that run for every message. Wolverine middleware is code-generated per handler
chain and can be filtered to only the message types that need it.

### Configuration

**NServiceBus**:
```csharp
var endpointConfiguration = new EndpointConfiguration("Sales");
endpointConfiguration.UseTransport(new RabbitMQTransport(
    RoutingTopology.Conventional(QueueType.Quorum), connectionString));
endpointConfiguration.UsePersistence<SqlPersistence>();

var routing = endpointConfiguration.UseTransport(transport);
routing.RouteToEndpoint(typeof(PlaceOrder), "Sales.Orders");
```

**Wolverine**:
```csharp
builder.Host.UseWolverine(opts =>
{
    opts.UseRabbitMq(r => r.HostName = "localhost").AutoProvision();
    opts.PersistMessagesWithPostgresql(connectionString);
    opts.PublishMessage<PlaceOrder>().ToRabbitQueue("sales-orders");
    opts.ListenToRabbitQueue("sales-orders");
});
```

Key differences:
- NServiceBus uses `EndpointConfiguration`; Wolverine uses `UseWolverine()` on the .NET Generic Host
- NServiceBus selects one transport; Wolverine can use multiple transports simultaneously
- NServiceBus routes commands by assembly or type; Wolverine configures explicit routing per message type

### Transport Interoperability

Enable NServiceBus interop on an endpoint-by-endpoint basis:

```csharp
opts.ListenToAzureServiceBusQueue("incoming")
    .UseNServiceBusInterop();

opts.Policies.RegisterInteropMessageAssembly(typeof(SharedMessages).Assembly);
```

Wolverine detects message types from standard NServiceBus headers. You may need [message type aliases](/guide/messages#message-type-name-or-alias)
to bridge naming differences. See the full [interoperability guide](/tutorials/interop#interop-with-nservicebus).

### Migration Checklist

**Phase 1: Coexistence**
- [ ] Add Wolverine and transport NuGet packages alongside NServiceBus
- [ ] Configure `UseWolverine()` in your host setup
- [ ] Enable `UseNServiceBusInterop()` on endpoints exchanging messages
- [ ] Register shared assemblies: `opts.Policies.RegisterInteropMessageAssembly(assembly)`
- [ ] Convert `ICommand`/`IEvent` interface message types to concrete types
- [ ] Write new handlers in Wolverine while NServiceBus handlers continue running

**Phase 2: Handler Migration**
- [ ] Remove `IHandleMessages<T>` interfaces from handler classes
- [ ] Replace `IMessageHandlerContext` with method parameter injection
- [ ] Replace `context.Send()`/`context.Publish()` with cascading message return values
- [ ] Refactor toward pure functions and static handlers
- [ ] Convert pipeline behaviors to Wolverine middleware
- [ ] Rewrite recoverability config to Wolverine error handling policies

**Phase 3: Saga Migration**
- [ ] Convert `Saga<TSagaData>` to Wolverine `Saga` base class
- [ ] Move saga data properties onto the saga class directly
- [ ] Convert `IAmStartedByMessages<T>` to `Start(T)` methods
- [ ] Convert `ConfigureHowToFindSaga()` to convention-based correlation
- [ ] Replace `IHandleTimeouts<T>` with scheduled messages

**Phase 4: Cleanup**
- [ ] Remove NServiceBus interop, packages, and configuration
- [ ] Remove NServiceBus license file
- [ ] Enable Wolverine's [transactional outbox](/guide/durability/)
- [ ] Consider [pre-generated types](/guide/codegen) for production

## From MediatR

For a detailed comparison of MediatR and Wolverine, see the dedicated [Wolverine for MediatR Users](/introduction/from-mediatr) guide.

The key differences in summary:

- **No `IRequest<T>` / `IRequestHandler<T>`** -- Wolverine handlers are discovered by convention
- **No `INotificationHandler`** -- Use Wolverine's [local queues](/guide/messaging/transports/local) with [durable inbox/outbox](/guide/durability/) for reliable background work
- **No `IPipelineBehavior`** -- Use Wolverine [middleware](/guide/handlers/middleware) with per-message-type filtering
- **Return values are cascading messages** -- No need for explicit `IMediator.Send()` to chain work
- **Pure function handlers** -- Static methods, method injection, no mocking needed for unit tests
- **Railway Programming** -- Use [compound handlers](/guide/handlers/#compound-handlers) with `Load`/`Validate` methods for sad-path handling
- **Unified model** -- Wolverine's `InvokeAsync()` replaces MediatR's `Send()`, and the same handler conventions work for both in-process and async messaging

The most common reason to migrate from MediatR is that Wolverine provides both the mediator pattern *and*
asynchronous messaging with durable outbox support in one framework, eliminating the need for MediatR + MassTransit or
MediatR + NServiceBus.

## From Rebus

[Rebus](https://github.com/rebus-org/Rebus) uses the same `IHandleMessages<T>` interface pattern as NServiceBus:

```csharp
// Rebus
public class OrderHandler : IHandleMessages<PlaceOrder>
{
    public async Task Handle(PlaceOrder message)
    {
        // handle the message
    }
}
```

**Wolverine** equivalent:

```csharp
public static class OrderHandler
{
    public static void Handle(PlaceOrder command)
    {
        // handle the message
    }
}
```

Key differences from Rebus:

- **No interfaces** -- Remove `IHandleMessages<T>`, Wolverine discovers handlers by convention
- **Sagas** -- Rebus uses `Saga<TSagaData>` with `IAmInitiatedBy<T>` and explicit `CorrelateMessages()`. Wolverine uses convention-based `Start`/`Handle` methods with automatic correlation
- **Error handling** -- Rebus has retry-count + error queue with optional `IFailed<T>` second-level handling. Wolverine has [per-exception-type policies](/guide/handlers/error-handling) with composable actions
- **Middleware** -- Rebus has global pipeline steps (`IIncomingStep`/`IOutgoingStep`). Wolverine has conventional middleware that can be [filtered per message type](/guide/handlers/middleware)
- **No transport interop** -- Unlike MassTransit and NServiceBus, there is no built-in Rebus interoperability in Wolverine. You would need a [custom envelope mapper](/tutorials/interop) or migrate endpoints fully

## From Brighter

[Brighter](https://github.com/BrighterCommand/Brighter) (Paramore.Brighter) uses a base class pattern with an
attribute-driven middleware pipeline:

```csharp
// Brighter
public class OrderHandler : RequestHandler<PlaceOrder>
{
    [RequestLogging(step: 1, timing: HandlerTiming.Before)]
    [UseResiliencePipeline(policy: "retry", step: 2)]
    public override PlaceOrder Handle(PlaceOrder command)
    {
        // handle the command
        return base.Handle(command);  // MUST call to continue pipeline
    }
}
```

**Wolverine** equivalent:

```csharp
public static class OrderHandler
{
    public static void Handle(PlaceOrder command)
    {
        // handle the command -- no base class, no pipeline chain to call
    }
}

// Middleware applied by policy, not attributes
opts.Policies.AddMiddleware<LoggingMiddleware>();
```

Key differences from Brighter:

- **No base class** -- No need to inherit from `RequestHandler<T>` or `RequestHandlerAsync<T>`
- **No `base.Handle()` chain** -- Wolverine handles pipeline chaining automatically via code generation; you cannot accidentally break the pipeline by forgetting `base.Handle()`
- **No sync/async split** -- Wolverine supports both sync and async handler methods in the same pipeline. Brighter requires entirely separate `RequestHandler<T>` vs `RequestHandlerAsync<T>` hierarchies
- **Middleware by policy *and/or* attributes** -- Wolverine applies middleware through policies that can filter by message type, namespace, or any predicate. Brighter uses per-handler `[RequestHandlerAttribute]` decorators with compile-time-constant parameters
- **Error handling** -- Brighter delegates to Polly via `[UseResiliencePipeline]` attributes. Wolverine has [built-in error handling policies](/guide/handlers/error-handling) with retry, schedule, requeue, and dead letter actions

::: info
Wolverine originally used Polly internally, but we felt like it was not adding any value in our particular usage and decided
to eliminate its usage as Polly's widespread adoption means that it's a common "diamond dependency conflict" waiting to happen. Marten continues
to use Polly for low level command resiliency. 
:::

## Message Routing

Wolverine supports any mix of explicit or conventional [message routing](/guide/messaging/subscriptions) to outbound endpoints (Rabbit MQ exchanges, Azure Service Bus or Kafka topics for example).
What Wolverine generally calls "conventional routing" is sometimes referred to by other tools as "automatic routing." In many
cases Wolverine's out of the box conventional routing choices are going to be very similar to MassTransit or NServiceBus's existing
routing topology both to ease interoperability and also because frankly we thought their routing rules made perfect sense as is.

## Transport Overview

::: tip
Wolverine's [transaction inbox & outbox support](/guide/durability/) is orthogonal to the message broker or transport integration packages and
is available for all of our supported messaging transports including our local, in process queues option. 
:::

If your current framework uses one of these transports, here's how they map to Wolverine:

| Transport | MassTransit Package | NServiceBus Package | Wolverine Package | Interop Support |
|-----------|-------------------|-------------------|-----------------|-----------------|
| RabbitMQ | `MassTransit.RabbitMQ` | `NServiceBus.Transport.RabbitMQ` | `Wolverine.RabbitMQ` | MassTransit, NServiceBus |
| Azure Service Bus | `MassTransit.Azure.ServiceBus.Core` | `NServiceBus.Transport.AzureServiceBus` | `Wolverine.AzureServiceBus` | MassTransit, NServiceBus |
| Amazon SQS | `MassTransit.AmazonSQS` | `NServiceBus.Transport.SQS` | `Wolverine.AmazonSqs` | MassTransit, NServiceBus |
| Amazon SNS | (via SQS) | (via SQS) | `Wolverine.AmazonSns` | MassTransit, NServiceBus |
| Kafka | `MassTransit.Kafka` | N/A | `Wolverine.Kafka` | CloudEvents |
| In-memory | `MassTransit.InMemory` | `LearningTransport` | Built-in [local queues](/guide/messaging/transports/local) | N/A |
| SQL Server | `MassTransit.SqlTransport` | `NServiceBus.Transport.SqlServer` | `Wolverine.SqlServer` | N/A |
| PostgreSQL | `MassTransit.PostgreSql` | `NServiceBus.Transport.PostgreSql` | `Wolverine.Postgresql` | N/A |

See the full list of [Wolverine transports](/guide/messaging/introduction) and the
[interoperability tutorial](/tutorials/interop) for configuration details.

Note that Wolverine supports a far greater number of messaging options because our community has been awesome at contributing
new "transports." 
