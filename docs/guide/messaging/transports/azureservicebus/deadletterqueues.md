# Dead Letter Queues

The behavior of Wolverine.AzureServiceBus dead letter queuing depends on the endpoint mode:

### Inline Endpoints

For inline endpoints, Wolverine uses native [Azure Service Bus dead letter queueing](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-dead-letter-queues). Failed messages are moved directly to the dead letter subqueue of the source queue. Note that inline endpoints do not use Wolverine's inbox for message persistence, so retries and dead lettering rely entirely on Azure Service Bus mechanisms.

To configure an endpoint for inline processing:

<!-- snippet: sample_asb_inline_dlq -->
<a id='snippet-sample_asb_inline_dlq'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus")!;

    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    // Inline endpoints use Azure Service Bus's *native* dead letter
    // subqueue of the source queue. There's no Wolverine inbox, so
    // dead lettering is handled entirely by Azure Service Bus.
    opts.ListenToAzureServiceBusQueue("inline-queue")
        .ProcessInline();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L528-L549' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asb_inline_dlq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Buffered Endpoints

For buffered endpoints, Wolverine sends failed messages to a designated dead letter queue. By default, this queue is named `wolverine-dead-letter-queue`.

To customize the dead letter queue for buffered endpoints:

<!-- snippet: sample_asb_buffered_dlq -->
<a id='snippet-sample_asb_buffered_dlq'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus")!;

    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    // Buffered endpoints move failed messages to a Wolverine-managed
    // dead letter queue. The default name is "wolverine-dead-letter-queue",
    // but you can override it per endpoint.
    opts.ListenToAzureServiceBusQueue("buffered-queue")
        .BufferedInMemory()
        .ConfigureDeadLetterQueue("my-custom-dlq");
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L554-L576' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asb_buffered_dlq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Durable Endpoints

Durable endpoints behave similarly to buffered endpoints, with dead lettering to the configured dead letter queue, while leveraging Wolverine's persistence for reliability.

To customize the dead letter queue for durable endpoints:

<!-- snippet: sample_asb_durable_dlq -->
<a id='snippet-sample_asb_durable_dlq'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus")!;

    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    // Durable endpoints behave like buffered endpoints for dead lettering,
    // but add Wolverine's durable inbox persistence for reliability.
    opts.ListenToAzureServiceBusQueue("durable-queue")
        .UseDurableInbox()
        .ConfigureDeadLetterQueue("my-custom-dlq");
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L581-L602' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asb_durable_dlq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Disabling Dead Letter Queues

You can disable dead letter queuing for specific endpoints if needed:

<!-- snippet: sample_disable_asb_dlq -->
<a id='snippet-sample_disable_asb_dlq'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus")!;

    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    // Disable Wolverine-managed dead letter queueing for this endpoint.
    // Failed messages fall back to Wolverine's regular error handling.
    opts.ListenToAzureServiceBusQueue("no-dlq")
        .DisableDeadLetterQueueing();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L607-L627' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_asb_dlq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
