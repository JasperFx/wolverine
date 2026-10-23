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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L109-L125' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_simplistic_aws_sqs_setup' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L130-L154' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_config_aws_sqs_connection' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L95-L104' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_connect_to_sqs_and_localstack' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L159-L186' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_aws_credentials' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Connecting with Aspire or a Named Connection String <Badge type="tip" text="6.52" />

Let's say you're developing locally under [.NET Aspire](/guide/aspire) against an AWS emulator. The LocalStack
(`AddLocalStack()`) and CommunityToolkit Floci (`AddFlociAws()`) hosting integrations put the emulator's service URL,
something like `http://localhost:4566`, at `ConnectionStrings:<resource name>`:

```csharp
// In the AppHost
var aws = builder.AddLocalStack("aws");

builder.AddProject<Projects.MyService>("service")
    .WithReference(aws)
    .WaitFor(aws);
```

In your Wolverine service, you just use the resource name:

<!-- snippet: sample_amazon_sqs_using_named_connection -->
<a id='snippet-sample_amazon_sqs_using_named_connection'></a>
```cs
// "aws" is the name of the LocalStack resource in the Aspire AppHost,
// i.e. builder.AddLocalStack("aws"), referenced by this project
opts.UseAmazonSqsTransportUsingNamedConnection("aws")
    .AutoProvision();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/named_connection_5126.cs#L82-L89' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_amazon_sqs_using_named_connection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine applies that value as the `ServiceURL` of its `AmazonSQSConfig`, and unless you configure credentials
yourself, it uses the same placeholder credentials as `UseAmazonSqsTransportLocally()`. There's an optional
`Action<AmazonSQSConfig>` argument for anything else you need to configure. If you have a second emulator, there's a
named broker version too:

<!-- snippet: sample_amazon_sqs_named_broker_using_named_connection -->
<a id='snippet-sample_amazon_sqs_named_broker_using_named_connection'></a>
```cs
opts.UseAmazonSqsTransportUsingNamedConnection("aws");

// A second emulator, i.e. builder.AddLocalStack("aws2") in the AppHost
opts.AddNamedAmazonSqsBrokerUsingNamedConnection(new BrokerName("other"), "aws2");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/named_connection_5126.cs#L114-L121' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_amazon_sqs_named_broker_using_named_connection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
The named connection string is only for emulators. Real AWS doesn't have a connection string under Aspire at all.
`Aspire.Hosting.AWS` passes the profile and region to your service as the `AWS_PROFILE` and `AWS_REGION` environment
variables, and the AWS SDK already reads those on its own. For real AWS, use `UseAmazonSqsTransport()` or the
container option just below.
:::

If you'd rather set up the AWS SDK the normal .NET way with `services.AddAWSService<IAmazonSQS>()` from
AWSSDK.Extensions.NETCore.Setup, you can tell Wolverine to use the `IAmazonSQS` client from your IoC container
instead of building its own:

<!-- snippet: sample_amazon_sqs_with_client_from_container -->
<a id='snippet-sample_amazon_sqs_with_client_from_container'></a>
```cs
// Use the IAmazonSQS registered in the container, e.g. by
// services.AddAWSService<IAmazonSQS>()
opts.UseAmazonSqsTransportWithClientFromContainer()
    .AutoProvision();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/named_connection_5126.cs#L210-L217' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_amazon_sqs_with_client_from_container' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine resolves the client when the transport connects, and it does not dispose it because the container owns it.
`UseAmazonSqsTransportWithClientFromContainer()` takes an optional `Func<IServiceProvider, IAmazonSQS>` if you need
to pick out something like a keyed service, and `AddNamedAmazonSqsBrokerWithClientFromContainer(brokerName, clientSource)`
does the same thing for a named broker.

See [Aspire Integration](/guide/aspire) for the full picture.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L19-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_multiple_sqs_brokers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that the `Uri` scheme within Wolverine for any endpoints from a "named" Amazon SQS broker is the name that you supply
for the broker. So in the example above, you might see `Uri` values for `emea://colors` or `americas://red`.

## Multi-Tenancy with a Broker per Tenant <Badge type="tip" text="6.17" />

Named brokers (above) are a *static* topology: you pin specific endpoints to a specific broker by name at
configuration time. **Broker-per-tenant** is different — it is *runtime* routing. You declare one shared queue
topology, and each tenant is served by its **own dedicated SQS connection** (a distinct AWS account/credentials,
region, or `ServiceURL`). Which connection a message goes to (and which connection an inbound message came from) is
decided at runtime by the message's [tenant id](/guide/handlers/multi-tenancy), typically set through
`DeliveryOptions.TenantId`:

<!-- snippet: sample_sqs_broker_per_tenant -->
<a id='snippet-sample_sqs_broker_per_tenant'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The "default" / shared SQS connection
        opts.UseAmazonSqsTransport(config =>
        {
            config.RegionEndpoint = RegionEndpoint.USEast1;
        })
            .AutoProvision()

            // How should Wolverine route a message whose TenantId is null or
            // unknown? FallbackToDefault (the default) uses the shared connection;
            // TenantIdRequired throws; IgnoreUnknownTenants silently drops it.
            .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

            // Each tenant gets its OWN dedicated SQS connection, but shares the
            // queue topology declared below. This tenant inherits the parent's
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

        // One shared topology; messages are routed to the right connection at
        // runtime by Envelope.TenantId (e.g. new DeliveryOptions { TenantId = "tenant-west" }).
        opts.PublishMessage<SenderConfigurationTests.ColorMessage>().ToSqsQueue("colors");
        opts.ListenToSqsQueue("colors");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L52-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqs_broker_per_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To route a specific message to a tenant's connection, stamp the tenant id on the send:

```csharp
await bus.SendAsync(new ColorMessage("blue"), new DeliveryOptions { TenantId = "tenant-west" });
```

Wolverine wraps the outbound endpoint in a `TenantedSender` that dispatches on `Envelope.TenantId`, and builds a
compound listener that runs one poller per tenant connection — each inbound envelope is stamped with the tenant id
of the connection it was consumed from. This mirrors the
[RabbitMQ](/guide/messaging/transports/rabbitmq/multi-tenancy) and [Azure Service Bus](/guide/messaging/transports/azureservicebus/multitenancy)
broker-per-tenant support.

::: tip Named broker vs. broker-per-tenant
Use a **named broker** when a *fixed set of endpoints* should always talk to a *specific* broker. Use
**broker-per-tenant** when the *same logical endpoints* should be transparently routed to a *different connection per
tenant* based on the runtime tenant id. They are independent features and can be combined.
:::

### How a tenant connection is seeded

Each tenant owns its own child transport — its own `IAmazonSQS` client *and* its own queue-url cache (which is why
tenants can safely share a queue name without their cached `QueueUrl` values colliding). At startup Wolverine seeds
the tenant's connection from the parent's — AWS credentials, region/`ServiceURL`, `AuthenticationRegion`,
auto-provisioning, and the dead-letter-queue configuration — and *then* applies your `AddTenant(...)` overrides, so a
tenant only re-points the axes it actually sets and inherits everything else.

### Choosing the unknown-tenant behavior

`TenantIdBehavior(...)` controls what happens when a message has a null or unregistered tenant id:

* `FallbackToDefault` (the default) — route it to the shared/default connection (the one passed to `UseAmazonSqsTransport`).
* `TenantIdRequired` — throw; every message must carry a known tenant id.
* `IgnoreUnknownTenants` — silently drop the message.

### Auto-provisioning per connection

When [`AutoProvision()`](#) is enabled, Wolverine provisions the shared queue topology — including each queue's dead
letter queue — on **every** tenant connection, not just the default one, since each is an independent broker. A tenant
listener's dead-lettered messages are likewise sent to the dead letter queue on that tenant's own connection.

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

## Global Partitioning

Amazon SQS queues can be used as the external transport for [global partitioned messaging](/guide/messaging/partitioning#global-partitioning). This creates a set of sharded SQS queues with companion local queues for sequential processing across a multi-node cluster.

Use `UseShardedAmazonSqsQueues()` within a `GlobalPartitioned()` configuration:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport().AutoProvision();

        opts.MessagePartitioning.ByMessage<IMyMessage>(x => x.GroupId);

        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            // Creates 4 sharded SQS queues named "orders1" through "orders4"
            // with matching companion local queues for sequential processing
            topology.UseShardedAmazonSqsQueues("orders", 4);
            topology.MessagesImplementing<IMyMessage>();
        });
    }).StartAsync();
```

This creates SQS queues named `orders1` through `orders4` with companion local queues `global-orders1` through `global-orders4`. Messages are routed to the correct shard based on their group id, and Wolverine handles the coordination between nodes automatically.

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

## URI reference

The `SqsEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `sqs://{name}` | `SqsEndpointUri.Queue("name")` |

```csharp
using Wolverine.AmazonSqs;

var uri = SqsEndpointUri.Queue("orders");
// FIFO queue (suffix preserved verbatim):
var fifoUri = SqsEndpointUri.Queue("orders.fifo");
```
