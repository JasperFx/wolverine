# Publishing

## Publish Directly to a Queue

In simple use cases, if you want to direct Wolverine to publish messages to a specific
queue without worrying about an exchange or binding, you have this syntax:

<!-- snippet: sample_publish_to_rabbitmq_queue -->
<a id='snippet-sample_publish_to_rabbitmq_queue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Connect to an unsecured, local Rabbit MQ broker
        // at port 5672
        opts.UseRabbitMq();

        opts.PublishAllMessages().ToRabbitQueue("outgoing")
            .UseDurableOutbox();

        // fine-tune the queue characteristics if Wolverine
        // will be governing the queue setup
        opts.PublishAllMessages().ToRabbitQueue("special", queue => { queue.IsExclusive = true; });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L194-L211' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_rabbitmq_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publish to an Exchange

To publish messages to a Rabbit MQ exchange with optional declaration of the
exchange, queue, and binding objects, use this syntax:

<!-- snippet: sample_publish_to_rabbitmq_exchange -->
<a id='snippet-sample_publish_to_rabbitmq_exchange'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Connect to an unsecured, local Rabbit MQ broker
        // at port 5672
        opts.UseRabbitMq();

        opts.PublishAllMessages().ToRabbitExchange("exchange1");

        // fine-tune the exchange characteristics if Wolverine
        // will be governing the queue setup
        opts.PublishAllMessages().ToRabbitExchange("exchange2", e =>
        {
            // Default is Fanout, so overriding that here
            e.ExchangeType = ExchangeType.Direct;

            // If you want, you can also create binding here too
            e.BindQueue("queue1", "exchange2ToQueue1");
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L216-L239' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_rabbitmq_exchange' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publish to a Routing Key

To publish messages directly to a known binding or routing key (and this actually works with queue
names as well just to be confusing here), use this syntax:

<!-- snippet: sample_publish_to_rabbitmq_routing_key -->
<a id='snippet-sample_publish_to_rabbitmq_routing_key'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq(rabbit => { rabbit.HostName = "localhost"; })
            // I'm declaring an exchange, a queue, and the binding
            // key that we're referencing below.
            // This is NOT MANDATORY, but rather just allows Wolverine to
            // control the Rabbit MQ object lifecycle
            .DeclareExchange("exchange1", ex => { ex.BindQueue("queue1", "key1"); })

            // This will direct Wolverine to create any missing Rabbit MQ exchanges,
            // queues, or binding keys declared in the application at application
            // start up time
            .AutoProvision();

        opts.PublishAllMessages().ToRabbitExchange("exchange1");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L244-L264' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_rabbitmq_routing_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

