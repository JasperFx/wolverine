# Dead Letter Queues

When Wolverine dead letters a message natively, it sets the broker's `DeadLetterReason` property to the
exception type and `DeadLetterErrorDescription` to the exception message, and additionally copies the standard
Wolverine diagnostic headers (`exception-type`, `exception-message`, `exception-stack`, `failed-at`,
`original-destination`) onto the dead lettered message's application properties. See
[diagnostic headers on dead letter messages](/tutorials/dead-letter-queues#diagnostic-headers-on-dead-letter-messages)
for the full cross-transport header structure.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L756-L777' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asb_inline_dlq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Buffered Endpoints

For buffered endpoints, Wolverine sends failed messages to a designated dead letter queue. By default, this queue is named `wolverine-dead-letter-queue` — or `[prefix].wolverine-dead-letter-queue` if you opted into [prefixing the system queues](/guide/messaging/transports/azureservicebus/#prefixing-system-queues).

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L782-L804' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asb_buffered_dlq' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L809-L830' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asb_durable_dlq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Overriding the Default Dead Letter Queue Name <Badge type="tip" text="6.34" />

`wolverine-dead-letter-queue` is fine for a single application, but it carries no service name at all, so several
applications sharing one Azure Service Bus namespace all dead letter into the same queue. Rather than repeating
`ConfigureDeadLetterQueue(...)` on every endpoint, override the default for the whole transport in one call — this is
the Azure Service Bus counterpart of RabbitMQ's `CustomizeDeadLetterQueueing()` and of SQS's method of the same name:

<!-- snippet: sample_asb_default_dead_letter_queue_name -->
<a id='snippet-sample_asb_default_dead_letter_queue_name'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus")!;

    opts.UseAzureServiceBus(azureServiceBusConnectionString)
        .AutoProvision()

        // Every endpoint that doesn't configure a dead letter queue of its
        // own now dead letters to "orders-errors" instead of
        // "wolverine-dead-letter-queue"
        .DefaultDeadLetterQueueName("orders-errors");

    // ...but per endpoint configuration still wins
    opts.ListenToAzureServiceBusQueue("orders")
        .ConfigureDeadLetterQueue("orders-rejects");

    opts.ListenToAzureServiceBusQueue("notifications")
        .DisableDeadLetterQueueing();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L983-L1011' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asb_default_dead_letter_queue_name' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Resolution order, per endpoint:

1. `ConfigureDeadLetterQueue("name")` on the endpoint wins.
2. `DisableDeadLetterQueueing()` on the endpoint wins — no dead letter queue for that one, and failures fall back to Wolverine's durable dead letter storage.
3. Otherwise the transport-wide `DefaultDeadLetterQueueName(...)`.
4. Otherwise `wolverine-dead-letter-queue`, with any [system queue prefix](/guide/messaging/transports/azureservicebus/#prefixing-system-queues) prepended.

The name is resolved when Wolverine reads it rather than when the endpoint is declared, so the order of these calls
during bootstrapping does not matter. A name you supply here is taken as fully qualified and is *not* prefixed by
`SystemQueuePrefix()` — only the name Wolverine picks for itself is. It is sanitized to legal Azure Service Bus entity
characters the same way per-endpoint names are.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L835-L855' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_asb_dlq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Recovering Native Dead Letters to Durable Storage <Badge type="tip" text="6.9" />

Azure Service Bus dead letters land in one of two places depending on the endpoint mode: buffered and
durable endpoints move failures to a Wolverine-managed dead letter **queue** (default
`wolverine-dead-letter-queue`, or whatever `DefaultDeadLetterQueueName()` / `SystemQueuePrefix()` resolve it to),
while inline endpoints — and Azure Service Bus itself, on TTL or
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
