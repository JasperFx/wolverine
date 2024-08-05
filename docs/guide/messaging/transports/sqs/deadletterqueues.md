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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L189-L210' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_dead_letter_queue_for_sqs' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Disabling All Native Dead Letter Queueing

In one stroke, you can disable all usage of native SQS queues for dead letter queueing with this 
syntax:

<!-- snippet: sample_disabling_all_sqs_dead_letter_queueing -->
<a id='snippet-sample_disabling_all_sqs_dead_letter_queueing'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransportLocally()
            // Disable all native SQS dead letter queueing
            .DisableAllNativeDeadLetterQueues()
            .AutoProvision();

        opts.ListenToSqsQueue("incoming");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Bugs/disabling_dead_letter_queue.cs#L17-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_all_sqs_dead_letter_queueing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This would force Wolverine to use any persistent envelope storage for dead letter queueing.



