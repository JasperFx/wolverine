# Publishing

Configuring Wolverine subscriptions through GCP Pub/Sub topics is done with the `ToPubsubTopic()` extension method shown in the example below:

<!-- snippet: sample_subscriber_rules_for_pubsub -->
<a id='snippet-sample_subscriber_rules_for_pubsub'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id");

        opts
            .PublishMessage<Message1>()
            .ToPubsubTopic("outbound1");

        opts
            .PublishMessage<Message2>()
            .ToPubsubTopic("outbound2")
            .ConfigurePubsubTopic(options =>
            {
                options.MessageRetentionDuration =
                    Duration.FromTimeSpan(TimeSpan.FromMinutes(10));
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L103-L124' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_subscriber_rules_for_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
