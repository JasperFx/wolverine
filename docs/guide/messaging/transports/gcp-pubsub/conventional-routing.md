# Conventional Message Routing

You can have Wolverine automatically determine message routing to GCP Pub/Sub
based on conventions as shown in the code snippet below. By default, this approach assumes that
each outgoing message type should be sent to topic named with the [message type name](/guide/messages.html#message-type-name-or-alias) for that
message type.

Likewise, Wolverine sets up a listener for a topic named similarly for each message type known
to be handled by the application.

<!-- snippet: sample_conventional_routing_for_pubsub -->
<a id='snippet-sample_conventional_routing_for_pubsub'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id")
            .UseConventionalRouting(convention =>
            {

                // Optionally override the default queue naming scheme
                convention.TopicNameForSender(t => t.Namespace)

                    // Optionally override the default queue naming scheme
                    .QueueNameForListener(t => t.Namespace)

                    // Fine tune the conventionally discovered listeners
                    .ConfigureListeners((listener, builder) =>
                    {
                        var messageType = builder.MessageType;
                        var runtime = builder.Runtime; // Access to basically everything

                        // customize the new queue
                        listener.CircuitBreaker(queue => { });

                        // other options...
                    })

                    // Fine tune the conventionally discovered sending endpoints
                    .ConfigureSending((subscriber, builder) =>
                    {
                        // Similarly, use the message type and/or wolverine runtime
                        // to customize the message sending
                    });
                    
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L135-L168' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_routing_for_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->