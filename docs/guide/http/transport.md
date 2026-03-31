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
