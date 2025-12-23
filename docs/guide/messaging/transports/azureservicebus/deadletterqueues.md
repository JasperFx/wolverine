# Dead Letter Queues

The behavior of Wolverine.AzureServiceBus dead letter queuing depends on the endpoint mode:

### Inline Endpoints

For inline endpoints, Wolverine uses native [Azure Service Bus dead letter queueing](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-dead-letter-queues). Failed messages are moved directly to the dead letter subqueue of the source queue. Note that inline endpoints do not use Wolverine's inbox for message persistence, so retries and dead lettering rely entirely on Azure Service Bus mechanisms.

To configure an endpoint for inline processing:

<!-- snippet: sample_asb_inline_dlq -->
<a id='snippet-sample_asb_inline_dlq'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Use inline processing with native Azure Service Bus DLQ
        opts.ListenToAzureServiceBusQueue("inline-queue")
            .ProcessInline();
    }).StartAsync();
```

### Buffered Endpoints

For buffered endpoints, Wolverine sends failed messages to a designated dead letter queue. By default, this queue is named `wolverine-dead-letter-queue`.

To customize the dead letter queue for buffered endpoints:

<!-- snippet: sample_asb_buffered_dlq -->
<a id='snippet-sample_asb_buffered_dlq'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Customize the dead letter queue name for buffered endpoint
        opts.ListenToAzureServiceBusQueue("buffered-queue")
            .BufferedInMemory()
            .ConfigureDeadLetterQueue("my-custom-dlq");
    }).StartAsync();
```

### Durable Endpoints

Durable endpoints behave similarly to buffered endpoints, with dead lettering to the configured dead letter queue, while leveraging Wolverine's persistence for reliability.

To customize the dead letter queue for durable endpoints:

<!-- snippet: sample_asb_durable_dlq -->
<a id='snippet-sample_asb_durable_dlq'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Customize the dead letter queue name for durable endpoint
        opts.ListenToAzureServiceBusQueue("durable-queue")
            .UseDurableInbox()
            .ConfigureDeadLetterQueue("my-custom-dlq");
    }).StartAsync();
```

## Disabling Dead Letter Queues

You can disable dead letter queuing for specific endpoints if needed:

<!-- snippet: sample_disable_asb_dlq -->
<a id='snippet-sample_disable_asb_dlq'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Disable dead letter queuing for this endpoint
        opts.ListenToAzureServiceBusQueue("no-dlq")
            .DisableDeadLetterQueueing();
    }).StartAsync();
``` 


