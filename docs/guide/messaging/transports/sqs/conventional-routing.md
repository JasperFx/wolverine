# Conventional Message Routing

As an example, you can apply conventional routing with the Amazon SQS transport like so:

<!-- snippet: sample_using_conventional_sqs_routing -->
<a id='snippet-sample_using_conventional_sqs_routing'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()
            .UseConventionalRouting();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L265-L273' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_conventional_sqs_routing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this case any outgoing message types that aren't handled locally or have an explicit subscription will be automatically routed
to an Amazon SQS queue named after the Wolverine message type name of the message type.

## Handler Type Naming <Badge type="tip" text="5.25" />

By default, conventional routing names queues after the **message type**. In modular monolith scenarios where you have
more than one handler for a given message type and want each handler to receive messages on its own dedicated queue,
you can opt into naming queues after the **handler type** instead:

```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()
            // Name listener queues after the handler type instead of the message type
            .UseConventionalRouting(NamingSource.FromHandlerType);
    }).StartAsync();
```

With `NamingSource.FromHandlerType`, each handler class gets its own dedicated SQS queue named after the handler type.
This ensures that each handler independently receives a copy of every message. Outgoing queue names are still derived
from the message type.

## Several Handler Types for One Message Type <Badge type="tip" text="7.0" />

When a message type has more than one handler type, every handler type is its own handler chain
([see here](/guide/handlers/#multiple-handlers-for-the-same-message-type)) with its own listener, and so is a
sticky handler. SQS has no exchange or topic to fan a message out from, so the convention does the fan-out at the
sender:

* each of those handler types listens on a queue named from the handler type, run through the transport's naming
  rules (the same name RabbitMQ gives its per-handler queue);
* the sender publishes the message to each of those queues;
* a message type with a single, non-sticky handler type keeps the message type's own queue, as before.

```csharp
opts.UseAmazonSqsTransport()
    .AutoProvision()
    .UseConventionalRouting();
```

The per-handler queues are known only to the process that holds those handlers. A publisher in another process
publishes to the message type's queue alone, and nothing listens on that queue when every handler type has a queue of
its own, so none of the handlers receives the message. Fan-out across processes on AWS needs an [SNS topic](../sns)
in front of the queues.
