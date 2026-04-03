# HTTP Messaging Transport

Wolverine includes a built-in HTTP transport that allows you to use standard HTTP as an asynchronous messaging transport
between applications. This is particularly useful when:

- Your infrastructure only allows HTTP traffic (no dedicated message broker available)
- You want to use **asynchronous messaging patterns** between services that are already HTTP-based
- You need the benefits of Wolverine's messaging features (error resiliency, retries, circuit breaking, durable outbox)
  without deploying a separate message broker like RabbitMQ or Kafka
- You are building a **modular monolith** or microservice architecture where services communicate over HTTP

::: tip
The HTTP transport is a full Wolverine messaging transport. All of Wolverine's messaging capabilities are supported,
including error handling policies, retry logic, circuit breaking, Open Telemetry tracing, and even **durable message
persistence** through the transactional outbox. Messages are not simply fire-and-forget HTTP calls — they go through
the same pipeline as any other Wolverine transport.
:::

## Getting Started

The HTTP transport is included in the `WolverineFx.Http` NuGet package. You need two things:

1. **A receiving application** that maps the Wolverine HTTP transport endpoints
2. **A sending application** that publishes messages to the receiver's URL

### Receiving Application

In the application that will receive and process messages, map the Wolverine HTTP transport endpoints
in your `Program.cs`:

```cs
app.MapWolverineHttpTransportEndpoints();
```

This exposes two Minimal API endpoints:

| Endpoint | Purpose |
|----------|---------|
| `/_wolverine/batch/{queue}` | Receives batches of envelopes (default sending mode) |
| `/_wolverine/invoke` | Receives a single envelope for inline invocation |

### Sending Application

In the application that sends messages, configure publishing rules to target the receiver's URL:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PublishAllMessages()
            .ToHttpEndpoint("https://order-service/api");
    })
    .StartAsync();
```

## Security and Authentication

Since the HTTP transport uses standard ASP.NET Core Minimal API endpoints, you can use **all of ASP.NET Core's
built-in security features** — the same authentication and authorization middleware you already know.

### Securing the Receiver (Listener)

Apply authorization rules to the transport endpoints using ASP.NET Core's fluent API:

```cs
// Require authentication on all Wolverine HTTP transport endpoints
app.MapWolverineHttpTransportEndpoints()
    .RequireAuthorization();

// Or apply a specific authorization policy
app.MapWolverineHttpTransportEndpoints()
    .RequireAuthorization("WolverineTransportPolicy");
```

You can define custom authorization policies as you normally would in ASP.NET Core:

```cs
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("WolverineTransportPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "messaging");
    });
});

builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.Authority = "https://your-identity-server";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false
        };
    });
```

### Authenticating the Sender (HttpClient)

On the sending side, configure the `HttpClient` with authentication headers using ASP.NET Core's
standard `IHttpClientFactory` pattern. Wolverine uses named `HttpClient` instances based on the
destination URL:

```cs
var targetUrl = "https://order-service/api";

builder.UseWolverine(opts =>
{
    opts.PublishAllMessages().ToHttpEndpoint(targetUrl);
});

// Configure the HttpClient that Wolverine will use to send messages
builder.Services.AddHttpClient(targetUrl, client =>
{
    client.BaseAddress = new Uri(targetUrl);
    client.DefaultRequestHeaders.Add("Authorization", "Bearer your-token-here");
});
```

For more advanced scenarios like token refresh, you can use `DelegatingHandler` middleware
on the `HttpClient`:

```cs
builder.Services.AddTransient<AuthTokenHandler>();

builder.Services.AddHttpClient(targetUrl, client =>
{
    client.BaseAddress = new Uri(targetUrl);
})
.AddHttpMessageHandler<AuthTokenHandler>();
```

You can also implement a custom `IWolverineHttpTransportClient` for complete control over
how messages are sent:

```cs
// Register your custom implementation
builder.Services.AddScoped<IWolverineHttpTransportClient, MyCustomTransportClient>();
```

## Sending Modes

The HTTP transport supports multiple sending modes:

```cs
opts.PublishAllMessages().ToHttpEndpoint(url)
    .SendInline();          // Send each message individually (synchronous)

opts.PublishAllMessages().ToHttpEndpoint(url)
    .BufferedInMemory();    // Batch messages in memory (default)

opts.PublishAllMessages().ToHttpEndpoint(url)
    .UseDurableOutbox();    // Persist to durable outbox before sending
```

### Durable Outbox

When using `.UseDurableOutbox()`, messages are first persisted to your configured message store
(PostgreSQL, SQL Server, etc.) and then delivered in the background. This guarantees that messages
are never lost, even if the sending application crashes or the receiver is temporarily unavailable.

## CloudEvents Support

The HTTP transport supports the [CloudEvents](https://cloudevents.io/) specification for
interoperability with non-Wolverine systems:

```cs
opts.PublishAllMessages()
    .ToHttpEndpoint(url, useCloudEvents: true);
```

## Error Handling and Resilience

All of Wolverine's error handling policies apply to messages received through the HTTP transport.
You can configure retry policies, circuit breakers, and dead letter queues just as you would with
any other transport:

```cs
opts.PublishAllMessages().ToHttpEndpoint(url)
    .CircuitBreaking(cb =>
    {
        cb.MinimumThreshold = 10;
        cb.PauseTime = TimeSpan.FromSeconds(30);
        cb.TrackingPeriod = TimeSpan.FromMinutes(1);
        cb.FailurePercentageThreshold = 20;
    });
```

## Open Telemetry

The HTTP transport participates in Wolverine's Open Telemetry tracing. Sent and received messages
are tracked as spans, and correlation IDs are propagated across service boundaries just like with
any other Wolverine transport. No additional configuration is needed — if you have Open Telemetry
configured in your application, HTTP transport messages will appear in your traces automatically.

## Complete Example

Here is a complete example of two services communicating over the HTTP transport:

### Order Service (Receiver)

```cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("InternalServices", policy =>
        policy.RequireClaim("scope", "internal"));
});

builder.UseWolverine();
builder.Services.AddWolverineHttp();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// Secure the transport endpoints
app.MapWolverineHttpTransportEndpoints()
    .RequireAuthorization("InternalServices");

app.Run();

// Handler processes messages received via HTTP transport
public static class PlaceOrderHandler
{
    public static void Handle(PlaceOrder command)
    {
        // Process the order
    }
}
```

### API Gateway (Sender)

```cs
var builder = WebApplication.CreateBuilder(args);

var orderServiceUrl = "https://order-service";

builder.UseWolverine(opts =>
{
    opts.PublishMessage<PlaceOrder>()
        .ToHttpEndpoint(orderServiceUrl)
        .UseDurableOutbox();
});

// Configure authenticated HttpClient for the order service
builder.Services.AddHttpClient(orderServiceUrl, client =>
{
    client.BaseAddress = new Uri(orderServiceUrl);
    client.DefaultRequestHeaders.Add("Authorization", "Bearer service-token");
});

builder.Services.AddScoped<IWolverineHttpTransportClient, WolverineHttpTransportClient>();

var app = builder.Build();
app.Run();
```

### Shared Message Contract

```cs
public record PlaceOrder(Guid OrderId, string CustomerId, decimal Total);
```

## Cross-Site and WAN Messaging

::: tip
If you're coming from NServiceBus or Rebus, you may know this pattern as a "WAN Gateway" or "HTTP Gateway."
Wolverine's HTTP transport covers the same use case — durable, reliable messaging across sites separated
by firewalls, WANs, or network boundaries where a shared message broker isn't available.
:::

### The Problem

In many enterprise environments, you have services deployed across different sites, data centers, or
cloud regions that cannot share a single message broker. Firewalls may only allow HTTP/HTTPS traffic
between sites. You still want the benefits of asynchronous messaging — decoupled services, retry
policies, guaranteed delivery — but without requiring every site to connect to the same RabbitMQ
cluster or Kafka instance.

Other frameworks solve this with a dedicated gateway component:

- **NServiceBus Gateway** provides HTTP(S)-based fire-and-forget messaging across sites with hash
  verification and message deduplication
- **Rebus HTTP Gateway** bridges REST endpoints and messaging for external integration

Wolverine takes a simpler approach: the HTTP transport **is** the gateway. There's no separate
component to deploy or configure — you use the same `ToHttpEndpoint()` API shown above, and all of
Wolverine's messaging infrastructure works automatically.

### How Wolverine Covers the WAN Gateway Pattern

| Capability | NServiceBus Gateway | Wolverine HTTP Transport |
|-----------|---------------------|--------------------------|
| **Transport protocol** | HTTP(S) | HTTP(S) via ASP.NET Core |
| **Durability** | Dedicated gateway storage | Wolverine's durable outbox (PostgreSQL, SQL Server, etc.) |
| **Deduplication** | Hash-based message deduplication | Built-in [idempotency checks](/tutorials/idempotency) via message ID tracking in the durable inbox |
| **Retry and resilience** | Gateway-specific retry | Full Wolverine error handling policies, circuit breakers, requeue |
| **Authentication** | Custom HTTP headers | Standard ASP.NET Core authentication and authorization middleware |
| **Serialization** | Gateway-specific binary | Wolverine binary format or [CloudEvents](https://cloudevents.io/) JSON for interoperability |
| **Observability** | NServiceBus metrics | Full Open Telemetry tracing and metrics |
| **Deployment** | Separate gateway process per site | No separate process — endpoints are hosted in your existing ASP.NET Core application |
| **Batching** | Single messages | Configurable batch sending for throughput |

### Setting Up Cross-Site Messaging

A typical cross-site topology has a sender in one site publishing messages to a receiver in another
site over HTTPS:

```
┌─────────────────┐         HTTPS          ┌─────────────────┐
│   Site A         │ ───────────────────▸  │   Site B         │
│                  │                        │                  │
│  Sender App      │   /_wolverine/batch/   │  Receiver App    │
│  (durable outbox)│   /_wolverine/invoke   │  (handlers)      │
└─────────────────┘                        └─────────────────┘
```

**Site A (Sender)** — uses the durable outbox to guarantee delivery:

```cs
builder.UseWolverine(opts =>
{
    // All messages to Site B go through the HTTP transport
    // with durable outbox persistence
    opts.PublishAllMessages()
        .ToHttpEndpoint("https://site-b.example.com")
        .UseDurableOutbox();
});
```

**Site B (Receiver)** — exposes the transport endpoints with authentication:

```cs
app.MapWolverineHttpTransportEndpoints()
    .RequireAuthorization("CrossSitePolicy");
```

### Guaranteed Delivery

The combination of the durable outbox on the sender and Wolverine's message handling pipeline on the
receiver provides end-to-end guaranteed delivery:

1. **Sender side**: When using `UseDurableOutbox()`, messages are persisted to the sender's database
   before any HTTP call is attempted. If the sender crashes, the message is still in the outbox and
   will be retried when the application restarts. The durable outbox uses the same transactional
   outbox pattern as any other Wolverine transport.

2. **Receiver side**: Once the receiver's `/_wolverine/batch/{queue}` endpoint accepts a batch, the
   messages are queued into Wolverine's local processing pipeline. If a handler fails, Wolverine's
   error handling policies (retry, requeue, scheduled retry, dead letter queue) apply just as they
   would for messages received from RabbitMQ or any other transport.

3. **Deduplication**: When using durable persistence, Wolverine tracks incoming message IDs in the
   durable inbox. If the same message is delivered twice (e.g., due to a network retry), the
   duplicate is detected and discarded. See the [idempotency tutorial](/tutorials/idempotency) for
   details on configuring idempotency behavior.

### Circuit Breaking for Unreliable Links

WAN links between sites are inherently less reliable than local networks. Use Wolverine's circuit
breaker to pause sending when the remote site is down, preventing a backlog of failed HTTP calls:

```cs
opts.PublishAllMessages()
    .ToHttpEndpoint("https://site-b.example.com")
    .UseDurableOutbox()
    .CircuitBreaking(cb =>
    {
        cb.MinimumThreshold = 5;
        cb.PauseTime = TimeSpan.FromMinutes(1);
        cb.TrackingPeriod = TimeSpan.FromMinutes(5);
        cb.FailurePercentageThreshold = 50;
    });
```

When the circuit breaker trips, messages accumulate safely in the durable outbox. Once the remote
site recovers and the circuit breaker resets, the queued messages are delivered automatically.

### Interoperability with Non-Wolverine Systems

If the remote site runs a non-Wolverine application, use CloudEvents mode for a standard JSON
wire format:

```cs
opts.PublishAllMessages()
    .ToHttpEndpoint("https://partner-api.example.com",
        useCloudEvents: true);
```

The receiver can be any HTTP endpoint that accepts
[CloudEvents](https://cloudevents.io/) JSON — a .NET Minimal API, a Java service, a Go application,
or anything else that speaks HTTP. See the [interoperability tutorial](/tutorials/interop) for more
details on messaging with non-Wolverine systems.
