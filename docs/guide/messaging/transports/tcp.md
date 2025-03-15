# Using Wolverine's Lightweight TCP Transport

Wolverine has a lightweight transport option built in that relies on batching messages through raw socket communication.
At this point, this transport is absolutely robust enough for production usage (that's my story and I'm sticking to it),
but does not yet have any facility for security. As such, it may be most useful for testing or development scenarios where the "real"
message broker is not really usable in local environments. Either way, there's not much necessary to use the TCP
transport:

::: tip
You can listen to messages from as many ports as you like, but be aware of port contention issues.
:::

To listen for messages with the TCP transport, use the `ListenAtPort()` extension method shown below:

<!-- snippet: sample_UseWolverineWithInlineOptionsConfigurationAndHosting -->
<a id='snippet-sample_UseWolverineWithInlineOptionsConfigurationAndHosting'></a>
```cs
public static IHost CreateHostBuilder()
{
    var builder = Host.CreateApplicationBuilder();
    
    // This adds Wolverine with inline configuration
    // of WolverineOptions
    builder.UseWolverine(opts =>
    {
        // This is an example usage of the application's
        // IConfiguration inside of Wolverine bootstrapping
        var port = builder.Configuration.GetValue<int>("ListenerPort");
        opts.ListenAtPort(port);

        // If we're running in development mode and you don't
        // want to worry about having all the external messaging
        // dependencies up and running, stub them out
        if (builder.Environment.IsDevelopment())
        {
            // This will "stub" out all configured external endpoints
            opts.StubAllExternalTransports();
        }
    });

    return builder.Build();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CustomWolverineOptions.cs#L30-L58' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_UseWolverineWithInlineOptionsConfigurationAndHosting' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Likewise, to publish via TCP, use the `ToPort()` extension method to publish to another port on the same
machine:

<!-- snippet: sample_sending_to_endpoint_by_name -->
<a id='snippet-sample_sending_to_endpoint_by_name'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PublishAllMessages().ToPort(5555)
            .Named("One");

        opts.PublishAllMessages().ToPort(5555)
            .Named("Two");
    }).StartAsync();

var bus = host.Services
    .GetRequiredService<IMessageBus>();

// Explicitly send a message to a named endpoint
await bus.EndpointFor("One").SendAsync(new SomeMessage());

// Or invoke remotely
await bus.EndpointFor("One").InvokeAsync(new SomeMessage());

// Or request/reply
var answer = bus.EndpointFor("One")
    .InvokeAsync<Answer>(new Question());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L56-L81' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_to_endpoint_by_name' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

or use `ToServerAndPort()` to send messages to a port on another machine:

<!-- snippet: sample_StaticPublishingRules -->
<a id='snippet-sample_StaticPublishingRules'></a>
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

            // Implementing a specific marker interface or common base class
            rule.MessagesImplementing<IEventMarker>();

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/StaticPublishingRule.cs#L12-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_StaticPublishingRules' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



