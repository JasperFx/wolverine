# Using Amazon SQS

::: warning
At this moment, Wolverine cannot support request/reply mechanics (`IMessageBus.InvokeAsync<T>()`) with Amazon SQS.
:::

Wolverine supports [Amazon SQS](https://aws.amazon.com/sqs/) as a messaging transport through the WolverineFx.AmazonSqs package.

## Connecting to the Broker

First, if you are using the [shared AWS config and credentials files](https://docs.aws.amazon.com/sdkref/latest/guide/file-format.html), the SQS connection is just this:

<!-- snippet: sample_simplistic_aws_sqs_setup -->
<a id='snippet-sample_simplistic_aws_sqs_setup'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // This does depend on the server having an AWS credentials file
        // See https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-files.html for more information
        opts.UseAmazonSqsTransport()

            // Let Wolverine create missing queues as necessary
            .AutoProvision()

            // Optionally purge all queues on application startup.
            // Warning though, this is potentially slow
            .AutoPurgeOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L66-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_simplistic_aws_sqs_setup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


<!-- snippet: sample_config_aws_sqs_connection -->
<a id='snippet-sample_config_aws_sqs_connection'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var config = builder.Configuration;

    opts.UseAmazonSqsTransport(sqsConfig =>
        {
            sqsConfig.ServiceURL = config["AwsUrl"];
            // And any other elements of the SQS AmazonSQSConfig
            // that you may need to configure
        })

        // Let Wolverine create missing queues as necessary
        .AutoProvision()

        // Optionally purge all queues on application startup.
        // Warning though, this is potentially slow
        .AutoPurgeOnStartup();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L88-L113' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_config_aws_sqs_connection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


If you'd just like to connect to Amazon SQS running from within [LocalStack](https://localstack.cloud/) on your development box,
there's this helper:

<!-- snippet: sample_connect_to_sqs_and_localstack -->
<a id='snippet-sample_connect_to_sqs_and_localstack'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Connect to an SQS broker running locally
        // through LocalStack
        opts.UseAmazonSqsTransportLocally();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L51-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_connect_to_sqs_and_localstack' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And lastly, if you want to explicitly supply an access and secret key for your credentials to SQS, you can use this syntax:

<!-- snippet: sample_setting_aws_credentials -->
<a id='snippet-sample_setting_aws_credentials'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var config = builder.Configuration;

    opts.UseAmazonSqsTransport(sqsConfig =>
        {
            sqsConfig.ServiceURL = config["AwsUrl"];
            // And any other elements of the SQS AmazonSQSConfig
            // that you may need to configure
        })

        // And you can also add explicit AWS credentials
        .Credentials(new BasicAWSCredentials(config["AwsAccessKey"], config["AwsSecretKey"]))

        // Let Wolverine create missing queues as necessary
        .AutoProvision()

        // Optionally purge all queues on application startup.
        // Warning though, this is potentially slow
        .AutoPurgeOnStartup();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L118-L146' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_aws_credentials' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Connecting to Multiple Brokers <Badge type="tip" text="4.7" />

Wolverine supports interacting with multiple Amazon SQS brokers within one application like this:

<!-- snippet: sample_using_multiple_sqs_brokers -->
<a id='snippet-sample_using_multiple_sqs_brokers'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport(config =>
        {
            // Add configuration for connectivity
        });
        
        opts.AddNamedAmazonSqsBroker(new BrokerName("americas"), config =>
        {
            // Add configuration for connectivity
        });
        
        opts.AddNamedAmazonSqsBroker(new BrokerName("emea"), config =>
        {
            // Add configuration for connectivity
        });

        // Or explicitly make subscription rules
        opts.PublishMessage<SenderConfigurationTests.ColorMessage>()
            .ToSqsQueueOnNamedBroker(new BrokerName("emea"), "colors");

        // Listen to topics
        opts.ListenToSqsQueueOnNamedBroker(new BrokerName("americas"), "red");
        // Other configuration
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L17-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_multiple_sqs_brokers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that the `Uri` scheme within Wolverine for any endpoints from a "named" Amazon SQS broker is the name that you supply
for the broker. So in the example above, you might see `Uri` values for `emea://colors` or `americas://red`.
