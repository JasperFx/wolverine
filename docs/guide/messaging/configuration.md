# Configuring Messaging

There's a couple moving parts to using Wolverine as a messaging bus. You'll need to configure connectivity to external infrastructure like
Rabbit MQ brokers, set up listening endpoints, and create routing rules to teach Wolverine where and how to send your messages.


## Transport Connectivity

The [TCP transport](/guide/messaging/transports/tcp) is built in, and the ["local" in memory queues](/guide/in-memory-bus) can be used like a transport, but you'll need to configure connectivity for
every other type of messaging transport adapter to external infrastructure. In all cases so far, the connectivity to external transports is done through
an extension method on `WolverineOptions` using the `Use[ToolName]()` idiom that is now common across .NET tools.

For an example, here's connecting to a Rabbit MQ broker:

<!-- snippet: sample_configuring_connection_to_rabbit_mq -->
<a id='snippet-sample_configuring_connection_to_rabbit_mq'></a>
```cs
using Wolverine;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
{
    // Using the Rabbit MQ URI specification: https://www.rabbitmq.com/uri-spec.html
    opts.UseRabbitMq(new Uri(builder.Configuration["rabbitmq"]));

    // Or connect locally as you might for development purposes
    opts.UseRabbitMq();

    // Or do it more programmatically:
    opts.UseRabbitMq(rabbit =>
    {
        rabbit.HostName = builder.Configuration["rabbitmq_host"];
        rabbit.VirtualHost = builder.Configuration["rabbitmq_virtual_host"];
        rabbit.UserName = builder.Configuration["rabbitmq_username"];

        // and you get the point, you get full control over the Rabbit MQ
        // connection here for the times you need that
    });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/RabbitMqBootstrapping/Program.cs#L1-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_connection_to_rabbit_mq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Listening Endpoint Configuration


## Sending Endpoint Configuration




