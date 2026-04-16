# Integration Testing Wolverine.HTTP Endpoints

Wolverine.HTTP endpoints are designed from the ground up for testability — but unit tests alone don't tell the full story. When your HTTP endpoint publishes messages, kicks off cascading handlers, or writes to a database through middleware, you need integration tests that can **wait for all that asynchronous activity to finish** before making assertions.

Wolverine provides a first-class integration testing experience by combining two tools:

- [**Alba**](https://jasperfx.github.io/alba) — an in-memory HTTP testing library for ASP.NET Core that lets you make HTTP requests without a real network connection
- [**Wolverine Tracked Sessions**](/guide/testing) — Wolverine's built-in test coordination that tracks all message activity and waits for cascading work to complete

## Setting Up the Test Harness

### Install the Packages

You'll need `Alba` and your test framework of choice. Wolverine's tracking support ships in the core `WolverineFx` package — no extra package needed.

```xml
<PackageReference Include="Alba" Version="8.*" />
<PackageReference Include="xunit" Version="2.*" />
<PackageReference Include="Shouldly" Version="4.*" />
```

### Bootstrap Your Application with Alba

The recommended pattern is to share a single `IAlbaHost` across your test collection using xUnit's `ICollectionFixture`. This avoids the expensive startup cost of building the host for every test.

```cs
public class AppFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Required when using JasperFx command line integration
        JasperFxEnvironment.AutoStartHost = true;

        // Bootstrap the real application using its Program.Main() setup
        Host = await AlbaHost.For<Program>(x =>
        {
            x.ConfigureServices(services =>
            {
                // Run Wolverine in "solo" mode for faster test startup —
                // skips leader election, durability agents, etc.
                services.RunWolverineInSoloMode();

                // Disable external messaging transports (Rabbit MQ, SQS, etc.)
                // so tests run without infrastructure dependencies
                services.DisableAllExternalWolverineTransports();
            });
        });
    }

    public async Task DisposeAsync()
    {
        await Host.StopAsync();
        await Host.DisposeAsync();
    }
}

[CollectionDefinition("integration")]
public class IntegrationCollection : ICollectionFixture<AppFixture>;
```

::: tip
`RunWolverineInSoloMode()` sets `DurabilityMode.Solo`, which skips the durable inbox/outbox agents and leader election. Your application starts up significantly faster this way.
:::

::: tip
`DisableAllExternalWolverineTransports()` prevents Wolverine from trying to connect to Rabbit MQ, SQS, Kafka, or any other external broker during tests. Messages that would have been sent externally are silently discarded, but their sending is still tracked by the tracked session.
:::

### Create a Base Integration Context

Build a base class that every HTTP integration test inherits from. This is where you put the `TrackedHttpCall` helper method and any shared test setup:

```cs
[Collection("integration")]
public abstract class IntegrationContext : IAsyncLifetime
{
    private readonly AppFixture _fixture;

    protected IntegrationContext(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public IAlbaHost Host => _fixture.Host;

    async Task IAsyncLifetime.InitializeAsync()
    {
        // Reset database state before each test.
        // How you do this depends on your persistence provider:

        // For Marten:
        // await Host.ResetAllMartenDataAsync();

        // For EF Core, resolve your DbContext and clean up:
        // using var scope = Host.Services.CreateScope();
        // var db = scope.ServiceProvider.GetRequiredService<MyDbContext>();
        // await db.Database.EnsureDeletedAsync();
        // await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Simple Alba-only HTTP call (no message tracking)
    public Task<IScenarioResult> Scenario(Action<Scenario> configure)
    {
        return Host.Scenario(configure);
    }

    // The key method: combines Alba HTTP calls with Wolverine message tracking
    protected async Task<(ITrackedSession, IScenarioResult)> TrackedHttpCall(
        Action<Scenario> configuration,
        int timeoutInMilliseconds = 5000)
    {
        IScenarioResult result = null!;

        // The outer part ties into Wolverine's test support
        // to "wait" for all detected message activity to complete
        var tracked = await Host.ExecuteAndWaitAsync(async () =>
        {
            // The inner part makes an HTTP request
            // to the system under test with Alba
            result = await Host.Scenario(configuration);
        }, timeoutInMilliseconds);

        return (tracked, result);
    }
}
```

The `TrackedHttpCall` method is the centerpiece of this approach. It wraps an Alba HTTP request inside Wolverine's `ExecuteAndWaitAsync`, which:

1. Starts tracking all message activity in the system
2. Executes your HTTP request via Alba
3. Waits until **all** messages published, sent, or cascaded as a result of that HTTP request have finished processing
4. Returns both the HTTP response (`IScenarioResult`) and the tracked session (`ITrackedSession`)

## Using TrackedHttpCall

### Testing an Endpoint That Publishes Messages

Consider an endpoint that accepts a command and publishes it as a message:

```cs
// The endpoint
[WolverinePost("/api/orders")]
public static AcceptResponse Post(PlaceOrder command, IMessageBus bus)
{
    bus.PublishAsync(new OrderPlaced(command.OrderId, command.CustomerId));
    return new AcceptResponse("/api/orders/" + command.OrderId);
}
```

Your integration test can verify both the HTTP response and the published message:

```cs
public class OrderEndpointTests : IntegrationContext
{
    public OrderEndpointTests(AppFixture fixture) : base(fixture) { }

    [Fact]
    public async Task place_order_publishes_event()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(new PlaceOrder("order-123", "customer-456"))
                .ToUrl("/api/orders");
            x.StatusCodeShouldBe(202);
        });

        // Verify the published message
        tracked.Sent.SingleMessage<OrderPlaced>()
            .OrderId.ShouldBe("order-123");
    }
}
```

### Testing Cascading Messages

Wolverine endpoints can return cascading messages via tuple returns or `OutgoingMessages`. The tracked session captures the entire chain of activity:

```cs
// The endpoint returns multiple cascaded messages
[WolverinePost("/spawn")]
public static (string, OutgoingMessages) Post(SpawnInput input)
{
    var messages = new OutgoingMessages
    {
        new HttpMessage1(input.Name),
        new HttpMessage2(input.Name),
        new HttpMessage3(input.Name),
        new HttpMessage4(input.Name)
    };

    return ("got it", messages);
}
```

```cs
[Fact]
public async Task cascading_messages_are_all_tracked()
{
    var (tracked, result) = await TrackedHttpCall(x =>
    {
        x.Post.Json(new SpawnInput("Chris Jones")).ToUrl("/spawn");
    });

    // Verify the HTTP response
    result.ReadAsText().ShouldBe("got it");

    // Verify all four cascaded messages were sent
    tracked.Sent.SingleMessage<HttpMessage1>().Name.ShouldBe("Chris Jones");
    tracked.Sent.SingleMessage<HttpMessage2>().Name.ShouldBe("Chris Jones");
    tracked.Sent.SingleMessage<HttpMessage3>().Name.ShouldBe("Chris Jones");
    tracked.Sent.SingleMessage<HttpMessage4>().Name.ShouldBe("Chris Jones");
}
```

### Testing an Endpoint That Creates a Resource

When an endpoint creates a resource and returns a `201 Created` with a `Location` header, you can follow the redirect in the same test:

```cs
[Fact]
public async Task create_and_verify_resource()
{
    string url = null!;

    var tracked = await Host.ExecuteAndWaitAsync(async _ =>
    {
        var results = await Host.Scenario(opts =>
        {
            opts.Post.Json(new CreateTodo("Buy groceries"))
                .ToUrl("/todoitems");
            opts.StatusCodeShouldBe(201);
        });

        // Capture the Location header for follow-up
        url = results.Context.Response.Headers.Location!;
    });

    // Follow up: fetch the created resource
    var todo = await Host.GetAsJson<Todo>(url);
    todo!.Name.ShouldBe("Buy groceries");

    // Verify the cascaded event was handled
    var @event = tracked.Executed.SingleMessage<TodoCreated>();
    @event.Id.ShouldBe(todo.Id);
}
```

### Testing with EF Core Persistence

The same pattern works with Entity Framework Core. After the tracked session completes, query the database to verify side effects:

```cs
[Fact]
public async Task create_item_through_http()
{
    var name = Guid.NewGuid().ToString();
    using var host = await AlbaHost.For<Program>();

    var tracked = await host.ExecuteAndWaitAsync(async () =>
    {
        await host.Scenario(x =>
        {
            x.Post.Json(new CreateItemCommand { Name = name })
                .ToUrl("/items/create");
            x.StatusCodeShouldBe(204);
        });
    });

    // Verify the cascaded message was handled
    tracked.FindSingleTrackedMessageOfType<ItemCreated>()
        .ShouldNotBeNull();

    // Verify the database state
    using var scope = host.Services.CreateScope();
    var context = scope.ServiceProvider
        .GetRequiredService<ItemsDbContext>();
    var item = await context.Items
        .FirstOrDefaultAsync(x => x.Name == name);
    item.ShouldNotBeNull();
}
```

## The ITrackedSession API

The `ITrackedSession` returned from `ExecuteAndWaitAsync` (and by extension `TrackedHttpCall`) gives you full visibility into all message activity that occurred during your test:

### RecordCollections

| Property | What It Contains |
|----------|------------------|
| `Sent` | All messages that were sent or published, including to local queues |
| `Received` | All messages that were received by handlers |
| `Executed` | All messages that were executed (includes retries as separate records) |
| `ExecutionStarted` | All messages that started execution |
| `ExecutionFinished` | All messages that finished execution |
| `MessageSucceeded` | All messages that completed successfully |
| `MessageFailed` | All messages that failed during processing |
| `Scheduled` | All messages that were scheduled for later execution |
| `NoHandlers` | Messages received with no registered handler |
| `NoRoutes` | Messages that could not be routed |
| `MovedToErrorQueue` | Messages that exhausted retry policies |
| `Requeued` | Messages that were requeued for retry |
| `Discarded` | Messages discarded by exception handling policies |

### Querying Records

Each `RecordCollection` provides several ways to query tracked messages:

```cs
// Find the single message of type T (throws if zero or more than one)
var order = tracked.Sent.SingleMessage<OrderPlaced>();

// Find the envelope record (includes metadata like destination, headers)
var record = tracked.Sent.SingleRecord<OrderPlaced>();
var envelope = tracked.Sent.SingleEnvelope<OrderPlaced>();

// Find all messages of a type
var events = tracked.Executed.MessagesOf<OrderPlaced>();

// Get all messages regardless of type
var allMessages = tracked.Sent.AllMessages();

// Get the raw envelope records in order
var records = tracked.Sent.RecordsInOrder();
```

### Cross-Cutting Queries

`ITrackedSession` also provides methods that search across all record collections:

```cs
// Find any single message of type T across all activity
var msg = tracked.FindSingleTrackedMessageOfType<OrderPlaced>();

// Find message records by type and event
var records = tracked.FindEnvelopesWithMessageType<OrderPlaced>(
    MessageEventType.Sent);

// Get all activity in chronological order
var allRecords = tracked.AllRecordsInOrder();

// Get all exceptions thrown during the session
var exceptions = tracked.AllExceptions();
```

## When You Don't Need Message Tracking

Not every HTTP test needs the overhead of tracked sessions. For simple endpoints that don't publish messages or trigger async work, use Alba directly:

```cs
[Fact]
public async Task simple_get_endpoint()
{
    var result = await Host.Scenario(x =>
    {
        x.Get.Url("/");
        x.Header("content-type")
            .SingleValueShouldEqual("text/plain");
    });

    result.ReadAsText().ShouldBe("Hello.");
}

[Fact]
public async Task not_found_returns_404()
{
    await Host.Scenario(x =>
    {
        x.Get.Url("/todoitems/99999");
        x.StatusCodeShouldBe(404);
    });
}

[Fact]
public async Task invalid_json_returns_problem_details()
{
    var result = await Host.Scenario(opts =>
    {
        dynamic wrongJson = new { Title = true };
        opts.Post.Json(wrongJson).ToUrl("/api/todo-lists");
        opts.StatusCodeShouldBe(400);
    });

    var problem = result.ReadAsJson<ProblemDetails>();
    problem.Status.ShouldBe(400);
}
```

## Advanced: TrackActivity() Configuration

For more control over tracking behavior, use the fluent `TrackActivity()` API instead of the simpler `ExecuteAndWaitAsync()`:

```cs
var tracked = await Host.TrackActivity()
    .Timeout(TimeSpan.FromSeconds(30))           // Override the default timeout
    .DoNotAssertOnExceptionsDetected()            // Don't fail on exceptions
    .IncludeExternalTransports()                  // Track external broker messages
    .IgnoreMessageType<HealthCheckPing>()         // Ignore background noise
    .ExecuteAndWaitAsync(async context =>
    {
        await Host.Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/api/orders");
        });
    });
```

### Key Configuration Options

| Method | Purpose |
|--------|---------|
| `Timeout(TimeSpan)` | Override how long to wait for all activity to complete |
| `DoNotAssertOnExceptionsDetected()` | Useful when testing error handling — normally the session throws if any handler threw |
| `IncludeExternalTransports()` | Track messages to external brokers (disabled by default when using `DisableAllExternalWolverineTransports`) |
| `IgnoreMessageType<T>()` | Exclude specific message types from tracking (useful for background polling) |
| `AlsoTrack(otherHost)` | Track message activity across multiple Wolverine hosts in the same process |
| `WaitForMessageToBeReceivedAt<T>(host)` | Don't complete until a specific message type arrives at a specific host |

## Marten-Specific Testing Helpers

If you're using Wolverine with [Marten](https://martendb.io), the `Wolverine.Marten` package provides additional testing extensions:

```cs
// Reset all Marten data before each test
async Task IAsyncLifetime.InitializeAsync()
{
    await Host.ResetAllMartenDataAsync();
}

// Or as part of tracked session configuration:
var tracked = await Host.TrackActivity()
    .ResetAllMartenDataFirst()
    .ExecuteAndWaitAsync(async context =>
    {
        // your test action
    });
```

For applications using Marten's async projections, use the daemon coordination helpers:

```cs
var tracked = await Host.TrackActivity()
    // Pause the async daemon, run the action, then catch up
    .PauseThenCatchUpOnMartenDaemonActivity()
    .ExecuteAndWaitAsync(async context =>
    {
        await Host.Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/api/incidents");
        });
    });
```

This ensures your async projections have fully processed all events before you make assertions — eliminating flaky tests caused by projection lag.

## Sample Projects

The Wolverine repository includes several complete sample projects that demonstrate these integration testing patterns:

| Sample | Location | What It Demonstrates |
|--------|----------|---------------------|
| TodoWebService | `src/Samples/TodoWebService/` | Basic CRUD with Alba, tracked sessions, cascading `TodoCreated` events |
| IncidentService | `src/Samples/IncidentService/` | Event sourcing with Marten, `CreationResponse`, `AppFixture` + `IntegrationContext` pattern |
| EFCoreSample | `src/Samples/EFCoreSample/` | Entity Framework Core with `ExecuteAndWaitAsync`, database verification |

## Further Reading

- [Alba Documentation](https://jasperfx.github.io/alba) — Full reference for Alba's HTTP testing API including `Scenario`, authentication stubs, and JSON helpers
- [Wolverine Tracked Sessions](/guide/testing) — Complete guide to Wolverine's `ITrackedSession` and message tracking for both HTTP and messaging tests
- [Wolverine Best Practices](/introduction/best-practices) — General guidance on structuring Wolverine applications for testability
