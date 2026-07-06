# Using Amazon SNS

::: warning
At this moment, Wolverine cannot support request/reply mechanics (`IMessageBus.InvokeAsync<T>()`) with SNS.
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L97-L109' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_simplistic_aws_sns_setup' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L114-L134' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_config_aws_sns_connection' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L83-L92' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_connect_to_sns_and_localstack' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L139-L162' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_aws_sns_credentials' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L167-L185' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_subscriber_rules_for_sns' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Connecting to Multiple Brokers <Badge type="tip" text="6.17" />

If you need to connect to more than one Amazon SNS broker (a different AWS account, region, or endpoint) from a single
Wolverine application, register additional, *named* brokers alongside the default one and pin publishing rules to a
specific broker by name:

<!-- snippet: sample_using_multiple_sns_brokers -->
<a id='snippet-sample_using_multiple_sns_brokers'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The "default" broker
        opts.UseAmazonSnsTransport();

        // Add a second, named Amazon SNS broker that connects to a
        // different account or region
        opts.AddNamedAmazonSnsBroker(new BrokerName("americas"), snsConfig =>
        {
            snsConfig.RegionEndpoint = RegionEndpoint.USEast1;
        }).AutoProvision();

        // Publish to a topic on the named broker. The Uri scheme for these
        // endpoints is the broker name, so you'd see "americas://colors".
        opts.PublishMessage<Message1>()
            .ToSnsTopicOnNamedBroker(new BrokerName("americas"), "colors");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L13-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_multiple_sns_brokers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that the `Uri` scheme within Wolverine for any endpoints from a "named" Amazon SNS broker is the name that you
supply for the broker. So in the example above, you would see `Uri` values like `americas://colors`.

::: tip
Because SNS is publish-only, the receiving side of a named SNS broker is handled by an SQS queue subscribed to the
topic. A named SNS broker and a same-named SQS broker cannot share a single `BrokerName` (Wolverine keys transports by
their `Uri` scheme, and the SNS broker owns that name), so run the SQS listener that drains the subscribed queue on the
*default* (or a differently-named) SQS broker. The SNS-side subscription provisioning uses its own paired SQS client
that targets the same account/region as the named SNS broker.
:::

## Multi-Tenancy with a Broker per Tenant <Badge type="tip" text="6.17" />

Wolverine can give each tenant its own dedicated Amazon SNS connection (a distinct AWS account/credentials, region, or
`ServiceURL`) while sharing one declared topic topology. Which connection a message is published to is decided at
runtime by the message's [tenant id](/guide/handlers/multi-tenancy), typically set through `DeliveryOptions.TenantId`:

<!-- snippet: sample_sns_broker_per_tenant -->
<a id='snippet-sample_sns_broker_per_tenant'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The "default" / shared SNS connection
        opts.UseAmazonSnsTransport(config =>
        {
            config.RegionEndpoint = RegionEndpoint.USEast1;
        })
            .AutoProvision()

            // How should Wolverine route a message whose TenantId is null or
            // unknown? FallbackToDefault (the default) uses the shared connection;
            // TenantIdRequired throws; IgnoreUnknownTenants silently drops it.
            .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

            // Each tenant gets its OWN dedicated SNS connection, but shares the
            // topic topology declared below. This tenant inherits the parent's
            // AWS credentials, and just re-points at its own region.
            .AddTenant("tenant-west", config =>
            {
                config.RegionEndpoint = RegionEndpoint.USWest2;
            })

            // Or give the tenant its own dedicated AWS account by supplying
            // its own credentials (optionally with its own region/endpoint too):
            .AddTenant("tenant-eu", new BasicAWSCredentials("tenant-eu-key", "tenant-eu-secret"),
                config =>
                {
                    config.RegionEndpoint = RegionEndpoint.EUWest1;
                });

        // SNS is publish-only, so broker-per-tenant means tenant-specific PUBLISHERS.
        // A message is published to the right tenant's connection at runtime by its
        // Envelope.TenantId (e.g. new DeliveryOptions { TenantId = "tenant-west" }).
        opts.PublishMessage<Message1>().ToSnsTopic("colors");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L39-L78' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sns_broker_per_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To route a specific message to a tenant's connection, stamp the tenant id on the send:

```csharp
await bus.SendAsync(new ColorMessage("blue"), new DeliveryOptions { TenantId = "tenant-west" });
```

::: warning SNS is publish-only
SNS has no listening surface in Wolverine, so broker-per-tenant support here means **tenant-specific publishers only** —
Wolverine wraps the outbound topic in a `TenantedSender` that dispatches on `Envelope.TenantId` to a per-tenant SNS
client. There is no per-tenant listener.

The *receiving* side is handled exactly as it is for a single-tenant SNS setup: by subscribing an SQS queue to the
tenant's topic and listening on the SQS side. For full per-tenant **consumption**, pair this with
[Amazon SQS broker-per-tenant](/guide/messaging/transports/sqs/) multi-tenancy so that each tenant's subscribed queue is
consumed on that tenant's own SQS connection. Each SNS tenant already provisions its topic subscription and queue policy
through its own paired SQS client, so the SNS and SQS tenant connections line up on the same account/region.
:::

`TenantIdBehavior(...)` controls what happens when a message has a null or unregistered tenant id:

* `FallbackToDefault` (the default) — publish it to the shared/default connection (the one passed to `UseAmazonSnsTransport`).
* `TenantIdRequired` — throw; every message must carry a known tenant id.
* `IgnoreUnknownTenants` — silently drop the message.

::: tip Named broker vs. broker-per-tenant
Use a **named broker** when a *fixed set of endpoints* should always publish to a *specific* broker. Use
**broker-per-tenant** when the *same logical topic* should be transparently routed to a *different connection per tenant*
based on the runtime tenant id. They are independent features and can be combined.
:::

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSns.Tests/Samples/Bootstrapping.cs#L190-L237' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sns_topic_subscriptions_creation' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Interoperability

::: tip
Also see the more generic [Wolverine Guide on Interoperability](/tutorials/interop)
:::

SNS interoperability is done through the `ISnsEnvelopeMapper`. At this point, SNS supports interoperability through
MassTransit, NServiceBus, CloudEvents, or user defined mapping strategies.

## URI reference

The `SnsEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `sns://{name}` | `SnsEndpointUri.Topic("name")` |

```csharp
using Wolverine.AmazonSns;

var uri = SnsEndpointUri.Topic("events");
// FIFO topic (suffix preserved verbatim):
var fifoUri = SnsEndpointUri.Topic("events.fifo");
```
