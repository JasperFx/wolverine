# Multi-Tenancy with Rabbit MQ <Badge type="tip" text="3.4" />

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

<!-- snippet: sample_configuring_rabbit_mq_for_tenancy -->
<a id='snippet-sample_configuring_rabbit_mq_for_tenancy'></a>
```cs
var builder = Host.CreateApplicationBuilder();

builder.UseWolverine(opts =>
{
    // At this point, you still have to have a *default* broker connection to be used for 
    // messaging. 
    opts.UseRabbitMq(new Uri(builder.Configuration.GetConnectionString("main")))
        
        // This will be respected across *all* the tenant specific
        // virtual hosts and separate broker connections
        .AutoProvision()

        // This is the default, if there is no tenant id on an outgoing message,
        // use the default broker
        .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

        // Or tell Wolverine instead to just quietly ignore messages sent
        // to unrecognized tenant ids
        .TenantIdBehavior(TenantedIdBehavior.IgnoreUnknownTenants)

        // Or be draconian and make Wolverine assert and throw an exception
        // if an outgoing message does not have a tenant id
        .TenantIdBehavior(TenantedIdBehavior.TenantIdRequired)

        // Add specific tenants for separate virtual host names
        // on the same broker as the default connection
        .AddTenant("one", "vh1")
        .AddTenant("two", "vh2")
        .AddTenant("three", "vh3")

        // Or, you can add a broker connection to something completel
        // different for a tenant
        .AddTenant("four", new Uri(builder.Configuration.GetConnectionString("rabbit_four")));

    // This Wolverine application would be listening to a queue
    // named "incoming" on all virtual hosts and/or tenant specific message
    // brokers
    opts.ListenToRabbitQueue("incoming");

    opts.ListenToRabbitQueue("incoming_global")
        
        // This opts this queue out from being per-tenant, such that
        // there will only be the single "incoming_global" queue for the default
        // broker connection
        .GlobalListener();

    // More on this in the docs....
    opts.PublishMessage<Message1>()
        .ToRabbitQueue("outgoing").GlobalSender();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/multi_tenancy_through_virtual_hosts.cs#L263-L316' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_rabbit_mq_for_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Wolverine has no way of creating new virtual hosts in Rabbit MQ for you. You will have to do that manually
through either the Rabbit MQ admin site, the Rabbit MQ HTTP API, or the Rabbit MQ command line. 
:::

In the code sample above, I'm setting up Rabbit MQ to "know" that there are four specific tenants identified as
"one", "two", "three", and "four". I've also told Wolverine how to connect to Rabbit MQ separately for each 
known tenant id. 

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/multi_tenancy_through_virtual_hosts.cs#L321-L329' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_message_to_specific_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the case above, in the Wolverine internals, it:

1. Routes the message to a Rabbit MQ queue named "outgoing"
2. Within the sender for that queue, Wolverine sees that `TenantId == "two"`, so it sends the message to the "outgoing" queue
   on the "vh2" virtual host

Likewise, see the listening set up against the "incoming" queue above. At runtime, this Wolverine application will be
listening to a queue named "incoming" on the default Rabbit MQ broker and a separate queue named "incoming" on the separate
virtual hosts or brokers for the known tenants. When a message is received at any of these queues, it's tagged with the 
`TenantId` that's appropriate for each separate tenant-specific listening endpoint. That helps Wolverine also track
tenant specific operations (with Marten maybe?) and tracks the tenant id across any outgoing messages or responses as well.





