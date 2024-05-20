# Listening

Setting up a Wolverine listener for an SQS queue is shown below:

<!-- snippet: sample_listen_to_sqs_queue -->
<a id='snippet-sample_listen_to_sqs_queue'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()

            // Let Wolverine create missing queues as necessary
            .AutoProvision()

            // Optionally purge all queues on application startup.
            // Warning though, this is potentially slow
            .AutoPurgeOnStartup();

        opts.ListenToSqsQueue("incoming", queue =>
        {
            queue.Configuration.Attributes[QueueAttributeName.DelaySeconds]
                = "5";

            queue.Configuration.Attributes[QueueAttributeName.MessageRetentionPeriod]
                = 4.Days().TotalSeconds.ToString();
        })
            // You can optimize the throughput by running multiple listeners
            // in parallel
            .ListenerCount(5);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L110-L137' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listen_to_sqs_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
