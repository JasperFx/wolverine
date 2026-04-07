# Migrating from MVC Controllers to Wolverine.HTTP

This tutorial provides side-by-side conversions between ASP.NET Core MVC/Web API controllers and
their Wolverine.HTTP equivalents. If you've been building APIs with `ControllerBase` and
`[ApiController]`, this will show you how each pattern maps to Wolverine.

::: tip
For filter and middleware migration specifically, see
[Migrating from MVC/Minimal API Filters](/tutorials/middleware-migration).
:::

## Basic CRUD Controller

### MVC Controller

```csharp
[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly IOrderRepository _repo;

    public OrdersController(IOrderRepository repo) => _repo = repo;

    [HttpGet("{id}")]
    public async Task<ActionResult<Order>> GetById(int id)
    {
        var order = await _repo.GetByIdAsync(id);
        if (order == null) return NotFound();
        return Ok(order);
    }

    [HttpPost]
    public async Task<ActionResult<Order>> Create(CreateOrderRequest request)
    {
        var order = new Order { ProductName = request.ProductName };
        await _repo.SaveAsync(order);
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UpdateOrderRequest request)
    {
        var order = await _repo.GetByIdAsync(id);
        if (order == null) return NotFound();
        order.ProductName = request.ProductName;
        await _repo.SaveAsync(order);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var order = await _repo.GetByIdAsync(id);
        if (order == null) return NotFound();
        await _repo.DeleteAsync(id);
        return NoContent();
    }
}
```

### Wolverine Endpoints

In Wolverine, there is no controller base class. Each endpoint is a plain method on a plain class.
You can organize endpoints however you like — one class per endpoint, or group related endpoints
in a single class:

```csharp
public static class OrderEndpoints
{
    [WolverineGet("/api/orders/{id}")]
    public static Order GetById([Entity] Order order) => order;

    [WolverinePost("/api/orders")]
    public static async Task<CreationResponse> Create(
        CreateOrderRequest request, IOrderRepository repo)
    {
        var order = new Order { ProductName = request.ProductName };
        await repo.SaveAsync(order);
        return new CreationResponse($"/api/orders/{order.Id}");
    }

    [WolverineDelete("/api/orders/{id}")]
    public static Task Delete(int id, IOrderRepository repo)
    {
        return repo.DeleteAsync(id);
        // this will set a 204 HTTP status code
    }
}

public static class UpdateOrderEndpoint
{
    // This could be further reduced by using the [Entity] attribute
    // if you'll also drop the custom repository wrappers:)
    public static async Task<Order?> LoadAsync(int id, IOrderRepository repo)
        => await repo.GetByIdAsync(id);

    [WolverinePut("/api/orders/{id}")]
    public static async Task<int> Put(
        UpdateOrderRequest request,
        [Required] Order? order,
        IOrderRepository repo)
    {
        order!.ProductName = request.ProductName;
        await repo.SaveAsync(order);
        return 204;
    }
}
```

**Key differences:**
- No `ControllerBase` inheritance, no constructor injection — services are method parameters
- `[Entity]` handles loading + 404 in one shot (replaces the manual load-and-check pattern)
- `Load`/`LoadAsync` methods on the class replace the "fetch then check null" boilerplate
- `[Required]` on a nullable parameter returns 404 automatically if the loaded value is null
- `CreationResponse` replaces `CreatedAtAction()` — sets 201 + Location header
- Returning `int` sets the HTTP status code (e.g. `return 204;`)

## Dependency Injection

### MVC (Constructor Injection)

```csharp
[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IProductService _products;
    private readonly ILogger<ProductsController> _logger;
    private readonly IValidator<CreateProduct> _validator;

    public ProductsController(
        IProductService products,
        ILogger<ProductsController> logger,
        IValidator<CreateProduct> validator)
    {
        _products = products;
        _logger = logger;
        _validator = validator;
    }

    [HttpPost]
    public async Task<ActionResult<Product>> Create(CreateProduct command)
    {
        var result = await _validator.ValidateAsync(command);
        if (!result.IsValid) return BadRequest(result.Errors);

        var product = await _products.CreateAsync(command);
        _logger.LogInformation("Created product {Id}", product.Id);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, product);
    }
}
```

### Wolverine (Method Parameter Injection)

```csharp
public static class CreateProductEndpoint
{
    [WolverinePost("/api/products")]
    public static async Task<CreationResponse> Post(
        CreateProduct command,       // deserialized from request body
        IProductService products,    // injected from IoC
        ILogger logger)              // injected from IoC
    {
        var product = await products.CreateAsync(command);
        logger.LogInformation("Created product {Id}", product.Id);
        return new CreationResponse($"/api/products/{product.Id}");
    }
}
```

**Key differences:**
- No constructor — each method declares exactly the dependencies it needs
- No field assignments, no `_` prefix convention
- Validation is handled via FluentValidation middleware or `Validate` methods, not manually
- Services are resolved per-method, not per-controller-instance

## Model Binding

### MVC

```csharp
[HttpGet]
public IActionResult Search(
    [FromQuery] string? name,
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 20,
    [FromHeader(Name = "X-Correlation-Id")] string? correlationId,
    [FromRoute] string category)
{
    // ...
}
```

### Wolverine

```csharp
[WolverineGet("/api/products/{category}")]
public static IEnumerable<Product> Search(
    string category,                              // route parameter (matched by name)
    string? name,                                 // query string (inferred for simple types)
    int page,                                     // query string (default is 0, not configurable inline)
    int pageSize,                                 // query string
    [FromHeader(Name = "X-Correlation-Id")] string? correlationId)
{
    // ...
}
```

**Binding rules comparison:**

| Source | MVC | Wolverine |
|--------|-----|-----------|
| Route | `[FromRoute]` or inferred | Inferred by parameter name matching route template |
| Query string | `[FromQuery]` or inferred for simple types | Inferred for simple types |
| Body | `[FromBody]` (or inferred with `[ApiController]`) | First complex type parameter (inferred) |
| Header | `[FromHeader]` | `[FromHeader]` |
| Service | `[FromServices]` | Inferred (no attribute needed) |
| Form | `[FromForm]` | `[FromForm]` |

## Request Body and [ApiController]

### MVC with [ApiController]

```csharp
[ApiController]  // enables automatic model validation and body binding
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    [HttpPost]
    public ActionResult<Order> Create(CreateOrder command)
    {
        // [ApiController] automatically:
        // 1. Binds 'command' from request body
        // 2. Validates with DataAnnotations
        // 3. Returns 400 if ModelState is invalid
    }
}
```

### Wolverine

```csharp
public static class CreateOrderEndpoint
{
    [WolverinePost("/api/orders")]
    public static Order Post(CreateOrder command)
    {
        // Wolverine automatically deserializes 'command' from JSON body
        // (first complex type parameter is always the body)
    }
}
```

For automatic validation, add FluentValidation or DataAnnotations middleware:

```csharp
// In startup
app.MapWolverineEndpoints(opts =>
{
    opts.UseFluentValidationProblemDetailMiddleware();
    // or
    opts.UseDataAnnotationsValidationProblemDetailMiddleware();
});
```

## `ActionResult<T>` → Return Types

MVC uses `ActionResult<T>` extensively. Wolverine uses simpler return types:

### MVC

```csharp
[HttpGet("{id}")]
public async Task<ActionResult<Order>> Get(int id)
{
    var order = await _repo.FindAsync(id);
    if (order == null) return NotFound();
    return Ok(order);
}

[HttpPost]
public async Task<ActionResult<Order>> Create(CreateOrder command)
{
    var order = await _service.CreateAsync(command);
    return CreatedAtAction(nameof(Get), new { id = order.Id }, order);
}

[HttpDelete("{id}")]
public async Task<IActionResult> Delete(int id)
{
    await _repo.DeleteAsync(id);
    return NoContent();
}
```

### Wolverine

```csharp
// Simple GET — return the object directly, Wolverine serializes to JSON (200)
[WolverineGet("/api/orders/{id}")]
public static Order Get([Entity] Order order) => order;

// POST with 201 — use CreationResponse
[WolverinePost("/api/orders")]
public static CreationResponse Create(CreateOrder command)
{
    var order = /* create */;
    return new CreationResponse($"/api/orders/{order.Id}");
}

// DELETE with 204 — return status code as int
[WolverineDelete("/api/orders/{id}")]
public static int Delete(int id)
{
    /* delete */
    return 204;
}

// Need full control? IResult works too
[WolverineGet("/api/orders/{id}/details")]
public static async Task<IResult> GetDetails(int id, IOrderRepository repo)
{
    var order = await repo.FindAsync(id);
    return order is not null ? Results.Ok(order) : Results.NotFound();
}
```

**Return type mapping:**

| MVC | Wolverine |
|-----|-----------|
| `Ok(value)` | Return the value directly |
| `NotFound()` | Use `[Entity]` (auto 404) or return `Results.NotFound()` |
| `CreatedAtAction(...)` | Return `CreationResponse(url)` |
| `Accepted(...)` | Return `AcceptResponse(url)` |
| `NoContent()` | Return `204` (int) |
| `BadRequest(...)` | Return `ProblemDetails` from a `Validate` method |
| `StatusCode(n)` | Return `n` (int) |
| Any `IActionResult` | Return `IResult` (Minimal API result type) |

## ModelState Validation → Validate Methods

### MVC

```csharp
[HttpPost]
public ActionResult<Order> Create(CreateOrder command)
{
    if (!ModelState.IsValid)
        return BadRequest(ModelState);

    // Or with [ApiController], this check happens automatically
}
```

### Wolverine

```csharp
public static class CreateOrderEndpoint
{
    public static ProblemDetails Validate(CreateOrder command)
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
    public static Order Post(CreateOrder command) { ... }
}
```

Or use FluentValidation for automatic validation (closest to `[ApiController]` behavior):

```csharp
// Register once at startup
app.MapWolverineEndpoints(opts =>
{
    opts.UseFluentValidationProblemDetailMiddleware();
});

// Validator class — discovered and applied automatically
public class CreateOrderValidator : AbstractValidator<CreateOrder>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.ProductName).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}

// Endpoint — no validation code needed, FluentValidation runs before the handler
[WolverinePost("/api/orders")]
public static Order Post(CreateOrder command) { ... }
```

## Controller Filters → Wolverine Middleware

### MVC (Attribute Filters on Controller)

```csharp
[Authorize]
[ServiceFilter(typeof(AuditFilter))]
[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    [HttpPost("reset")]
    public IActionResult Reset() { ... }

    [HttpPost("purge")]
    public IActionResult Purge() { ... }
}
```

### Wolverine (Middleware on Class)

```csharp
[Authorize]
[Middleware(typeof(AuditMiddleware))]
public static class AdminEndpoints
{
    [WolverinePost("/api/admin/reset")]
    public static int Reset() { /* ... */ return 200; }

    [WolverinePost("/api/admin/purge")]
    public static int Purge() { /* ... */ return 200; }
}
```

For comprehensive filter migration examples, see
[Migrating from MVC/Minimal API Filters](/tutorials/middleware-migration).

## Cascading Messages (No MVC Equivalent)

This is a capability MVC controllers simply don't have. Wolverine endpoints can trigger
asynchronous messages as part of the HTTP response, with transactional outbox guarantees:

```csharp
public static class PlaceOrderEndpoint
{
    [WolverinePost("/api/orders")]
    public static (CreationResponse, OrderPlaced, NotifyWarehouse) Post(
        PlaceOrder command, IDocumentSession session)
    {
        var order = new Order { ... };
        session.Store(order);

        return (
            new CreationResponse($"/api/orders/{order.Id}"),  // HTTP response (201)
            new OrderPlaced(order.Id),                         // message → handler
            new NotifyWarehouse(order.Id, order.Items)         // message → handler
        );
    }
}
```

The `OrderPlaced` and `NotifyWarehouse` messages are sent through Wolverine's messaging pipeline
after the HTTP response is committed. With transactional middleware enabled, the messages are
persisted via the outbox in the same database transaction as the order — guaranteed delivery
even if the process crashes.

## Registration

### MVC

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();  // discovers controllers by convention
```

### Wolverine

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWolverine();

var app = builder.Build();
app.MapWolverineEndpoints();  // discovers endpoints by attribute scanning
```

Both frameworks use automatic discovery. MVC finds classes inheriting `ControllerBase`;
Wolverine finds methods decorated with `[WolverineGet]`, `[WolverinePost]`, etc.

## Summary

| Concept | MVC Controller | Wolverine.HTTP |
|---------|---------------|----------------|
| **Base class** | `ControllerBase` required | No base class — plain static classes |
| **DI pattern** | Constructor injection | Method parameter injection |
| **Route prefix** | `[Route("api/[controller]")]` | Full route in attribute |
| **Body binding** | `[FromBody]` or `[ApiController]` inferred | First complex param (always inferred) |
| **Validation** | ModelState + `[ApiController]` | `Validate` methods or FluentValidation middleware |
| **Responses** | `ActionResult<T>`, `IActionResult` | Direct return, `IResult`, `CreationResponse`, or `int` |
| **Filters** | `IActionFilter`, `IExceptionFilter`, etc. | `Before`/`After`/`Finally` methods |
| **Entity loading** | Manual in action or filter | `[Entity]` attribute or `Load`/`LoadAsync` method |
| **Async messaging** | Not built-in | Tuple returns, `OutgoingMessages` |
| **Registration** | `AddControllers()` + `MapControllers()` | `UseWolverine()` + `MapWolverineEndpoints()` |

## Further Reading

- [Wolverine.HTTP Endpoints](/guide/http/endpoints) — full endpoint reference
- [Wolverine for MediatR Users](/introduction/from-mediatr) — if you're using MediatR with MVC
- [Migrating from MVC/Minimal API Filters](/tutorials/middleware-migration) — filter migration
- [Railway Programming](/tutorials/railway-programming) — validation and loading patterns
- [Publishing Messages from HTTP](/guide/http/messaging) — cascading messages guide
