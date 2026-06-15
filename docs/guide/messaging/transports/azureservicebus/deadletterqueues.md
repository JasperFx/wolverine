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

## Recovering Native Dead Letters to Durable Storage <Badge type="tip" text="6.9" />

Azure Service Bus dead letters land in one of two places depending on the endpoint mode: buffered and
durable endpoints move failures to a Wolverine-managed dead letter **queue** (default
`wolverine-dead-letter-queue`), while inline endpoints — and Azure Service Bus itself, on TTL or
max-delivery — use the native
[`$DeadLetterQueue` sub-queue](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-dead-letter-queues)
of the source entity. Either way, those messages are only visible through Azure tooling. Tools that
manage Wolverine's *durable* dead letters (for example [CritterWatch](https://github.com/JasperFx/CritterWatch))
can't see or replay them.

`EnableDeadLetterQueueRecovery()` starts a background listener that drains **both** kinds of source —
the Wolverine-managed dead letter queue(s) and the native `$DeadLetterQueue` sub-queue of every
listening queue and subscription — copying each message into Wolverine's durable dead letter storage
(the `wolverine_dead_letters` table), where it becomes queryable and replayable through
`IDeadLetters`:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Durable message storage is required — the recovered dead letters
        // are written to the wolverine_dead_letters table.
        opts.PersistMessagesWithPostgresql(connectionString);

        opts.UseAzureServiceBus(connectionString)
            .AutoProvision()
            // Drain the native $DeadLetterQueue sub-queue of every listening
            // queue and subscription into Wolverine's durable storage.
            .EnableDeadLetterQueueRecovery();

        opts.ListenToAzureServiceBusQueue("orders");
    }).StartAsync();
```

With no arguments, every managed dead letter queue and every listening queue/subscription's native
sub-queue is drained. Pass explicit names (a managed dead letter queue name, a listening queue name,
or a subscription endpoint name) to restrict recovery to a subset:

```csharp
opts.UseAzureServiceBus(connectionString)
    .EnableDeadLetterQueueRecovery("orders", "shipments");
```

The original exception type and message are preserved: from the stamped failure metadata for messages
in the managed dead letter queue, or from the native `DeadLetterReason`/`DeadLetterErrorDescription`
for messages in a native sub-queue. A message is only completed off its source *after* it has been
safely written to durable storage, so a transient database outage never loses a dead letter.

::: tip
This is the Azure Service Bus equivalent of the
[RabbitMQ dead letter recovery](../rabbitmq/deadletterqueues.html) feature, and uses the same
`EnableDeadLetterQueueRecovery()` syntax across every native-dead-letter transport.
:::
