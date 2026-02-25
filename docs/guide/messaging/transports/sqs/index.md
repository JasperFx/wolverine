# Using Amazon SQS

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

## Identifier Prefixing for Shared Brokers

When sharing a single AWS account or SQS namespace between multiple developers or development environments, you can use `PrefixIdentifiers()` to automatically prepend a prefix to every queue name created by Wolverine. This helps isolate cloud resources for each developer or environment:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()
            .AutoProvision()

            // Prefix all queue names with "dev-john-"
            .PrefixIdentifiers("dev-john");

        // A queue named "orders" becomes "dev-john-orders"
        opts.ListenToSqsQueue("orders");
    }).StartAsync();
```

You can also use `PrefixIdentifiersWithMachineName()` as a convenience to use the current machine name as the prefix:

```csharp
opts.UseAmazonSqsTransport()
    .AutoProvision()
    .PrefixIdentifiersWithMachineName();
```

The default delimiter between the prefix and the original name is `-` for Amazon SQS (e.g., `dev-john-orders`).

## Request/Reply <Badge type="tip" text="5.14" />

[Request/reply](https://www.enterpriseintegrationpatterns.com/patterns/messaging/RequestReply.html) mechanics (`IMessageBus.InvokeAsync<T>()`) are supported with the Amazon SQS transport when system queues are enabled. Wolverine creates a dedicated per-node response queue named like `wolverine-response-[service name]-[node id]` that is used to receive replies.

To enable request/reply support, call `EnableSystemQueues()` on the SQS transport configuration:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()
            .AutoProvision()

            // Enable system queues for request/reply support
            .EnableSystemQueues();
    }).StartAsync();
```

::: tip
Unlike Azure Service Bus and RabbitMQ where system queues are enabled by default, SQS system queues require explicit opt-in via `EnableSystemQueues()`. This is because creating SQS queues requires IAM permissions that your application may not have.
:::

System queues are automatically cleaned up when your application shuts down. Wolverine also tags each system queue with a `wolverine:last-active` timestamp and runs a background keep-alive timer. On startup, Wolverine scans for orphaned system queues (from crashed nodes) with the `wolverine-response-` or `wolverine-control-` prefix and deletes any that have been inactive for more than 5 minutes.

## Wolverine Control Queues <Badge type="tip" text="5.14" />

You can opt into using SQS queues for intra-node communication that Wolverine needs for leader election and background worker distribution. Using SQS for this feature is more efficient than the built-in database control queues that Wolverine uses otherwise, and is necessary for message storage options like RavenDb that do not have a built-in control queue mechanism.

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()
            .AutoProvision()

            // This enables Wolverine to use SQS queues
            // created at runtime for communication between
            // Wolverine nodes
            .EnableWolverineControlQueues();
    }).StartAsync();
```

Calling `EnableWolverineControlQueues()` implicitly enables system queues and request/reply support as well.

## Disabling System Queues <Badge type="tip" text="5.14" />

If your application does not have IAM permissions to create or delete queues, you can explicitly disable system queues:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()
            .AutoProvision()
            .SystemQueuesAreEnabled(false);

        opts.ListenToSqsQueue("send-and-receive");
        opts.PublishAllMessages().ToSqsQueue("send-and-receive");
    }).StartAsync();
```
