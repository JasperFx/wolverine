# Subscriptions

TODO -- talk about messaging Uri

:::tip
Some of the transports have conventional routing approaches as well as the explicit routing rules
shown in this page.
:::

When you publish a message using `IMessageBus` or `IMessageContext` without explicitly setting the Uri of the desired
destination, Wolverine has to invoke the known message routing rules and dynamic subscriptions to
figure out which locations should receive the message. Consider this code that publishes a
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


