# Multi-Tenancy with Azure Service Bus <Badge type="tip" text="3.4" />

Let's take a trip to the world of IoT where you might very well build a single cloud hosted service that needs
to communicate via Rabbit MQ with devices at your customers sites. You'd preferably like to keep traffic separate
so that one customer never accidentally receives information from another customer. In this case, Wolverine now
lets you register separate Rabbit MQ brokers -- or at least separate virtual hosts within a single Rabbit MQ broker --
for each tenant.

::: info
Definitely see [Multi-Tenancy with Wolverine](/guide/handlers/multi-tenancy) for more information about how
Wolverine tracks the tenant id across messages. 
:::

Let's just jump straight into a simple example of the configuration:

snippet: sample_configuring_azure_service_bus_for_multi_tenancy

::: warning
Wolverine has no way of creating new Azure Service Bus namespaces for you
:::

In the code sample above, I'm setting up the Azure Service Bus transport to "know" that there are multiple tenants
with separate Azure Service Bus fully qualified namespaces. 

::: tip
Note that Wolverine uses the credentials specified for the default Azure Service
Bus connection for all tenant specific connections
:::

At runtime, if we send a message like so:

<!-- snippet: sample_send_message_to_specific_tenant -->
<a id='snippet-sample_send_message_to_specific_tenant'></a>
```cs
public static async Task send_message_to_specific_tenant(IMessageBus bus)
{
    // Send a message tagged to a specific tenant id
    await bus.PublishAsync(new Message1(), new DeliveryOptions { TenantId = "two" });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/multi_tenancy_through_virtual_hosts.cs#L211-L219' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_message_to_specific_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the case above, in the Wolverine internals, it:

1. Routes the message to a Azure Service Bus queue named "outgoing"
2. Within the sender for that queue, Wolverine sees that `TenantId == "two"`, so it sends the message to the "outgoing" queue
   on the Azure Service Bus connection that we specified for the "two" tenant id.

Likewise, see the listening set up against the "incoming" queue above. At runtime, this Wolverine application will be
listening to a queue named "incoming" on the default Azure Service Bus namespace and a separate queue named "incoming" on the separate
fully qualified namespaces for the known tenants. When a message is received at any of these queues, it's tagged with the 
`TenantId` that's appropriate for each separate tenant-specific listening endpoint. That helps Wolverine also track
tenant specific operations (with Marten maybe?) and tracks the tenant id across any outgoing messages or responses as well.





