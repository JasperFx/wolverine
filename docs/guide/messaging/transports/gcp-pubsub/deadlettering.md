# Dead Lettering

By default, Wolverine dead lettering is disabled for GCP Pub/Sub transport and Wolverine uses any persistent envelope storage for dead lettering. You can opt in to Wolverine dead lettering through GCP Pub/Sub globally as shown below.

<!-- snippet: sample_enable_wolverine_dead_lettering_for_pubsub -->
<a id='snippet-sample_enable_wolverine_dead_lettering_for_pubsub'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id")

            // Enable dead lettering for all Wolverine endpoints
            .EnableDeadLettering(
                // Optionally configure how the GCP Pub/Sub dead letter itself
                // is created by Wolverine
                options =>
                {
                    options.Topic.MessageRetentionDuration =
                        Duration.FromTimeSpan(TimeSpan.FromDays(14));

                    options.Subscription.MessageRetentionDuration =
                        Duration.FromTimeSpan(TimeSpan.FromDays(14));
                }
            );
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L169-L191' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enable_wolverine_dead_lettering_for_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When enabled, Wolverine will try to move dead letter messages in GCP Pub/Sub to a single, global topic named "wlvrn.dead-letter".

That can be overridden on a single endpoint at a time (or by conventions too of course) like:

<!-- snippet: sample_configuring_wolverine_dead_lettering_for_pubsub -->
<a id='snippet-sample_configuring_wolverine_dead_lettering_for_pubsub'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id")
            .EnableDeadLettering();

        // No dead letter queueing
        opts.ListenToPubsubTopic("incoming")
            .DisableDeadLettering();

        // Use a different dead letter queue
        opts.ListenToPubsubTopic("important")
            .ConfigureDeadLettering(
                "important_errors",

                // Optionally configure how the dead letter itself
                // is built by Wolverine
                e => { e.TelemetryEnabled = true; }
            );
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L196-L219' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_wolverine_dead_lettering_for_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
