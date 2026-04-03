# Migrating from MVC Filters and Minimal API Endpoint Filters

If you're coming from ASP.NET Core MVC or Minimal APIs, you're used to filters like `IActionFilter`,
`IEndpointFilter`, `IResultFilter`, and friends. Wolverine.HTTP replaces all of these with a single,
convention-based middleware system that is compile-time code generated, requires no interface ceremony,
and gives you the same (and often more) control.

This tutorial maps the filter concepts you already know to their idiomatic Wolverine equivalents.

## The Core Difference

In MVC and Minimal APIs, filters are **runtime pipeline delegates** that wrap your endpoint. You implement
interfaces, register them globally or per-endpoint, and they execute via delegate chains at runtime.

Wolverine takes a fundamentally different approach: middleware is **compiled directly into generated C# code**.
When Wolverine bootstraps, it detects your middleware methods by naming convention and weaves them into the
generated handler source code. The result is:

- **Zero allocation overhead** — no delegate chains or middleware pipeline objects
- **Clean stack traces** — no nested middleware layers obscuring where an error occurred
- **Compile-time validation** — middleware parameter mismatches are caught at startup, not at request time

## Quick Reference

| MVC / Minimal API | Wolverine Equivalent |
|-------------------|---------------------|
| `IActionFilter.OnActionExecuting` | `Before` / `BeforeAsync` method |
| `IEndpointFilter` | `Before` / `BeforeAsync` method |
| `IAuthorizationFilter` | `Before` returning `IResult` (e.g. `Results.Unauthorized()`) |
| `IResourceFilter.OnResourceExecuting` | `Before` with early `IResult` return |
| `IActionFilter.OnActionExecuted` | `After` / `AfterAsync` method |
| `IResultFilter` | `After` / `AfterAsync` method |
| `IExceptionFilter` | `Finally` / `FinallyAsync` method |
| `[TypeFilter]` / `[ServiceFilter]` | `[Middleware(typeof(...))]` attribute |
| Global filters (`MvcOptions.Filters`) | `IHttpPolicy` |
| `AddEndpointFilter()` on route groups | `IHttpPolicy` with namespace/type filtering |
| Filter ordering (`Order` property) | Insertion position in `chain.Middleware` list |

## Before Methods: Replacing IActionFilter and IEndpointFilter

### MVC IActionFilter

In MVC, you'd implement `IActionFilter` to run logic before an action:

```csharp
// MVC approach
public class LoggingFilter : IActionFilter
{
    private readonly ILogger<LoggingFilter> _logger;

    public LoggingFilter(ILogger<LoggingFilter> logger) => _logger = logger;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        _logger.LogInformation("Executing {Action}", context.ActionDescriptor.DisplayName);
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}

// Applied via attribute
[ServiceFilter(typeof(LoggingFilter))]
public IActionResult GetOrder(int id) { ... }
```

### Minimal API IEndpointFilter

In Minimal APIs, you'd use `IEndpointFilter`:

```csharp
// Minimal API approach
public class LoggingFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<LoggingFilter>>();
        logger.LogInformation("Executing endpoint");

        var result = await next(context);
        return result;
    }
}

// Applied to an endpoint
app.MapGet("/orders/{id}", (int id) => ...)
   .AddEndpointFilter<LoggingFilter>();
```

### Wolverine Equivalent

In Wolverine, middleware is just a class with methods following naming conventions. No interfaces:

```csharp
// Wolverine approach — just a class with a Before method
public static class LoggingMiddleware
{
    // Dependencies are injected as method parameters — no constructor needed
    public static void Before(HttpContext context, ILogger logger)
    {
        logger.LogInformation("Executing {Path}", context.Request.Path);
    }
}
```

Apply it to a specific endpoint with `[Middleware]`:

```csharp
[Middleware(typeof(LoggingMiddleware))]
[WolverineGet("/orders/{id}")]
public static Order Get([Entity] Order order) => order;
```

Or put the `Before` method directly on the endpoint class — no separate middleware class needed:

```csharp
public static class GetOrderEndpoint
{
    // This runs before the handler automatically — discovered by naming convention
    public static void Before(HttpContext context, ILogger logger)
    {
        logger.LogInformation("Executing {Path}", context.Request.Path);
    }

    [WolverineGet("/orders/{id}")]
    public static Order Get([Entity] Order order) => order;
}
```

::: tip
All of the following method names are recognized as "before" middleware:
`Before`, `BeforeAsync`, `Load`, `LoadAsync`, `Validate`, `ValidateAsync`
:::

## Short-Circuiting: Replacing IAuthorizationFilter and IResourceFilter

A major use of filters is short-circuiting — stopping the request before the handler runs.
In MVC, you'd set `context.Result` in `OnActionExecuting`. In Minimal APIs, you'd return early
from `IEndpointFilter` without calling `next()`.

### MVC Authorization Filter

```csharp
// MVC approach
public class ApiKeyFilter : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var key)
            || key != "secret")
        {
            context.Result = new UnauthorizedResult();
        }
    }
}
```

### Wolverine Equivalent

In Wolverine, return an `IResult` from a `Before` method. If the return value is anything
other than `WolverineContinue.Result()`, Wolverine writes that result to the response and
stops processing:

```csharp
public static class ApiKeyMiddleware
{
    public static IResult Before(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var key)
            || key != "secret")
        {
            // Returning any IResult other than WolverineContinue stops processing
            return Results.Unauthorized();
        }

        // This tells Wolverine to keep going to the handler
        return WolverineContinue.Result();
    }
}
```

### Using ProblemDetails for Validation

For validation scenarios, you can return `ProblemDetails` instead of `IResult`:

```csharp
public static class CreateOrderEndpoint
{
    // Naming convention: "Validate" is recognized as a Before method
    public static ProblemDetails Validate(CreateOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProductName))
        {
            return new ProblemDetails
            {
                Detail = "Product name is required",
                Status = 400
            };
        }

        // WolverineContinue.NoProblems signals "validation passed, keep going"
        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/orders")]
    public static OrderConfirmation Post(CreateOrderRequest request)
    {
        // Only reached if Validate returned NoProblems
        return new OrderConfirmation(Guid.NewGuid());
    }
}
```

### Using Nullable IResult

You can also use a nullable `IResult` return type — `null` means "continue":

```csharp
public static class AuthMiddleware
{
    // Returning null means "keep going"
    // Returning a non-null IResult means "stop and write this response"
    public static UnauthorizedHttpResult? Before(ClaimsPrincipal user)
    {
        return user.Identity?.IsAuthenticated == true
            ? null
            : TypedResults.Unauthorized();
    }
}
```

## Data Loading: Replacing IResourceFilter

A common MVC pattern is using `IResourceFilter` to load data before model binding:

```csharp
// MVC approach
public class LoadOrderFilter : IResourceFilter
{
    private readonly IOrderRepository _repo;
    public LoadOrderFilter(IOrderRepository repo) => _repo = repo;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var id = (int)context.RouteData.Values["id"]!;
        var order = _repo.GetById(id);
        if (order == null)
        {
            context.Result = new NotFoundResult();
            return;
        }
        context.HttpContext.Items["order"] = order;
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
```

### Wolverine Equivalent

Use a `Load` or `LoadAsync` method. The return value is passed as a parameter to the handler:

```csharp
public static class UpdateOrderEndpoint
{
    // "LoadAsync" is a recognized Before method name
    // Its return value is passed to the handler as a parameter
    public static Task<Order?> LoadAsync(int id, IDocumentSession session)
        => session.LoadAsync<Order>(id);

    [WolverinePut("/orders/{id}")]
    public static IMartenOp Put(
        UpdateOrderRequest request,
        [Required] Order? order)  // [Required] returns 404 automatically if null
    {
        order!.Name = request.Name;
        return MartenOps.Store(order);
    }
}
```

The `[Required]` attribute on a nullable parameter tells Wolverine to return a 404 if the loaded
value is `null` — replacing the manual null check in the MVC filter.

## After Methods: Replacing IResultFilter

### MVC IResultFilter

```csharp
// MVC approach
public class AddHeaderFilter : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        context.HttpContext.Response.Headers["X-Custom"] = "value";
    }
    public void OnResultExecuted(ResultExecutedContext context) { }
}
```

### Wolverine Equivalent

Use an `After` method:

```csharp
public static class CustomHeaderMiddleware
{
    public static void After(HttpContext context)
    {
        context.Response.Headers["X-Custom"] = "value";
    }
}
```

## Finally Methods: Replacing IExceptionFilter

### MVC IExceptionFilter

```csharp
// MVC approach
public class ErrorHandlingFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is NotFoundException)
        {
            context.Result = new NotFoundResult();
            context.ExceptionHandled = true;
        }
    }
}
```

### Wolverine Equivalent

Use a `Finally` method. It runs in a `try/finally` block, guaranteeing execution even when
exceptions occur:

```csharp
public class StopwatchMiddleware
{
    private readonly Stopwatch _stopwatch = new();

    public void Before()
    {
        _stopwatch.Start();
    }

    public void Finally(ILogger logger, HttpContext context)
    {
        _stopwatch.Stop();
        logger.LogDebug("Request to {Path} took {Duration}ms",
            context.Request.Path, _stopwatch.ElapsedMilliseconds);
    }
}
```

::: warning
Note that `Finally` methods do not receive the exception — they are cleanup hooks, not exception
handlers. For exception-to-response mapping, see [issue #2410](https://github.com/JasperFx/wolverine/issues/2410)
which is tracking a dedicated exception handling convention.
:::

## Applying Middleware Per-Endpoint

Wolverine provides several ways to apply middleware to specific endpoints, from most targeted to broadest:

### 1. Inline on the Endpoint Class

Put `Before`/`After`/`Finally` methods directly on the endpoint class. They apply only to that endpoint:

```csharp
public static class SecureOrderEndpoint
{
    // Only applies to this endpoint
    public static IResult Before(ClaimsPrincipal user)
    {
        return user.IsInRole("OrderAdmin")
            ? WolverineContinue.Result()
            : Results.Forbid();
    }

    [WolverinePost("/orders/cancel/{id}")]
    public static void Post(CancelOrder command) { ... }
}
```

### 2. The `[Middleware]` Attribute

Apply a middleware class to a single endpoint method or an entire endpoint class:

```csharp
// On a single method
public static class OrderEndpoints
{
    [Middleware(typeof(ApiKeyMiddleware))]
    [WolverineDelete("/orders/{id}")]
    public static void Delete(int id) { ... }

    // This endpoint does NOT have the middleware
    [WolverineGet("/orders/{id}")]
    public static Order Get([Entity] Order order) => order;
}

// On the entire class — applies to all endpoints in the class
[Middleware(typeof(ApiKeyMiddleware))]
public static class AdminEndpoints
{
    [WolverinePost("/admin/reset")]
    public static void Reset() { ... }

    [WolverinePost("/admin/purge")]
    public static void Purge() { ... }
}
```

You can also apply multiple middleware classes and control scoping:

```csharp
[Middleware(typeof(LoggingMiddleware), typeof(AuthMiddleware))]
[WolverinePost("/orders")]
public static OrderConfirmation Post(CreateOrderRequest request) { ... }
```

### 3. The `Configure(HttpChain)` Method

For programmatic control over a specific endpoint's middleware pipeline, add a static
`Configure` method to the endpoint class:

```csharp
public class TimedEndpoint
{
    public static void Configure(HttpChain chain)
    {
        // Add middleware before the handler
        chain.AddMiddleware<StopwatchMiddleware>(x => x.Before());

        // Add a postprocessor after the handler
        chain.AddPostprocessor<StopwatchMiddleware>(x => x.Finally(null!, null!));

        // You can also manipulate OpenAPI metadata here
        chain.Metadata.Produces(503);
    }

    [WolverineGet("/timed")]
    public static string Get() => "how long did I take?";
}
```

The `AddMiddleware<T>()` and `AddPostprocessor<T>()` extension methods accept a lambda pointing
to the middleware method. You can also use the non-generic overload with a type and method name:

```csharp
chain.AddMiddleware(typeof(StopwatchMiddleware), nameof(StopwatchMiddleware.Before));
```

This gives you full control over ordering — you can also directly manipulate `chain.Middleware`
and `chain.Postprocessors` as lists, using `Insert()` to place middleware at specific positions.

### 4. `IHttpPolicy` for Groups of Endpoints

For applying middleware to multiple endpoints based on criteria (namespace, type, dependencies),
implement `IHttpPolicy`:

```csharp
// Apply audit logging to all endpoints in a specific namespace
public class AuditLoggingPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules,
        IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            // Filter by namespace
            if (chain.Method.HandlerType.IsInNamespace("MyApp.Features.Admin"))
            {
                chain.AddMiddleware<AuditMiddleware>(x => x.Before(null!, null!));
            }
        }
    }
}

// Register the policy
app.MapWolverineEndpoints(opts =>
{
    opts.AddPolicy<AuditLoggingPolicy>();
});
```

This is the Wolverine equivalent of applying `AddEndpointFilter()` to a Minimal API route group
or registering global MVC filters with type-based filtering.

**You can also filter based on service dependencies:**

```csharp
public class TrainerLoadingPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules,
        IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            // Only apply to endpoints that depend on the Trainer type
            var dependencies = chain.ServiceDependencies(container, Type.EmptyTypes);
            if (dependencies.Contains(typeof(Trainer)))
            {
                chain.AddMiddleware<TrainerMiddleware>(x => x.LoadAsync(null!, null!));
            }
        }
    }
}
```

## Middleware with Tuple Returns

A powerful Wolverine pattern with no MVC equivalent: middleware can return **tuples** that
both provide data to the handler and control flow:

```csharp
public static class UserMiddleware
{
    // Returns both a UserId for the handler AND ProblemDetails for short-circuiting
    public static (UserId, ProblemDetails) Load(ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst("sub");
        if (claim != null && Guid.TryParse(claim.Value, out var id))
        {
            return (new UserId(id), WolverineContinue.NoProblems);
        }

        return (new UserId(Guid.Empty), new ProblemDetails
        {
            Detail = "Valid user identity required",
            Status = 401
        });
    }
}
```

The `UserId` value is injected into the handler method, and the `ProblemDetails` controls whether
processing continues. This combines data loading and validation in a single middleware method.

## Global Middleware vs. MVC Global Filters

### MVC Global Filters

```csharp
// MVC approach
builder.Services.AddControllers(options =>
{
    options.Filters.Add<GlobalLoggingFilter>();
    options.Filters.Add<GlobalExceptionFilter>();
});
```

### Wolverine Equivalent

```csharp
app.MapWolverineEndpoints(opts =>
{
    // Apply middleware to ALL Wolverine endpoints
    opts.AddMiddleware(typeof(LoggingMiddleware));

    // Or with a filter predicate
    opts.AddMiddleware(typeof(AuditMiddleware),
        chain => chain.Method.HandlerType.IsInNamespace("MyApp.Admin"));

    // Or use a full policy for complex logic
    opts.AddPolicy<MyCustomPolicy>();
});
```

## Summary: Why Wolverine's Approach is Different

| Concern | MVC / Minimal API | Wolverine |
|---------|-------------------|-----------|
| **Definition** | Implement interfaces (`IActionFilter`, `IEndpointFilter`) | Write methods with conventional names (`Before`, `After`, `Finally`) |
| **DI** | Constructor injection | Method parameter injection — no constructors needed |
| **Registration** | Attributes, global config, or `AddEndpointFilter()` | Inline methods, `[Middleware]`, `Configure(HttpChain)`, or `IHttpPolicy` |
| **Short-circuit** | Set `context.Result` or return early from `next()` | Return `IResult`, `ProblemDetails`, or nullable types |
| **Runtime cost** | Delegate chains with allocations | Compiled directly into generated source code — zero overhead |
| **Data passing** | `HttpContext.Items` dictionary or `context.Arguments` | Method return values automatically injected as handler parameters |
| **Validation** | Runtime errors if filter has wrong dependencies | Startup errors if middleware parameters can't be resolved |

The key insight is that Wolverine middleware is **just methods** — no interfaces, no base classes,
no ceremony. The framework discovers them by naming convention and compiles them directly into the
request handling code.

## One-Off Middleware with [WolverineBefore] / [WolverineAfter]

If you have a one-off cross-cutting concern that doesn't warrant a separate middleware class — the
equivalent of slapping a single `IActionFilter` on one controller action — consider using Wolverine's
[Railway Programming](/tutorials/railway-programming) patterns instead. The `Before`, `Validate`, and
`Load` methods directly on your endpoint class serve the same purpose with less ceremony than creating
a dedicated middleware type.

For reusable middleware that lives in a separate class and needs to be discoverable across endpoints,
Wolverine provides `[WolverineBefore]` and `[WolverineAfter]` attributes. See the
[Wolverine.HTTP Middleware](/guide/http/middleware) reference for details on these and other
advanced middleware patterns.

## Further Reading

- [Wolverine.HTTP Middleware](/guide/http/middleware) — full reference documentation
- [Wolverine.HTTP Policies](/guide/http/policies) — `IHttpPolicy` reference
- [Handler Middleware](/guide/handlers/middleware) — middleware for message handlers (same conventions)
- [Railway Programming with Wolverine](/tutorials/railway-programming) — validation and data loading patterns
