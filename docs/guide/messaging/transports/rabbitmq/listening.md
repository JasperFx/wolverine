# Listening


## Listening Options

Wolverine's Rabbit MQ integration comes with quite a few options to fine tune
listening performance as shown below:

<!-- snippet: sample_listening_to_rabbitmq_queue -->
<a id='snippet-sample_listening_to_rabbitmq_queue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // *A* way to configure Rabbit MQ using their Uri schema
        // documented here: https://www.rabbitmq.com/uri-spec.html
        opts.UseRabbitMq(new Uri("amqp://localhost"));

        // Set up a listener for a queue
        opts.ListenToRabbitQueue("incoming1")
            .PreFetchSize(5)
            .PreFetchCount(100)
            .ListenerCount(5) // use 5 parallel listeners
            .CircuitBreaker(cb =>
            {
                cb.PauseTime = 1.Minutes();
                // 10% failures will cause the listener to pause
                cb.FailurePercentageThreshold = 10;
            })
            .UseDurableInbox();

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L53-L85' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listening_to_rabbitmq_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To optimize and tune the message processing, you may want to read more about the [Rabbit MQ prefetch count and prefetch
size concepts](https://www.cloudamqp.com/blog/how-to-optimize-the-rabbitmq-prefetch-count.html).

## Listen to a Queue

Setting up a listener to a specific Rabbit MQ queue is shown below:

<!-- snippet: sample_listening_to_rabbitmq_queue -->
<a id='snippet-sample_listening_to_rabbitmq_queue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // *A* way to configure Rabbit MQ using their Uri schema
        // documented here: https://www.rabbitmq.com/uri-spec.html
        opts.UseRabbitMq(new Uri("amqp://localhost"));

        // Set up a listener for a queue
        opts.ListenToRabbitQueue("incoming1")
            .PreFetchSize(5)
            .PreFetchCount(100)
            .ListenerCount(5) // use 5 parallel listeners
            .CircuitBreaker(cb =>
            {
                cb.PauseTime = 1.Minutes();
                // 10% failures will cause the listener to pause
                cb.FailurePercentageThreshold = 10;
            })
            .UseDurableInbox();

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L53-L85' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listening_to_rabbitmq_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->