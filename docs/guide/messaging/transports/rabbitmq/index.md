# Using Rabbit MQ

::: tip
Wolverine uses the [Rabbit MQ .NET Client](https://www.rabbitmq.com/dotnet.html) to connect to Rabbit MQ.
:::

## Installing

All the code samples in this section are from the [Ping/Pong with Rabbit MQ sample project](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPongWithRabbitMq).

To use [RabbitMQ](http://www.rabbitmq.com/) as a transport with Wolverine, first install the `Wolverine.RabbitMQ` library via nuget to your project. Behind the scenes, this package uses the [RabbitMQ C# Client](https://www.rabbitmq.com/dotnet.html) to both send and receive messages from RabbitMQ.

<!-- snippet: sample_bootstrapping_rabbitmq -->
<a id='snippet-sample_bootstrapping_rabbitmq'></a>
```cs
return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        // Listen for messages coming into the pongs queue
        opts
            .ListenToRabbitQueue("pongs");

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L99-L122' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_only_use_listener_connection_with_rabbitmq' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L127-L150' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_only_use_sending_connection_with_rabbitmq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L81-L94' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_rabbit_mq_control_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Disable Rabbit MQ Reply Queues

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L52-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_rabbit_mq_system_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Of course, doing so means that you will not be able to do request/reply through Rabbit MQ with your Wolverine application.


