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

## Cascading Messages

As a convenience, you can embed tenant id information into outgoing cascading messages with these helpers:

<!-- snippet: sample_using_tenant_id_and_cascading_messages -->
<a id='snippet-sample_using_tenant_id_and_cascading_messages'></a>
```cs
public static IEnumerable<object> Handle(IncomingMessage message)
{
    yield return new Message1().WithTenantId("one");
    yield return new Message2().WithTenantId("one");

    yield return new Message3().WithDeliveryOptions(new DeliveryOptions
    {
        ScheduleDelay = 5.Minutes(),
        TenantId = "two"
    });
    
    // Long hand
    yield return new Message4().WithDeliveryOptions(new DeliveryOptions
    {
        TenantId = "one"
    });
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/using_group_ids.cs#L32-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_tenant_id_and_cascading_messages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
