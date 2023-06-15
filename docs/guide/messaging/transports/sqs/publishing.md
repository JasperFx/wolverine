# Publishing

Configuring subscriptions through Amazon SQS queues is done with the `ToSqsQueue()` extension method 
shown in the example below:

<!-- snippet: sample_subscriber_rules_for_sqs -->
<a id='snippet-sample_subscriber_rules_for_sqs'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport();

        opts.PublishMessage<Message1>()
            .ToSqsQueue("outbound1");

        opts.PublishMessage<Message2>()
            .ToSqsQueue("outbound2").ConfigureQueueCreation(request =>
            {
                request.Attributes[QueueAttributeName.MaximumMessageSize] = "1024";
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L136-L154' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_subscriber_rules_for_sqs' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
