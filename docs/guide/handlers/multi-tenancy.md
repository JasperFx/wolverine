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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoWebService.Tests/end_to_end.cs#L97-L109' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_invoking_by_tenant' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoWebService.Tests/end_to_end.cs#L111-L119' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_by_tenant' title='Start of snippet'>anchor</a></sup>
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

## Referencing the TenantId <Badge type="tip" text="3.6" />

Let's say that you want to reference the current tenant id in your Wolverine message handler or Wolverine HTTP endpoint,
but you don't want to inject the Wolverine `IMessageContext` or `Envelope` into your methods, but instead would like
an easy way to just "push" the current tenant id into your handler methods. Maybe this is for ease of writing unit tests,
or conditional logic, or some other reason.

To that end, you can inject the `Wolverine.Persistence.TenantId` into any Wolverine message handler or HTTP endpoint method
to get easy access to the tenant id:

<!-- snippet: sample_TenantId -->
<a id='snippet-sample_tenantid'></a>
```cs
/// <summary>
/// Strong typed identifier for the tenant id within a Wolverine message handler
/// or HTTP endpoint that is using multi-tenancy
/// </summary>
/// <param name="Value">The active tenant id. Note that this can be null</param>
public record TenantId(string Value)
{
    public const string DefaultTenantId = "*DEFAULT*";

    /// <summary>
    /// Is there a non-default tenant id?
    /// </summary>
    /// <returns></returns>
    public bool IsEmpty() => Value.IsEmpty() || Value == DefaultTenantId;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Persistence/TenantId.cs#L9-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_tenantid' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There's really nothing to it other than just pulling that type in as a parameter argument to a message handler:

<!-- snippet: sample_injecting_tenant_id -->
<a id='snippet-sample_injecting_tenant_id'></a>
```cs
public static class SomeCommandHandler
{
    // Wolverine is keying off the type, the parameter name
    // doesn't really matter
    public static void Handle(SomeCommand command, TenantId tenantId)
    {
        Debug.WriteLine($"I got a command {command} for tenant {tenantId.Value}");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/multi_tenancy.cs#L108-L120' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_injecting_tenant_id' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In tests, you can create that `TenantId` value just by:

```csharp
var tenantId = new TenantId("tenant1");
```

and then just pass the value into the method under test.
