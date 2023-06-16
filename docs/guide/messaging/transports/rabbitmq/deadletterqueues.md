# Dead Letter Queues

::: info
The end result is the same regardless, but Wolverine bypasses this functionality to move messages
to the dead letter queue in `Buffered` or `Durable` queue endpoints.
:::

By default, Wolverine's Rabbit MQ transport supports the [native dead letter exchange](https://www.rabbitmq.com/dlx.html) 
functionality in Rabbit MQ itself. If running completely with default behavior, Wolverine will:

* Declare a queue named `wolverine-dead-letter-queue` as the system dead letter queue for the entire application -- but don't worry, that can be overridden queue by queue
* Add the `x-dead-letter-exchange` argument to each non-system queue created by Wolverine in Rabbit MQ

Great, but someone will inevitably want to alter the dead letter queue functionality to use differently named queues like so:

<!-- snippet: sample_overriding_rabbit_mq_dead_letter_queue -->
<a id='snippet-sample_overriding_rabbit_mq_dead_letter_queue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Use a different default deal letter queue name
        opts.UseRabbitMq()
            .CustomizeDeadLetterQueueing(new DeadLetterQueue("error-queue"))

            // or conventionally
            .ConfigureListeners(l =>
            {
                l.DeadLetterQueueing(new DeadLetterQueue($"{l.QueueName}-errors"));
            });
            

        // Use a different dead letter queue for this specific queue
        opts.ListenToRabbitQueue("incoming")
            .DeadLetterQueueing(new DeadLetterQueue("incoming-errors"));

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L248-L270' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_overriding_rabbit_mq_dead_letter_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
You will need this if you are interoperating against NServiceBus!
:::

But wait there's more! Other messaging tools or previous usages of Rabbit MQ in your environment may have already declared
the Rabbit MQ queues without the `x-dead-letter-exchange` argument, meaning that Wolverine will not be able to declare queues
for you, or might do so in a way that interferes with *other* messaging tools. To avoid all that hassle, you can opt out
of native Rabbit MQ dead letter queues with the `InteropFriendly` option:

<!-- snippet: sample_overriding_rabbit_mq_dead_letter_queue_interop_friendly -->
<a id='snippet-sample_overriding_rabbit_mq_dead_letter_queue_interop_friendly'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Use a different default deal letter queue name
        opts.UseRabbitMq()
            .CustomizeDeadLetterQueueing(new DeadLetterQueue("error-queue", DeadLetterQueueMode.InteropFriendly))

            // or conventionally
            .ConfigureListeners(l =>
            {
                l.DeadLetterQueueing(new DeadLetterQueue($"{l.QueueName}-errors", DeadLetterQueueMode.InteropFriendly));
            });
            

        // Use a different dead letter queue for this specific queue
        opts.ListenToRabbitQueue("incoming")
            .DeadLetterQueueing(new DeadLetterQueue("incoming-errors", DeadLetterQueueMode.InteropFriendly));

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L275-L297' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_overriding_rabbit_mq_dead_letter_queue_interop_friendly' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And lastly, if you don't particularly want to have any Rabbit MQ dead letter queues and you quite like the [database backed 
dead letter queues](/guide/durability/dead-letter-storage) you get with Wolverine's message durability, you can use the `WolverineStorage` option:

<!-- snippet: sample_disable_rabbit_mq_dead_letter_queue -->
<a id='snippet-sample_disable_rabbit_mq_dead_letter_queue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Disable dead letter queueing by default
        opts.UseRabbitMq()
            .DisableDeadLetterQueueing()

            // or conventionally
            .ConfigureListeners(l =>
            {
                // Really does the same thing as the first usage
                l.DisableDeadLetterQueueing();
            });
            

        // Disable the dead letter queue for this specific queue
        opts.ListenToRabbitQueue("incoming").DisableDeadLetterQueueing();

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L302-L324' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_rabbit_mq_dead_letter_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->




