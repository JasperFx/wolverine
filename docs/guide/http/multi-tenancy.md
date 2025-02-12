# Multi-Tenancy and ASP.Net Core

::: warning
Neither Wolverine.HTTP nor Wolverine message handling use the shared, scoped IoC/DI container
from an ASP.Net Core request and any common mechanism for multi-tenancy inside of HTTP requests
that relies on IoC trickery will probably not work -- with the possible exception of `IHttpContextAccessor` using `AsyncLocal`
:::

::: info
"Real" multi-tenancy support for Wolverine.HTTP was added in Wolverine 1.7.0.
:::

## Tenant Id Detection

::: warning
Wolverine's multi-tenancy support is very admittedly built with [Marten's multi-tenancy support]() in mind, 
and part of that is assuming that tenants are identified with a `string`.
:::

::: tip
Wolverine has no direct or special security integration, but should be usable with (we think) any existing
[ASP.Net Core authentication and authorization support](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/claims?view=aspnetcore-7.0) including the `[Authorize]` attribute usage that declares
required claims.
:::

The first part of any multi-tenancy approach in HTTP services is to just detect which tenant should be active within
the current request. Wolverine.HTTP refers to this as "tenant id detection". Out of the box, Wolverine comes with some simple
recipes that can be mixed and matched as shown below:

<!-- snippet: sample_configuring_tenant_id_detection -->
<a id='snippet-sample_configuring_tenant_id_detection'></a>
```cs
var builder = WebApplication.CreateBuilder();

var connectionString = builder.Configuration.GetConnectionString("postgres");

builder.Services
    .AddMarten(connectionString)
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
});

var app = builder.Build();

// Configure the WolverineHttpOptions
app.MapWolverineEndpoints(opts =>
{
    // The tenancy detection is fall through, so the first strategy
    // that finds anything wins!

    // Use the value of a named request header
    opts.TenantId.IsRequestHeaderValue("tenant");

    // Detect the tenant id from an expected claim in the
    // current request's ClaimsPrincipal
    opts.TenantId.IsClaimTypeNamed("tenant");

    // Use a query string value for the key 'tenant'
    opts.TenantId.IsQueryStringValue("tenant");

    // Use a named route argument for the tenant id
    opts.TenantId.IsRouteArgumentNamed("tenant");

    // Use the *first* sub domain name of the request Url
    // Note that this is very naive
    opts.TenantId.IsSubDomainName();
    
    // If the tenant id cannot be detected otherwise, fallback
    // to a designated tenant id
    opts.TenantId.DefaultIs("default_tenant");

});

return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Samples/MultiTenancy.cs#L15-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_tenant_id_detection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

All of the options are configured on `WolverineHttpOptions.TenantId`.

::: tip
Wolverine does not yet have direct support for multi-tenancy with Entity Framework Core, but that's something we're
interested in building into Wolverine's feature set. [You can track or comment on that work here](https://github.com/JasperFx/wolverine/issues/556).
:::

When Wolverine is actively detecting the tenant id, it's first setting the detected value on the active `MessageContext.TenantId`
property, so any messages sent out during the execution of the HTTP request will also be tagged with this tenant id. In the 
case of the [Marten integration with Wolverine](/guide/durability/marten/), Wolverine is able to use the tenant id to create the proper `IDocumentSession`.

As an example, consider the [MultiTenantedTodoService
](https://github.com/JasperFx/wolverine/tree/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService) sample
in the Wolverine codebase. 

That service first sets up multi-tenancy in Marten with a separate database per tenant like so:

<!-- snippet: sample_configuring_wolverine_for_marten_multi_tenancy -->
<a id='snippet-sample_configuring_wolverine_for_marten_multi_tenancy'></a>
```cs
// Adding Marten for persistence
builder.Services.AddMarten(m =>
    {
        // With multi-tenancy through a database per tenant
        m.MultiTenantedDatabases(tenancy =>
        {
            // You would probably be pulling the connection strings out of configuration,
            // but it's late in the afternoon and I'm being lazy building out this sample!
            tenancy.AddSingleTenantDatabase("Host=localhost;Port=5433;Database=tenant1;Username=postgres;password=postgres", "tenant1");
            tenancy.AddSingleTenantDatabase("Host=localhost;Port=5433;Database=tenant2;Username=postgres;password=postgres", "tenant2");
            tenancy.AddSingleTenantDatabase("Host=localhost;Port=5433;Database=tenant3;Username=postgres;password=postgres", "tenant3");
        });

        m.DatabaseSchemaName = "mttodo";
    })
    .IntegrateWithWolverine(x => x.MasterDatabaseConnectionString = connectionString);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Program.cs#L12-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_wolverine_for_marten_multi_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then configures Wolverine itself like:

<!-- snippet: sample_wolverine_setup_for_marten_multitenancy -->
<a id='snippet-sample_wolverine_setup_for_marten_multitenancy'></a>
```cs
// Wolverine usage is required for WolverineFx.Http
builder.Host.UseWolverine(opts =>
{
    // This middleware will apply to the HTTP
    // endpoints as well
    opts.Policies.AutoApplyTransactions();

    // Setting up the outbox on all locally handled
    // background tasks
    opts.Policies.UseDurableLocalQueues();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Program.cs#L39-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_setup_for_marten_multitenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Lastly, the Wolverine.HTTP setup to add the tenant id detection:

<!-- snippet: sample_configuring_tenant_id_detection_for_todo_service -->
<a id='snippet-sample_configuring_tenant_id_detection_for_todo_service'></a>
```cs
// Let's add in Wolverine HTTP endpoints to the routing tree
app.MapWolverineEndpoints(opts =>
{
    // Letting Wolverine HTTP automatically detect the tenant id!
    opts.TenantId.IsRouteArgumentNamed("tenant");

    // Assert that the tenant id was successfully detected,
    // or pull the rip cord on the request and return a
    // 400 w/ ProblemDetails
    opts.TenantId.AssertExists();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Program.cs#L70-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_tenant_id_detection_for_todo_service' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the code sample above, I'm choosing to make the "tenant" a mandatory route argument
on each HTTP endpoint, then relying on that for the tenant id detection. As discussed in a later
section, this application is also enforcing that all routes must have a non-null tenant.

::: warning
Wolverine is not yet doing anything to validate your tenant id, so that will need to be done explicitly
in your own code. 
:::

Inside of this "Todo" web service, there's an endpoint that just allows users to access the data for all the `Todo`
items persisted in the current tenant's database like so:

<!-- snippet: sample_get_all_todos -->
<a id='snippet-sample_get_all_todos'></a>
```cs
// The "tenant" route argument would be the route
[WolverineGet("/todoitems/{tenant}")]
public static Task<IReadOnlyList<Todo>> Get(string tenant, IQuerySession session)
{
    return session.Query<Todo>().ToListAsync();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Endpoints.cs#L25-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_get_all_todos' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At runtime, Wolverine is now generating this code around that endpoint method:

```csharp
public class GET_todoitems_tenant : Wolverine.Http.HttpHandler
{
    private readonly Wolverine.Http.WolverineHttpOptions _wolverineHttpOptions;
    private readonly Wolverine.Runtime.IWolverineRuntime _wolverineRuntime;
    private readonly Wolverine.Marten.Publishing.OutboxedSessionFactory _outboxedSessionFactory;

    public GET_todoitems_tenant(Wolverine.Http.WolverineHttpOptions wolverineHttpOptions, Wolverine.Runtime.IWolverineRuntime wolverineRuntime, Wolverine.Marten.Publishing.OutboxedSessionFactory outboxedSessionFactory) : base(wolverineHttpOptions)
    {
        _wolverineHttpOptions = wolverineHttpOptions;
        _wolverineRuntime = wolverineRuntime;
        _outboxedSessionFactory = outboxedSessionFactory;
    }



    public override async System.Threading.Tasks.Task Handle(Microsoft.AspNetCore.Http.HttpContext httpContext)
    {
        var messageContext = new Wolverine.Runtime.MessageContext(_wolverineRuntime);

        // Tenant Id detection
        // 1. Tenant Id is route argument named 'tenant'
        var tenantId = await TryDetectTenantId(httpContext);
        messageContext.TenantId = tenantId;
        if (string.IsNullOrEmpty(tenantId))
        {
            await WriteTenantIdNotFound(httpContext);
            return;
        }

        // Building the Marten session using the detected tenant id
        await using var querySession = _outboxedSessionFactory.QuerySession(messageContext, tenantId);
        var tenant = (string)httpContext.GetRouteValue("tenant");
        
        // The actual HTTP request handler execution
        var todoIReadOnlyList_response = await MultiTenantedTodoWebService.TodoEndpoints.Get(tenant, querySession).ConfigureAwait(false);

        // Writing the response body to JSON because this was the first 'return variable' in the method signature
        await WriteJsonAsync(httpContext, todoIReadOnlyList_response);
    }

}
```

## Referencing the Tenant Id in Endpoint Methods

See [Referencing the TenantId](/guide/handlers/multi-tenancy.html#referencing-the-tenantid) on using Wolverine's `TenantId` type.

## Requiring Tenant Id -- or Not!

You can direct Wolverine.HTTP to verify that there is a non-null, non-empty tenant id on all requests with this syntax:

<!-- snippet: sample_assert_tenant_id_exists -->
<a id='snippet-sample_assert_tenant_id_exists'></a>
```cs
app.MapWolverineEndpoints(opts =>
{
    // Configure your tenant id detection...

    // Require tenant id some how, some way...
    opts.TenantId.AssertExists();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Samples/MultiTenancy.cs#L68-L78' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_assert_tenant_id_exists' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At runtime, this is going to return a status code of 400 with a [ProblemDetails](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.problemdetails?view=aspnetcore-7.0) specification 
response stating that the tenant id was missing. 

But of course, you will frequently have *some* endpoints within your system that do **not** use any kind
of multi-tenancy, so you can completely opt out of all tenant id detection and assertions through the
`[NotTenanted]` attribute as shown here in the tests:

<!-- snippet: sample_using_NotTenanted -->
<a id='snippet-sample_using_nottenanted'></a>
```cs
// Mark this endpoint as not using any kind of multi-tenancy
[WolverineGet("/nottenanted"), NotTenanted]
public static string NoTenantNoProblem()
{
    return "hey";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/multi_tenancy_detection_and_integration.cs#L440-L449' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_nottenanted' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If the above usage completely disabled all tenant id detection or validation, in the case of an endpoint that *might* be 
tenanted or might be validly used across all tenants depending on client needs, you can add the tenant id detection
while disabling the tenant id assertion on missing values with the '[MaybeTenanted]` attribute shown
below in test code:

<!-- snippet: sample_maybe_tenanted_attribute_usage -->
<a id='snippet-sample_maybe_tenanted_attribute_usage'></a>
```cs
// Mark this endpoint as "maybe" having a tenant id
[WolverineGet("/maybe"), MaybeTenanted]
public static string MaybeTenanted(IMessageBus bus)
{
    return bus.TenantId ?? "none";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/multi_tenancy_detection_and_integration.cs#L451-L460' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_maybe_tenanted_attribute_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Custom Tenant Detection Strategy

The built in tenant id detection strategies are all very simplistic, and it's quite possible that you will have more complex
needs. Maybe you need to do some database lookups. Maybe you need to interpret the values and partially parse route
parameters. Wolverine still has you covered by allowing you to create custom implementations of its `Wolverine.Http.Runtime.MultiTenancy.ITenantDetection`
interface:

<!-- snippet: sample_ITenantDetection -->
<a id='snippet-sample_itenantdetection'></a>
```cs
/// <summary>
/// Used to create new strategies to detect the tenant id from an HttpContext
/// for the current request
/// </summary>
public interface ITenantDetection
{
    /// <summary>
    /// This method can return the actual tenant id or null to represent "not found"
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public ValueTask<string?> DetectTenant(HttpContext context);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/Runtime/MultiTenancy/ITenantDetection.cs#L5-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_itenantdetection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As any example, the route argument detection implementation looks like this:

<!-- snippet: sample_ArgumentDetection -->
<a id='snippet-sample_argumentdetection'></a>
```cs
internal class ArgumentDetection : ITenantDetection, ISynchronousTenantDetection
{
    private readonly string _argumentName;

    public ArgumentDetection(string argumentName)
    {
        _argumentName = argumentName;
    }
    
    

    public ValueTask<string?> DetectTenant(HttpContext httpContext) 
        => new(DetectTenantSynchronously(httpContext));

    public override string ToString()
    {
        return $"Tenant Id is route argument named '{_argumentName}'";
    }

    public string? DetectTenantSynchronously(HttpContext context)
    {
        return context.Request.RouteValues.TryGetValue(_argumentName, out var value)
            ? value?.ToString()
            : null;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/Runtime/MultiTenancy/ArgumentDetection.cs#L5-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_argumentdetection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
When you implement your custom strategy, the `ToString()` output will be a
hopefully descriptive comment in the generated HTTP endpoint code as a diagnostics
:::

To add a custom tenant id detection strategy, you can use one of two options:

<!-- snippet: sample_registering_custom_tenant_detection -->
<a id='snippet-sample_registering_custom_tenant_detection'></a>
```cs
app.MapWolverineEndpoints(opts =>
{
    // If your strategy does not need any IoC service
    // dependencies, just add it directly
    opts.TenantId.DetectWith(new MyCustomTenantDetection());

    // In this case, your detection type will be built by
    // the underlying IoC container for your application
    // No other registration is necessary here for the strategy
    // itself
    opts.TenantId.DetectWith<MyCustomTenantDetection>();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Samples/MultiTenancy.cs#L83-L99' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_custom_tenant_detection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Just note that if you are having the IoC container for your Wolverine application resolve your
custom `ITenantDetection` strategy that it's going to be effectively `Singleton`-scoped. Wolverine
depends on using [Lamar](https://jasperfx.github.io/lamar) as the underlying IoC container, and Lamar does
not require prior registrations to directly resolve a concrete type as long as it can select a public
constructor with dependencies that it "knows" how to resolve in turn.



## Delegating to Wolverine as "Mediator"

To utilize multi-tenancy with Wolverine.HTTP today *and* play nicely with Wolverine's transactional inbox/outbox 
at the same time, you will have to use Wolverine as a mediator but also pass the tenant id as an argument as shown in this sample project:

<!-- snippet: sample_invoke_for_tenant -->
<a id='snippet-sample_invoke_for_tenant'></a>
```cs
// While this is still valid....
[WolverineDelete("/todoitems/{tenant}/longhand")]
public static async Task Delete(
    string tenant,
    DeleteTodo command,
    IMessageBus bus)
{
    // Invoke inline for the specified tenant
    await bus.InvokeForTenantAsync(tenant, command);
}

// Wolverine.HTTP 1.7 added multi-tenancy support so
// this short hand works without the extra jump through
// "Wolverine as Mediator"
[WolverineDelete("/todoitems/{tenant}")]
public static void Delete(
    DeleteTodo command, IDocumentSession session)
{
    // Just mark this document as deleted,
    // and Wolverine middleware takes care of the rest
    // including the multi-tenancy detection now
    session.Delete<Todo>(command.Id);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Endpoints.cs#L74-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_invoke_for_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and with an expected result:

<!-- snippet: sample_calling_invoke_for_tenant_async_with_expected_result -->
<a id='snippet-sample_calling_invoke_for_tenant_async_with_expected_result'></a>
```cs
[WolverinePost("/todoitems/{tenant}")]
public static CreationResponse<TodoCreated> Create(
    // Only need this to express the location of the newly created
    // Todo object
    string tenant,
    CreateTodo command,
    IDocumentSession session)
{
    var todo = new Todo { Name = command.Name };

    // Marten itself sets the Todo.Id identity
    // in this call
    session.Store(todo);

    // New syntax in Wolverine.HTTP 1.7
    // Helps Wolverine
    return CreationResponse.For(new TodoCreated(todo.Id), $"/todoitems/{tenant}/{todo.Id}");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Endpoints.cs#L51-L72' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_calling_invoke_for_tenant_async_with_expected_result' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Multi-Tenancy with Wolverine](/guide/handlers/multi-tenancy) for a little more information.


## Tenant Id Detection for Marten Without Wolverine

Okay, here's an oddball case that absolutely came up for our users. Let's say that you need to do 
the tenant id detection for Marten directly within HTTP requests without using Wolverine otherwise
-- like a recent Marten user needed to do with [Hot Chocolate](https://chillicream.com/docs/hotchocolate/v13) endpoints. 

Using the `WolverineFx.Http.Marten` Nuget, there's a helper to replace Marten's `ISessionFactory`
with a multi-tenanted version like this:

<!-- snippet: sample_using_AddMartenTenancyDetection -->
<a id='snippet-sample_using_addmartentenancydetection'></a>
```cs
builder.Services.AddMartenTenancyDetection(tenantId =>
{
    tenantId.IsQueryStringValue("tenant");
    tenantId.DefaultIs("default-tenant");
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Marten/multi_tenanted_session_factory_without_wolverine.cs#L29-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_addmartentenancydetection' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_using_addmartentenancydetection-1'></a>
```cs
builder.Services.AddMartenTenancyDetection(tenantId =>
{
    tenantId.IsQueryStringValue("tenant");
    tenantId.DefaultIs("default-tenant");
}, (c, session) =>
{
    session.CorrelationId = c.TraceIdentifier;
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Marten/multi_tenanted_session_factory_without_wolverine.cs#L92-L103' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_addmartentenancydetection-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

