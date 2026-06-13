# Getting Started with Wolverine as Message Bus

::: tip
As of Wolverine 3.0, you can now connect to multiple Rabbit MQ brokers from one application. We will extend this support
to other message broker types in the future.
:::

There's certainly some value in Wolverine just being a command bus running inside of a single process, but now
it's time to utilize Wolverine to both publish and process messages received through external infrastructure like [Rabbit MQ](https://www.rabbitmq.com/)
or [Pulsar](https://pulsar.apache.org/).

## Terminology

To put this into perspective, here's how a Wolverine application could be connected to the outside world:

![Wolverine Messaging Architecture](/WolverineMessaging.png)

:::tip
The diagram above should just say "Message Handler" as Wolverine makes no structural differentiation between commands or events, but Jeremy is being too lazy to fix the diagram.
:::

## Configuring Messaging

There's a couple moving parts to using Wolverine as a messaging bus. You'll need to configure connectivity to external infrastructure like
Rabbit MQ brokers, set up listening endpoints, and create routing rules to teach Wolverine where and how to send your messages.

The [TCP transport](/guide/messaging/transports/tcp) is built in, and the ["local" in memory queues](/guide/messaging/transports/local) can be used like a transport, but you'll need to configure connectivity for
every other type of messaging transport adapter to external infrastructure. In all cases so far, the connectivity to external transports is done through
an extension method on `WolverineOptions` using the `Use[ToolName]()` idiom that is now common across .NET tools.

For an example, here's connecting to a Rabbit MQ broker:

<!-- snippet: sample_configuring_connection_to_rabbit_mq -->
<a id='snippet-sample_configuring_connection_to_rabbit_mq'></a>
```cs
using JasperFx;
using Wolverine;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
{
    // Using the Rabbit MQ URI specification: https://www.rabbitmq.com/uri-spec.html
    opts.UseRabbitMq(new Uri(builder.Configuration["rabbitmq"]!));

    // Or connect locally as you might for development purposes
    opts.UseRabbitMq();

    // Or do it more programmatically:
    opts.UseRabbitMq(rabbit =>
    {
        rabbit.HostName = builder.Configuration["rabbitmq_host"]!;
        rabbit.VirtualHost = builder.Configuration["rabbitmq_virtual_host"]!;
        rabbit.UserName = builder.Configuration["rabbitmq_username"]!;

        // and you get the point, you get full control over the Rabbit MQ
        // connection here for the times you need that
    });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/RabbitMqBootstrapping/Program.cs#L3-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_connection_to_rabbit_mq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Listening Endpoint Configuration

Once you've connected Wolverine to your external infrastructure, you'll want to configure listening endpoints so
that Wolverine can receive and process incoming messages. Listening endpoints control which queues, topics, or
subscriptions Wolverine reads from, how many messages are processed in parallel, and how received messages are
buffered or made durable.

See [Listener Configuration](/guide/messaging/listeners) for the full set of options, including parallelism,
durability, and endpoint-level error handling.

## Sending Endpoint Configuration

To publish messages out through external infrastructure, you'll configure sending endpoints and the routing
rules that teach Wolverine where each message type should be sent.

See [Subscriptions and Message Routing](/guide/messaging/subscriptions) for how to declare those rules, and
[The Message Bus](/guide/messaging/message-bus) for the `IMessageBus` API you use to publish, send, and invoke
messages from your application code.


