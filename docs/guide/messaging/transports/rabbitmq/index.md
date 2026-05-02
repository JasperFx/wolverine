# Using Rabbit MQ

::: tip
Wolverine uses the [Rabbit MQ .NET Client](https://www.rabbitmq.com/dotnet.html) to connect to Rabbit MQ.
:::

## Installing

All the code samples in this section are from the [Ping/Pong with Rabbit MQ sample project](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPongWithRabbitMq).

To use [RabbitMQ](http://www.rabbitmq.com/) as a transport with Wolverine, first install the `WolverineFX.RabbitMQ` library via nuget to your project. Behind the scenes, this package uses the [RabbitMQ C# Client](https://www.rabbitmq.com/dotnet.html) to both send and receive messages from RabbitMQ.

<!-- snippet: sample_bootstrapping_rabbitmq -->
<a id='snippet-sample_bootstrapping_rabbitmq'></a>
```cs
return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        opts.ApplicationAssembly = typeof(Program).Assembly;

        // Listen for messages coming into the pongs queue
        opts.ListenToRabbitQueue("pongs");

        // Publish messages to the pings queue
        opts.PublishMessage<PingMessage>().ToRabbitExchange("pings");

        // Configure Rabbit MQ connection to the connection string
        // named "rabbit" from IConfiguration. This is *a* way to use
        // Wolverine + Rabbit MQ using Aspire
        opts.UseRabbitMqUsingNamedConnection("rabbit")
            // Directs Wolverine to build any declared queues, exchanges, or
            // bindings with the Rabbit MQ broker as part of bootstrapping time
            .AutoProvision();

        // Or you can use this functionality to set up *all* known
        // Wolverine (or Marten) related resources on application startup
        opts.Services.AddResourceSetupOnStartup();

        // This will send ping messages on a continuous
        // loop
        opts.Services.AddHostedService<PingerService>();
    }).RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPongWithRabbitMq/Pinger/Program.cs#L7-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_rabbitmq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See the [Rabbit MQ .NET Client documentation](https://www.rabbitmq.com/dotnet-api-guide.html#connecting) for more information about configuring the `ConnectionFactory` to connect to Rabbit MQ.


## Managing Rabbit MQ Connections

In its default setup, the Rabbit MQ transport in Wolverine will open two connections, one for listening and another for sending
messages. All Rabbit MQ endpoints will share these two connections. If you need to conserve Rabbit MQ connections
and have a process that is only sending or only receiving messages through Rabbit MQ, you can opt to turn off one or the 
other connections that might not be used at runtime.

To only listen to Rabbit MQ messages, but never send them:

<!-- snippet: sample_only_use_listener_connection_with_rabbitmq -->
<a id='snippet-sample_only_use_listener_connection_with_rabbitmq'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // *A* way to configure Rabbit MQ using their Uri schema
        // documented here: https://www.rabbitmq.com/uri-spec.html
        opts.UseRabbitMq(new Uri("amqp://localhost"))

            // Turn on listener connection only in case if you only need to listen for messages
            // The sender connection won't be activated in this case
            .UseListenerConnectionOnly();

        // Set up a listener for a queue, but also
        // fine-tune the queue characteristics if Wolverine
        // will be governing the queue setup
        opts.ListenToRabbitQueue("incoming2", q =>
        {
            q.PurgeOnStartup = true;
            q.TimeToLive(5.Minutes());
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L97-L119' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_only_use_listener_connection_with_rabbitmq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To only send Rabbit MQ messages, but never receive them:

<!-- snippet: sample_only_use_sending_connection_with_rabbitmq -->
<a id='snippet-sample_only_use_sending_connection_with_rabbitmq'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // *A* way to configure Rabbit MQ using their Uri schema
        // documented here: https://www.rabbitmq.com/uri-spec.html
        opts.UseRabbitMq(new Uri("amqp://localhost"))

            // Turn on sender connection only in case if you only need to send messages
            // The listener connection won't be created in this case
            .UseSenderConnectionOnly();

        // Set up a listener for a queue, but also
        // fine-tune the queue characteristics if Wolverine
        // will be governing the queue setup
        opts.ListenToRabbitQueue("incoming2", q =>
        {
            q.PurgeOnStartup = true;
            q.TimeToLive(5.Minutes());
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L124-L146' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_only_use_sending_connection_with_rabbitmq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Connecting to a RabbitMQ cluster

If you run RabbitMQ in a high-availability cluster, declare each node via
`AddClusterNode`. Wolverine forwards the list to the RabbitMQ.NET client,
which selects a node and transparently fails over to another if the
chosen node becomes unreachable.

<!-- snippet: sample_rabbit_mq_cluster_nodes -->
<!-- endSnippet -->

`AddClusterNode(host, port)` copies the TLS settings configured on the
`ConnectionFactory` onto the new endpoint, so a homogeneous cluster only
needs `Ssl` configured once. To override per node — for example, with
distinct certificates — pass an
[`AmqpTcpEndpoint`](https://www.rabbitmq.com/client-libraries/dotnet-api-guide#endpoints)
directly:

```csharp
opts.UseRabbitMq(f => { f.UserName = "guest"; f.Password = "guest"; })
    .AddClusterNode(new AmqpTcpEndpoint("rabbit-1.local", 5671, new SslOption
    {
        Enabled = true,
        ServerName = "rabbit-1.local",
        CertPath = "/etc/wolverine/rabbit-1.pem"
    }));
```

Multi-tenant configurations that share a cluster (i.e. tenants separated
by virtual host via `AddTenant(tenantId, virtualHostName)`) inherit the
parent transport's cluster nodes automatically. Tenants configured via
`AddTenant(tenantId, Uri)` or `AddTenant(tenantId, Action<ConnectionFactory>)`
do **not** inherit the cluster — those overloads are intended for tenants
on separate brokers and bring their own connection settings. Put differently:
virtual-host tenants share the same broker and therefore the same cluster
topology; URI- and Action-based tenants are explicitly pointed at a
different broker, so inheriting cluster nodes from the parent would be
wrong.

## Aspire Integration

::: tip
See the full [Aspire + Wolverine RabbitMQ sample](https://github.com/JasperFx/wolverine/tree/main/src/Samples/AspireWithRabbitMq) for a working end-to-end example.
:::

The recommended way to integrate Wolverine with .NET Aspire for RabbitMQ is the `UseRabbitMqUsingNamedConnection()` overload.
Aspire injects the RabbitMQ connection string (a `amqp://...` URI) via the standard `ConnectionStrings__rabbitmq` environment variable when you use `.WithReference()` in the AppHost:

**AppHost:**
```csharp
// Aspire.Hosting.RabbitMQ NuGet package
var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin();

builder.AddProject<Projects.MyWorker>("worker")
    .WithReference(rabbitmq)
    // WaitFor ensures RabbitMQ is healthy before your service starts,
    // so AutoProvision() will always succeed.
    .WaitFor(rabbitmq);
```

**Service project:**
```csharp
// WolverineFx.RabbitMQ NuGet package — no Aspire.RabbitMQ.Client needed
builder.UseWolverine(opts =>
{
    opts.UseRabbitMqUsingNamedConnection("rabbitmq")
        // AutoProvision creates all declared exchanges, queues, and bindings
        // at startup. This works reliably because Aspire's WaitFor() guarantees
        // RabbitMQ is healthy before the service starts.
        .AutoProvision();

    opts.ListenToRabbitQueue("my-queue");
    opts.PublishMessage<MyMessage>().ToRabbitExchange("my-exchange");
});
```

`UseRabbitMqUsingNamedConnection` reads from `IConfiguration.GetConnectionString("rabbitmq")`.
Aspire populates this automatically — it handles both URI-format strings (e.g., `amqp://guest:guest@localhost:5672`)
and the key=value format.

### Alternative: Using Aspire.RabbitMQ.Client

If you install the `Aspire.RabbitMQ.Client` NuGet package in your service project and call `AddRabbitMQClient("rabbitmq")`,
Aspire registers an `IConnectionFactory` in DI. Wolverine's no-argument `UseRabbitMq()` overload will automatically 
detect and use it:

```csharp
// In service project with Aspire.RabbitMQ.Client installed:
builder.AddRabbitMQClient("rabbitmq");

builder.UseWolverine(opts =>
{
    // Wolverine finds IConnectionFactory from DI automatically
    opts.UseRabbitMq()
        .AutoProvision();
});
```

### AutoProvision with Aspire

`AutoProvision()` works correctly with Aspire as long as you use `.WaitFor(rabbitmq)` in the AppHost. This tells Aspire not
to start your service until the RabbitMQ container health check passes, ensuring Wolverine can connect and declare all
exchanges, queues, and bindings before processing begins.

## Enable Rabbit MQ for Wolverine Control Queues

If you are using Wolverine in a cluster of running nodes -- and it's more likely that you are than not if you have any
kind of non trivial load -- Wolverine needs to communicate between its running nodes for various reasons if you are using
any kind of message persistence. Normally that communication is done through little, specialized database queueing (crude polling),
but there's an option to use more efficient Rabbit MQ queues for that inter-node communication with a non-durable Rabbit MQ
queue for each node with this option:

<!-- snippet: sample_using_rabbit_mq_control_queues -->
<a id='snippet-sample_using_rabbit_mq_control_queues'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // *A* way to configure Rabbit MQ using their Uri schema
        // documented here: https://www.rabbitmq.com/uri-spec.html
        opts.UseRabbitMq(new Uri("amqp://localhost"))

            // Use Rabbit MQ for inter-node communication
            .EnableWolverineControlQueues();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L80-L92' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_rabbit_mq_control_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Disable Rabbit MQ Reply Queues

::: info
The response queues (and system queues) are now created as durable Rabbit MQ queues with a TTL expiration of 30 minutes
after there is no connection for these queues. 
:::

By default, Wolverine creates an in memory queue in the Rabbit MQ broker for each individual node that is used by Wolverine
for request/reply invocations (`IMessageBus.InvokeAsync<T>()` when used remotely). Great, but if your process does not
have permissions with your Rabbit MQ broker to create queues, you may encounter errors. Not to worry, you can disable
that Wolverine system queue creation with:

<!-- snippet: sample_disable_rabbit_mq_system_queue -->
<a id='snippet-sample_disable_rabbit_mq_system_queue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // *A* way to configure Rabbit MQ using their Uri schema
        // documented here: https://www.rabbitmq.com/uri-spec.html
        opts.UseRabbitMq(new Uri("amqp://localhost"))

            // Stop Wolverine from trying to create a reply queue
            // for this node if your process does not have permission to
            // do so against your Rabbit MQ broker
            .DisableSystemRequestReplyQueueDeclaration();

        // Set up a listener for a queue, but also
        // fine-tune the queue characteristics if Wolverine
        // will be governing the queue setup
        opts.ListenToRabbitQueue("incoming2", q =>
        {
            q.PurgeOnStartup = true;
            q.TimeToLive(5.Minutes());
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L52-L75' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_rabbit_mq_system_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Of course, doing so means that you will not be able to do request/reply through Rabbit MQ with your Wolverine application.

## Configuring Channel Creation <Badge type="tip" text="5.10" />

You now have the ability to fine tune how the [Rabbit MQ channels](https://www.rabbitmq.com/docs/channels~~~~) are created by Wolverine through
this syntax:

<!-- snippet: sample_configuring_rabbit_mq_channel_creation -->
<a id='snippet-sample_configuring_rabbit_mq_channel_creation'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts
        .UseRabbitMq(builder.Configuration.GetConnectionString("rabbitmq")!)

        // Fine tune how the underlying Rabbit MQ channels from
        // this application will behave
        .ConfigureChannelCreation(o =>
        {
            o.PublisherConfirmationsEnabled = true;
            o.PublisherConfirmationTrackingEnabled = true;
            o.ConsumerDispatchConcurrency = 5;
        });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/channel_configuration.cs#L13-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_rabbit_mq_channel_creation' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Global Partitioning

RabbitMQ queues can be used as the external transport for [global partitioned messaging](/guide/messaging/partitioning#global-partitioning). This creates a set of sharded RabbitMQ queues with companion local queues for sequential processing across a multi-node cluster.

Use `UseShardedRabbitQueues()` within a `GlobalPartitioned()` configuration:

<!-- snippet: sample_global_partitioned_with_rabbit_mq -->
<a id='snippet-sample_global_partitioned_with_rabbit_mq'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq();

        // Do something to add Saga storage too!

        opts
            .MessagePartitioning

            // This tells Wolverine to "just" use implied
            // message grouping based on Saga identity among other things
            .UseInferredMessageGrouping()

            .GlobalPartitioned(topology =>
            {
                // Creates 5 sharded RabbitMQ queues named "sequenced1" through "sequenced5"
                // with matching companion local queues for sequential processing
                topology.UseShardedRabbitQueues("sequenced", 5);
                topology.MessagesImplementing<MySequencedCommand>();

            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L694-L720' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_global_partitioned_with_rabbit_mq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This creates RabbitMQ queues named `sequenced1` through `sequenced5` with companion local queues `global-sequenced1` through `global-sequenced5`. Messages are routed to the correct shard based on their group id, and Wolverine handles the coordination between nodes automatically.

## Compatibility Note

::: info
Wolverine with the `WolverineFX.RabbitMQ` transport has also been verified to work against [LavinMQ](https://lavinmq.com/), a modern RabbitMQ-protocol compatible message broker, using the RabbitMQ transport with 100% protocol compatibility when configured through the standard RabbitMQ integration shown above.
:::

## URI reference

The `RabbitMqEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `rabbitmq://queue/{name}` | `RabbitMqEndpointUri.Queue("name")` |
| `rabbitmq://exchange/{name}` | `RabbitMqEndpointUri.Exchange("name")` |
| `rabbitmq://topic/{exchange}/{routingKey}` | `RabbitMqEndpointUri.Topic("ex", "key")` |
| `rabbitmq://exchange/{exchange}/routing/{routingKey}` | `RabbitMqEndpointUri.Routing("ex", "key")` |

```csharp
using Wolverine.RabbitMQ;

var uri = RabbitMqEndpointUri.Queue("orders");
// new Uri("rabbitmq://queue/orders")
```
