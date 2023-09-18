# Dead Letter Queues

By default, Wolverine will try to move dead letter messages in SQS to a single, global queue named "wolverine-dead-letter-queue."

That can be overridden on a single queue at a time (or by conventions too of course) like:

<!-- snippet: sample_configuring_dead_letter_queue_for_sqs -->
<a id='snippet-sample_configuring_dead_letter_queue_for_sqs'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport();

        // No dead letter queueing
        opts.ListenToSqsQueue("incoming")
            .DisableDeadLetterQueueing();
        
        // Use a different dead letter queue
        opts.ListenToSqsQueue("important")
            .ConfigureDeadLetterQueue("important_errors", q =>
            {
                // optionally configure how the dead letter queue itself
                // is built by Wolverine
                q.MaxNumberOfMessages = 1000;
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L180-L201' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_dead_letter_queue_for_sqs' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



