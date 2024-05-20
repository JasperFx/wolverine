# Multi-Tenancy with Wolverine

Wolverine has first class support for multi-tenancy by tracking the tenant id as message metadata. When invoking a message
inline, you can execute that message for a specific tenant with this syntax:

<!-- snippet: sample_invoking_by_tenant -->
<a id='snippet-sample_invoking_by_tenant'></a>
```cs
public static async Task invoking_by_tenant(IMessageBus bus)
{
    // Invoke inline
    await bus.InvokeForTenantAsync("tenant1", new CreateTodo("Release Wolverine 1.0"));

    // Invoke with an expected result (request/response)
    var created =
        await bus.InvokeForTenantAsync<TodoCreated>("tenant2", new CreateTodo("Update the Documentation"));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoWebService.Tests/end_to_end.cs#L96-L108' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_invoking_by_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When using this syntax, any [cascaded messages](/guide/handlers/cascading) will also be tagged with the same tenant id.
This functionality is valid with both messages executed locally and messages that are executed remotely depending on 
the routing rules for that particular message.

To publish a message for a particular tenant id and ultimately pass the tenant id on to the message handler, use
the `DeliveryOptions` approach:

<!-- snippet: sample_publish_by_tenant -->
<a id='snippet-sample_publish_by_tenant'></a>
```cs
public static async Task publish_by_tenant(IMessageBus bus)
{
    await bus.PublishAsync(new CreateTodo("Fix that last broken test"),
        new DeliveryOptions { TenantId = "tenant3" });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoWebService.Tests/end_to_end.cs#L110-L118' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_by_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
