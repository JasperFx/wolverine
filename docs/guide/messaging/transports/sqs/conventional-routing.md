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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L204-L212' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_conventional_sqs_routing' title='Start of snippet'>anchor</a></sup>
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

