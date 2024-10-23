# Listening

Setting up Wolverine listeners and GCP Pub/Sub subscriptions for GCP Pub/Sub topics is shown below:

<!-- snippet: sample_listen_to_pubsub_topic -->
<a id='snippet-sample_listen_to_pubsub_topic'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id");

        opts.ListenToPubsubTopic("incoming1");

        opts.ListenToPubsubTopic("incoming2")

            // You can optimize the throughput by running multiple listeners
            // in parallel
            .ListenerCount(5)

            .ConfigurePubsubSubscription(options =>
            {

                // Optionally configure the subscription itself
                options.DeadLetterPolicy = new() {
                    DeadLetterTopic = "errors",
                    MaxDeliveryAttempts = 5
                };
                options.AckDeadlineSeconds = 60;
                options.RetryPolicy = new() {
                    MinimumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(1)),
                    MaximumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(10))
                };

            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L72-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listen_to_pubsub_topic' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
