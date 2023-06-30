# Multi-Tenancy and ASP.Net Core

::: warning
Neither Wolverine.HTTP nor Wolverine message handling use the shared, scoped IoC/DI container
from an ASP.Net Core request and any common mechanism for multi-tenancy inside of HTTP requests
that relies on IoC trickery will probably not work -- with the possible exception of `IHttpContextAccessor` using `AsyncLocal`
:::

::: info
"Real" multi-tenancy support for Wolverine.HTTP is planned. Please feel free to follow [that work on GitHub](https://github.com/JasperFx/wolverine/issues/415).
:::


To utilize multi-tenancy with Wolverine.HTTP today *and* play nicely with Wolverine's transactional inbox/outbox 
at the same time, you will have to use Wolverine as a mediator but also pass the tenant id as an argument as shown in this sample project:

<!-- snippet: sample_invoke_for_tenant -->
<a id='snippet-sample_invoke_for_tenant'></a>
```cs
[WolverineDelete("/todoitems/{tenant}")]
public static async Task Delete(
    string tenant, 
    DeleteTodo command, 
    IMessageBus bus)
{
    // Invoke inline for the specified tenant
    await bus.InvokeForTenantAsync(tenant, command);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Endpoints.cs#L72-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_invoke_for_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and with an expected result:

<!-- snippet: sample_calling_invoke_for_tenant_async_with_expected_result -->
<a id='snippet-sample_calling_invoke_for_tenant_async_with_expected_result'></a>
```cs
[WolverinePost("/todoitems/{tenant}")]
public static async Task<IResult> Create(string tenant, CreateTodo command, IMessageBus bus)
{
    // At the 1.0 release, you would have to use Wolverine as a mediator
    // to get the full multi-tenancy feature set.
    
    // That hopefully changes in 1.1
    var created = await bus.InvokeForTenantAsync<TodoCreated>(tenant, command);

    return Results.Created($"/todoitems/{tenant}/{created.Id}", created);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Endpoints.cs#L56-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_calling_invoke_for_tenant_async_with_expected_result' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Multi-Tenancy with Wolverine](/guide/handlers/multi-tenancy) for a little more information.


