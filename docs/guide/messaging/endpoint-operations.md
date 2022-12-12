# Endpoint Specific Operations

You can also explicitly send any message to a named endpoint in the system. You might
do this to programmatically distribute work in your system, or when you need to do more
programmatic routing as to what downstream system should handle the outgoing message.

Regardless, that usage is shown below. Just note that you can give a name to any type
of Wolverine endpoint:

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

var publisher = host.Services
    .GetRequiredService<IMessageBus>();

// Explicitly send a message to a named endpoint
await publisher.EndpointFor("One").SendAsync( new SomeMessage());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L54-L72' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_to_endpoint_by_name' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There's another option to reference a messaging endpoint by `Uri` as shown below:

snippet: sample_accessing_endpoint_by_uri
