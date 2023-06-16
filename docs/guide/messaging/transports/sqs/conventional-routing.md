# Conventional Message Routing

As an example, you can apply conventional routing with the Amazon SQS transport like so:

<!-- snippet: sample_using_conventional_sqs_routing -->
<a id='snippet-sample_using_conventional_sqs_routing'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()
            .UseConventionalRouting();

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L162-L172' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_conventional_sqs_routing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this case any outgoing message types that aren't handled locally or have an explicit subscription will be automatically routed
to an Amazon SQS queue named after the Wolverine message type name of the message type.


