# Using Amazon SNS

::: warning
At this moment, Wolverine cannot support request/reply mechanics (`IMessageBus.InvokeAsync<T>()`).
:::

:::tip
Due to the nature of SNS, Wolverine doesn't include any listening functionality for this transport. You may forward 
messages to Amazon SQS and use it in conjunction with the SQS transport to listen for incoming messages.
:::

Wolverine supports [Amazon SNS](https://aws.amazon.com/sns/) as a messaging transport through the WolverineFx.AmazonSns package.

## Connecting to the Broker

First, if you are using the [shared AWS config and credentials files](https://docs.aws.amazon.com/sdkref/latest/guide/file-format.html), the SNS connection is just this:

<!-- snippet: sample_simplistic_aws_sns_setup -->
<a id='snippet-sample_simplistic_aws_sns_setup'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // This does depend on the server having an AWS credentials file
        // See https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-files.html for more information
        opts.UseAmazonSnsTransport()

            // Let Wolverine create missing topics and subscriptions as necessary
            .AutoProvision();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L26-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_simplistic_aws_sns_setup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


<!-- snippet: sample_config_aws_sns_connection -->
<a id='snippet-sample_config_aws_sns_connection'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var config = builder.Configuration;

    opts.UseAmazonSnsTransport(snsConfig =>
        {
            snsConfig.ServiceURL = config["AwsUrl"];
            // And any other elements of the SNS AmazonSimpleNotificationServiceConfig
            // that you may need to configure
        })

        // Let Wolverine create missing topics and subscriptions as necessary
        .AutoProvision();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L44-L65' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_config_aws_sns_connection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


If you'd just like to connect to Amazon SNS running from within [LocalStack](https://localstack.cloud/) on your development box,
there's this helper:

<!-- snippet: sample_connect_to_sns_and_localstack -->
<a id='snippet-sample_connect_to_sns_and_localstack'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Connect to an SNS broker running locally
        // through LocalStack
        opts.UseAmazonSnsTransportLocally();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L11-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_connect_to_sns_and_localstack' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And lastly, if you want to explicitly supply an access and secret key for your credentials to SNS, you can use this syntax:

<!-- snippet: sample_setting_aws_sns_credentials -->
<a id='snippet-sample_setting_aws_sns_credentials'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var config = builder.Configuration;

    opts.UseAmazonSnsTransport(snsConfig =>
        {
            snsConfig.ServiceURL = config["AwsUrl"];
            // And any other elements of the SNS AmazonSimpleNotificationServiceConfig
            // that you may need to configure
        })

        // And you can also add explicit AWS credentials
        .Credentials(new BasicAWSCredentials(config["AwsAccessKey"], config["AwsSecretKey"]))

        // Let Wolverine create missing topics and subscriptions as necessary
        .AutoProvision();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L70-L94' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_aws_sns_credentials' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publishing

Configuring subscriptions through Amazon SNS topics is done with the `ToSnsTopic()` extension method
shown in the example below:

<!-- snippet: sample_subscriber_rules_for_sns -->
<a id='snippet-sample_subscriber_rules_for_sns'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSnsTransport();

        opts.PublishMessage<Message1>()
            .ToSnsTopic("outbound1")

            // Increase the outgoing message throughput, but at the cost
            // of strict ordering
            .MessageBatchMaxDegreeOfParallelism(Environment.ProcessorCount)
            .ConfigureTopicCreation(conf =>
            {
                // Configure topic creation request...
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L99-L118' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_subscriber_rules_for_sns' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Topic Subscriptions

Wolverine gives you the ability to automatically subscribe SQS Queues to SNS topics with it's auto-provision
feature through the `SubscribeSqsQueue()` extension method.

<!-- snippet: sample_sns_topic_subscriptions_creation -->
<a id='snippet-sample_sns_topic_subscriptions_creation'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSnsTransport()
            // Without this, the SubscribeSqsQueue() call does nothing
            // This tells Wolverine to try to ensure all topics, subscriptions,
            // and SQS queues exist at runtime 
            .AutoProvision()
            
            // *IF* you need to use some kind of custom queue policy in your
            // SQS queues *and* want to use AutoProvision() as well, this is 
            // the hook to customize that policy. This is the default though that
            // we're just showing for an example
            .QueuePolicyForSqsSubscriptions(description =>
            {
                return $$"""
                         {
                           "Version": "2012-10-17",
                           "Statement": [{
                               "Effect": "Allow",
                               "Principal": {
                                   "Service": "sns.amazonaws.com"
                               },
                               "Action": "sqs:SendMessage",
                               "Resource": "{{description.QueueArn}}",
                               "Condition": {
                                 "ArnEquals": {
                                     "aws:SourceArn": "{{description.TopicArn}}"
                                 }
                               }
                           }]
                         }
                         """;
            });

        opts.PublishMessage<Message1>()
            .ToSnsTopic("outbound1")
            // Sets a subscriptions to be
            .SubscribeSqsQueue("queueName",
                config =>
                {
                    // Configure subscription attributes
                    config.RawMessageDelivery = true;
                });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L123-L171' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sns_topic_subscriptions_creation' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
