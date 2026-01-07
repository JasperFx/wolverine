# Dead Letter Queues

The behavior of Wolverine.AzureServiceBus dead letter queuing depends on the endpoint mode:

### Inline Endpoints

For inline endpoints, Wolverine uses native [Azure Service Bus dead letter queueing](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-dead-letter-queues). Failed messages are moved directly to the dead letter subqueue of the source queue. Note that inline endpoints do not use Wolverine's inbox for message persistence, so retries and dead lettering rely entirely on Azure Service Bus mechanisms.

To configure an endpoint for inline processing:

<!-- snippet-todo: sample_asb_inline_dlq -->
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // One way or another, you're probably pulling the Azure Service Bus
    // connection string out of configuration
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus");

    // Connect to the broker
    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    // Use inline processing with native Azure Service Bus DLQ
    opts.ListenToAzureServiceBusQueue("inline-queue")
        .ProcessInline();
});

using var host = builder.Build();
await host.StartAsync();
```

### Buffered Endpoints

For buffered endpoints, Wolverine sends failed messages to a designated dead letter queue. By default, this queue is named `wolverine-dead-letter-queue`.

To customize the dead letter queue for buffered endpoints:

<!-- snippet-todo: sample_asb_buffered_dlq -->
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus");

    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    // Customize the dead letter queue name for buffered endpoint
    opts.ListenToAzureServiceBusQueue("buffered-queue")
        .BufferedInMemory()
        .ConfigureDeadLetterQueue("my-custom-dlq");
});

using var host = builder.Build();
await host.StartAsync();
```

### Durable Endpoints

Durable endpoints behave similarly to buffered endpoints, with dead lettering to the configured dead letter queue, while leveraging Wolverine's persistence for reliability.

To customize the dead letter queue for durable endpoints:

<!-- snippet-todo: sample_asb_durable_dlq -->
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus");

    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    // Customize the dead letter queue name for durable endpoint
    opts.ListenToAzureServiceBusQueue("durable-queue")
        .UseDurableInbox()
        .ConfigureDeadLetterQueue("my-custom-dlq");
});

using var host = builder.Build();
await host.StartAsync();
```

## Disabling Dead Letter Queues

You can disable dead letter queuing for specific endpoints if needed:

<!-- snippet-todo: sample_disable_asb_dlq -->
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus");

    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    // Disable dead letter queuing for this endpoint
    opts.ListenToAzureServiceBusQueue("no-dlq")
        .DisableDeadLetterQueueing();
});

using var host = builder.Build();
await host.StartAsync();
```
