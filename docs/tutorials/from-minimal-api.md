# Migrating from Minimal APIs to Wolverine.HTTP

This tutorial provides side-by-side conversions between ASP.NET Core Minimal API endpoints and
their Wolverine.HTTP equivalents. If you're already comfortable with Minimal APIs, this will
get you productive with Wolverine.HTTP quickly.

::: tip
For filter and middleware migration specifically, see
[Migrating from MVC/Minimal API Filters](/tutorials/middleware-migration).
:::

## Basic GET Endpoint

### Minimal API

```csharp
app.MapGet("/api/orders/{id}", async (int id, IOrderRepository repo) =>
{
    var order = await repo.GetByIdAsync(id);
    return order is not null ? Results.Ok(order) : Results.NotFound();
});
```

### Wolverine

```csharp
public static class GetOrderEndpoint
{
    [WolverineGet("/api/orders/{id}")]
    public static async Task<IResult> Get(int id, IOrderRepository repo)
    {
        var order = await repo.GetByIdAsync(id);
        return order is not null ? Results.Ok(order) : Results.NotFound();
    }
}
```

Or more idiomatically with Wolverine's `[Entity]` attribute, which loads the entity and returns 404 automatically:

```csharp
public static class GetOrderEndpoint
{
    [WolverineGet("/api/orders/{id}")]
    public static Order Get([Entity] Order order) => order;
}
```

**Key differences:**
- Wolverine endpoints are methods on public classes, decorated with `[WolverineGet]` etc.
- Route parameters, query strings, and services are bound the same way — by parameter name and type
- `IResult` return type works identically
- Wolverine adds `[Entity]` for automatic persistence loading with 404 handling

## Basic POST Endpoint

### Minimal API

```csharp
app.MapPost("/api/orders", async (CreateOrder command, IOrderRepository repo) =>
{
    var order = new Order { Id = Guid.NewGuid(), ProductName = command.ProductName };
    await repo.SaveAsync(order);
    return Results.Created($"/api/orders/{order.Id}", order);
});
```

### Wolverine

```csharp
public static class CreateOrderEndpoint
{
    [WolverinePost("/api/orders")]
    public static async Task<CreationResponse> Post(
        CreateOrder command, IOrderRepository repo)
    {
        var order = new Order { Id = Guid.NewGuid(), ProductName = command.ProductName };
        await repo.SaveAsync(order);

        // CreationResponse sets 201 status and Location header automatically
        return new CreationResponse($"/api/orders/{order.Id}");
    }
}
```

**Key differences:**
- The first complex type parameter is automatically deserialized from the JSON body (same as Minimal API)
- `CreationResponse` is a built-in Wolverine type that sets the 201 status code and `Location` header
- `AcceptResponse` works similarly for 202 Accepted

## Parameter Binding

Parameter binding works very similarly between the two frameworks. Here's a comprehensive comparison:

### Minimal API

```csharp
app.MapGet("/api/orders", (
    [FromQuery] int page,
    [FromQuery] int pageSize,
    [FromHeader(Name = "X-Tenant")] string tenant,
    [FromServices] IOrderRepository repo,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    // ...
});
```

### Wolverine

```csharp
[WolverineGet("/api/orders")]
public static Task<IEnumerable<Order>> Get(
    int page,                                  // query string (inferred for simple types)
    int pageSize,                              // query string (inferred)
    [FromHeader(Name = "X-Tenant")] string tenant,  // header — same attribute
    IOrderRepository repo,                     // IoC service (inferred, no attribute needed)
    ClaimsPrincipal user,                      // injected automatically
    CancellationToken ct)                      // injected automatically
{
    // ...
}
```

**Key differences:**
- Wolverine infers query string binding for simple types without `[FromQuery]`
- Wolverine infers service injection without `[FromServices]` (though the attribute still works)
- `HttpContext`, `HttpRequest`, `HttpResponse`, `ClaimsPrincipal`, and `CancellationToken` are all injected automatically in both frameworks

### Complex Query Objects

**Minimal API (.NET 7+):**

```csharp
app.MapGet("/api/orders", ([AsParameters] OrderQuery query, IOrderRepository repo) => { ... });
```

**Wolverine (v3.12+):**

```csharp
[WolverineGet("/api/orders")]
public static Task<IEnumerable<Order>> Get(
    [FromQuery] OrderQuery query, IOrderRepository repo)
{
    // query.Page, query.PageSize, etc. bound from query string
}
```

## Form Data and File Uploads

### Minimal API

```csharp
app.MapPost("/api/upload", async ([FromForm] string description, IFormFile file) =>
{
    using var stream = file.OpenReadStream();
    // process file...
    return Results.Ok(new { file.FileName, description });
});
```

### Wolverine

```csharp
[WolverinePost("/api/upload")]
public static async Task<object> Post(
    [FromForm] string description, IFormFile file)
{
    using var stream = file.OpenReadStream();
    // process file...
    return new { file.FileName, description };
}
```

File upload binding is identical — `IFormFile` and `IFormFileCollection` work the same way.

## Authentication and Authorization

### Minimal API

```csharp
app.MapGet("/api/admin/users", () => { ... })
   .RequireAuthorization("AdminPolicy");

app.MapGet("/api/public/health", () => "ok")
   .AllowAnonymous();
```

### Wolverine

```csharp
[Authorize(Policy = "AdminPolicy")]
[WolverineGet("/api/admin/users")]
public static IEnumerable<User> Get(IUserRepository repo) { ... }

[AllowAnonymous]
[WolverineGet("/api/public/health")]
public static string Get() => "ok";
```

To require authorization on all Wolverine endpoints globally:

```csharp
app.MapWolverineEndpoints(opts =>
{
    opts.RequireAuthorizeOnAll();
});
```

## Route Groups → IHttpPolicy

### Minimal API

```csharp
var api = app.MapGroup("/api")
    .RequireAuthorization()
    .AddEndpointFilter<LoggingFilter>();

var orders = api.MapGroup("/orders");
orders.MapGet("/", GetOrders.Handle);
orders.MapPost("/", CreateOrder.Handle);
```

### Wolverine

Wolverine doesn't have route groups. Instead, use `IHttpPolicy` to apply cross-cutting concerns
to groups of endpoints by namespace, type, or any other criteria:

```csharp
app.MapWolverineEndpoints(opts =>
{
    // Apply middleware to endpoints in a namespace
    opts.AddMiddleware(typeof(LoggingMiddleware),
        chain => chain.Method.HandlerType.IsInNamespace("MyApp.Features.Orders"));
});
```

See [Route Prefix Groups](https://github.com/JasperFx/wolverine/issues/2405) for future
route prefix support.

## Endpoint Filters → Before/After Methods

### Minimal API

```csharp
app.MapPost("/api/orders", CreateOrder.Handle)
   .AddEndpointFilter(async (context, next) =>
   {
       var command = context.GetArgument<CreateOrderCommand>(0);
       if (string.IsNullOrWhiteSpace(command.ProductName))
           return Results.BadRequest("Product name is required");

       return await next(context);
   });
```

### Wolverine

```csharp
public static class CreateOrderEndpoint
{
    // "Validate" is a recognized Before method — runs before the handler
    public static ProblemDetails Validate(CreateOrderCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ProductName))
            return new ProblemDetails
            {
                Detail = "Product name is required",
                Status = 400
            };

        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/api/orders")]
    public static OrderConfirmation Post(CreateOrderCommand command) { ... }
}
```

For a comprehensive filter migration guide, see
[Migrating from MVC/Minimal API Filters](/tutorials/middleware-migration).

## Publishing Messages (The Wolverine Superpower)

This is where Wolverine fundamentally differs from Minimal APIs — endpoints can trigger
asynchronous messaging with transactional guarantees.

### Minimal API (Manual)

```csharp
app.MapPost("/api/orders", async (
    CreateOrder command,
    IOrderRepository repo,
    IMessageBroker broker) =>
{
    var order = new Order { ... };
    await repo.SaveAsync(order);
    // Manual, non-transactional — if this fails, order is saved but event is lost
    await broker.PublishAsync(new OrderCreated(order.Id));
    return Results.Created($"/api/orders/{order.Id}", order);
});
```

### Wolverine (Cascading Messages)

```csharp
public static class CreateOrderEndpoint
{
    [WolverinePost("/api/orders")]
    public static (CreationResponse, OrderCreated) Post(
        CreateOrder command, IDocumentSession session)
    {
        var order = new Order { Id = Guid.NewGuid(), ProductName = command.ProductName };
        session.Store(order);

        // First tuple item = HTTP response (201 Created)
        // Second tuple item = cascading message, sent transactionally via outbox
        return (
            new CreationResponse($"/api/orders/{order.Id}"),
            new OrderCreated(order.Id)
        );
    }
}
```

**Key differences:**
- Tuple return values cascade messages through Wolverine's messaging pipeline
- With transactional middleware, the message is sent via the outbox — guaranteed delivery
- No explicit `IMessageBus` injection needed for cascading
- Multiple cascading messages via additional tuple items or `OutgoingMessages`

### Multiple Cascading Messages

```csharp
[WolverinePost("/api/orders")]
public static (CreationResponse, OutgoingMessages) Post(CreateOrder command)
{
    var order = new Order { ... };
    var messages = new OutgoingMessages
    {
        new OrderCreated(order.Id),
        new NotifyWarehouse(order.Id),
        new SendConfirmationEmail(order.CustomerEmail)
    };
    return (new CreationResponse($"/api/orders/{order.Id}"), messages);
}
```

## Delegate-to-Wolverine Shortcut

If you want the absolute minimum conversion from Minimal API, Wolverine provides shortcut
methods that wire a Minimal API route directly to a Wolverine message handler:

```csharp
// Instead of: app.MapPost("/orders", (CreateOrder cmd, IMessageBus bus) => bus.InvokeAsync(cmd));
app.MapPostToWolverine<CreateOrder>("/orders");

// With a response type:
app.MapPostToWolverine<CreateOrder, OrderConfirmation>("/orders");

// Also available for PUT and DELETE:
app.MapPutToWolverine<UpdateOrder>("/orders");
app.MapDeleteToWolverine<DeleteOrder>("/orders/{id}");
```

These are optimized Minimal API endpoints that delegate to Wolverine's handler pipeline —
a quick way to integrate Wolverine into an existing Minimal API application without rewriting
endpoints.

## OpenAPI Metadata

### Minimal API

```csharp
app.MapGet("/api/orders/{id}", GetOrder.Handle)
   .WithTags("Orders")
   .WithDescription("Get an order by ID")
   .Produces<Order>(200)
   .Produces(404)
   .WithOpenApi();
```

### Wolverine

```csharp
[Tags("Orders")]
[WolverineGet("/api/orders/{id}", OperationId = "GetOrder")]
[ProducesResponseType(typeof(Order), 200)]
[ProducesResponseType(404)]
public static Order Get([Entity] Order order) => order;
```

Wolverine also generates sensible OpenAPI defaults from the method signature — JSON content types,
200/404/500 status codes, and parameter metadata are inferred automatically.

## Registration

### Minimal API

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/orders", GetOrders.Handle);
app.MapPost("/orders", CreateOrder.Handle);
// ... register each endpoint manually
```

### Wolverine

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWolverine();

var app = builder.Build();
app.MapWolverineEndpoints();
// All endpoints discovered automatically from [WolverineGet], [WolverinePost], etc.
```

Wolverine discovers endpoints by scanning assemblies for methods decorated with Wolverine
route attributes. No manual registration needed.

## Summary

| Concept | Minimal API | Wolverine.HTTP |
|---------|------------|----------------|
| **Declaration** | `app.MapGet("/path", handler)` | `[WolverineGet("/path")]` on a method |
| **Body binding** | First complex param (inferred) | First complex param (inferred) |
| **Query binding** | `[FromQuery]` or inferred | Inferred for simple types |
| **Service injection** | `[FromServices]` or inferred | Inferred (attribute optional) |
| **Header binding** | `[FromHeader]` | `[FromHeader]` |
| **Authorization** | `.RequireAuthorization()` | `[Authorize]` |
| **Validation** | `IEndpointFilter` | `Validate` method or FluentValidation |
| **Route groups** | `app.MapGroup()` | `IHttpPolicy` with namespace filtering |
| **201 Created** | `Results.Created(url, body)` | Return `CreationResponse` |
| **Cascading messages** | Manual via message broker | Tuple returns or `OutgoingMessages` |
| **Registration** | Manual per-endpoint | Automatic assembly scanning |

## Further Reading

- [Wolverine.HTTP Endpoints](/guide/http/endpoints) — full endpoint reference
- [Publishing Messages from HTTP](/guide/http/messaging) — cascading messages guide
- [Migrating from MVC/Minimal API Filters](/tutorials/middleware-migration) — filter migration
- [Railway Programming](/tutorials/railway-programming) — validation and loading patterns
