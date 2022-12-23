# Subscriptions

When you publish a message using `IMessageBus` or `IMessageContext`, Wolverine uses its concept of subscriptions to know how and where to send the message. Consider this code that publishes a
`PingMessage`:

<!-- snippet: sample_sending_messages_for_static_routing -->
<a id='snippet-sample_sending_messages_for_static_routing'></a>
```cs
public class SendingExample
{
    public async Task SendPingsAndPongs(IMessageContext bus)
    {
        // Publish a message
        await bus.SendAsync(new PingMessage());
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/channels.cs#L6-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_messages_for_static_routing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When sending, publishing, scheduling, or invoking a message type for the first time, Wolverine runs though
a series of rules to determine what endpoint(s) subscribes to the message type. Those rules in order of precedence
are:

1. Explicit subscription rules
2. Use a local subscription using the conventional local queue routing if the message type has a known message handler within the application
3. Any registered message routing conventions like the Rabbit MQ or Amazon SQS routing conventions

## Explicit Subscriptions

To route messages to specific endpoints, we can apply static message routing rules by using a routing rule as shown below:

<!-- snippet: sample_StaticPublishingRules -->
<a id='snippet-sample_staticpublishingrules'></a>
```cs
using var host = Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Route a single message type
        opts.PublishMessage<PingMessage>()
            .ToServerAndPort("server", 1111);

        // Send every possible message to a TCP listener
        // on this box at port 2222
        opts.PublishAllMessages().ToPort(2222);

        // Or use a more fluent interface style
        opts.Publish().MessagesFromAssembly(typeof(PingMessage).Assembly)
            .ToPort(3333);

        // Complicated rules, I don't think folks will use this much
        opts.Publish(rule =>
        {
            // Apply as many message matching
            // rules as you need

            // Specific message types
            rule.Message<PingMessage>();
            rule.Message<Message1>();

            // All types in a certain assembly
            rule.MessagesFromAssemblyContaining<PingMessage>();

            // or this
            rule.MessagesFromAssembly(typeof(PingMessage).Assembly);

            // or by namespace
            rule.MessagesFromNamespace("MyMessageLibrary");
            rule.MessagesFromNamespaceContaining<PingMessage>();

            // Express the subscribers
            rule.ToPort(1111);
            rule.ToPort(2222);
        });

        // Or you just send all messages to a certain endpoint
        opts.PublishAllMessages().ToPort(3333);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/StaticPublishingRule.cs#L13-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_staticpublishingrules' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Do note that doing the message type filtering by namespace will also include child namespaces. In
our own usage we try to rely on either namespace rules or by using shared message assemblies.

## Message Routing Conventions

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L125-L135' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_conventional_sqs_routing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this case any outgoing message types that aren't handled locally or have an explicit subscription will be automatically routed
to an Amazon SQS queue named after the Wolverine message type name of the message type.

## Basic Options

TODO -- talk about durability, buffered, inline. Envelope rules. Serializers
