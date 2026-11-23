# Dead Lettering

By default, Wolverine dead lettering is disabled for GCP Pub/Sub transport and Wolverine uses any persistent envelope storage for dead lettering. You can opt in to Wolverine dead lettering through GCP Pub/Sub globally as shown below.

When a message is moved to the dead letter topic, Wolverine stamps the standard diagnostic headers
(`exception-type`, `exception-message`, `exception-stack`, `failed-at`, `original-destination`) as Pub/Sub
message attributes, with the delivery attempt count on the standard `attempts` header. See
[diagnostic headers on dead letter messages](/tutorials/dead-letter-queues#diagnostic-headers-on-dead-letter-messages)
for the full cross-transport header structure.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L251-L272' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enable_wolverine_dead_lettering_for_pubsub' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L277-L299' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_wolverine_dead_lettering_for_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## When the dead letter topic can't be published to

Moving a message to the dead letter topic publishes a copy there. What happens to the original when that
publish fails depends on the endpoint mode:

| Mode | Failed dead letter publish |
|------|----------------------------|
| `ProcessInline()` and `NativeAck` | The publish is retried inline, 4 attempts over about 1.5 seconds. If none succeeds, or the host is stopping, the original is nacked and Pub/Sub redelivers it, so the handler runs again |
| `BufferedInMemory()` / `UseDurableInbox()` | The original was already acknowledged on receipt. The publish is retried in the background, at most 3 attempts, and none once the host is stopping |

On an inline or `NativeAck` endpoint a dead letter topic that stays unavailable therefore means repeated redelivery of the
failing message, and each redelivery counts toward the subscription's own `max_delivery_attempts` if you
configured a Pub/Sub dead letter policy on it.
