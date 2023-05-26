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
            .ListenToRabbitQueue("pongs")

            // This won't be necessary by the time Wolverine goes 2.0
            // but for now, I've got to help Wolverine out a little bit
            .UseForReplies();

        // Publish messages to the pings queue
        opts.PublishMessage<PingMessage>().ToRabbitExchange("pings");

        // Configure Rabbit MQ connection properties programmatically
        // against a ConnectionFactory
        opts.UseRabbitMq(rabbit =>
            {
                // Using a local installation of Rabbit MQ
                // via a running Docker image
                rabbit.HostName = "localhost";
            })
            // Directs Wolverine to build any declared queues, exchanges, or
            // bindings with the Rabbit MQ broker as part of bootstrapping time
            .AutoProvision();

        // Or you can use this functionality to set up *all* known
        // Wolverine (or Marten) related resources on application startup
        opts.Services.AddResourceSetupOnStartup();

        // This will send ping messages on a continuous
        // loop
        opts.Services.AddHostedService<PingerService>();
    }).RunOaktonCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPongWithRabbitMq/Pinger/Program.cs#L7-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_rabbitmq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See the [Rabbit MQ .NET Client documentation](https://www.rabbitmq.com/dotnet-api-guide.html#connecting) for more information about configuring the `ConnectionFactory` to connect to Rabbit MQ.

## Interoperability with Mass Transit

Wolverine can interoperate bi-directionally with [MassTransit](https://masstransit-project.com/) using [RabbitMQ](http://www.rabbitmq.com/).
At this point, the interoperability is **only** functional if MassTransit is using its standard "envelope" serialization
approach (i.e., **not** using raw JSON serialization).

::: warning
At this point, if an endpoint is set up for interoperability with MassTransit, reserve that endpoint for traffic
with MassTransit, and don't try to use that endpoint for Wolverine to Wolverine traffic
:::

The configuration to do this is shown below:

<!-- snippet: sample_MassTransit_interoperability -->
<a id='snippet-sample_masstransit_interoperability'></a>
```cs
Wolverine = await Host.CreateDefaultBuilder().UseWolverine(opts =>
{
    opts.ApplicationAssembly = GetType().Assembly;

    opts.UseRabbitMq()
        .AutoProvision().AutoPurgeOnStartup()
        .BindExchange("wolverine").ToQueue("wolverine")
        .BindExchange("masstransit").ToQueue("masstransit");

    opts.PublishAllMessages().ToRabbitExchange("masstransit")

        // Tell Wolverine to make this endpoint send messages out in a format
        // for MassTransit
        .UseMassTransitInterop();

    opts.ListenToRabbitQueue("wolverine")

        // Tell Wolverine to make this endpoint interoperable with MassTransit
        .UseMassTransitInterop(mt =>
        {
            // optionally customize the inner JSON serialization
        })
        .DefaultIncomingMessage<ResponseMessage>().UseForReplies();
}).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Interop/MassTransit/MassTransitSpecs.cs#L23-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_masstransit_interoperability' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->




## Conventional Routing

::: tip
All Rabbit MQ objects are declared as durable by default, just meaning that the Rabbit MQ objects
will live independently of the lifecycle of the Rabbit MQ connections from your Wolverine application.
:::

Wolverine comes with an option to set up conventional routing rules for Rabbit MQ so
you can bypass having to set up explicit message routing. Here's the easiest
possible usage:

<!-- snippet: sample_activating_rabbit_mq_conventional_routing -->
<a id='snippet-sample_activating_rabbit_mq_conventional_routing'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq()
            // Opt into conventional Rabbit MQ routing
            .UseConventionalRouting();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L195-L205' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_activating_rabbit_mq_conventional_routing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With the defaults from above, for each message that the application can handle
(as determined by the discovered [message handlers](/guide/handlers/discovery)) the conventional routing will:

1. A durable queue using Wolverine's [message type name logic](/guide/messages.html#message-type-name-or-alias)
2. A listening endpoint to the queue above configured with a single, inline listener and **without and enrollment in the durable outbox**

Likewise, for every outgoing message type, the routing convention will *on demand at runtime*:

1. Declare a fanout exchange named with the Wolverine message type alias name (usually the full name of the message type)
2. Create the exchange if auto provisioning is enabled if the exchange does not already exist
3. Create a [subscription rule](/guide/messaging/subscriptions) for that message type to the new exchange within the system

Of course, you may want your own slightly different behavior, so there's plenty of hooks to customize the
Rabbit MQ routing conventions as shown below:

<!-- snippet: sample_activating_rabbit_mq_conventional_routing_customized -->
<a id='snippet-sample_activating_rabbit_mq_conventional_routing_customized'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq()
            // Opt into conventional Rabbit MQ routing
            .UseConventionalRouting(x =>
            {
                // Customize the naming convention for the outgoing exchanges
                x.ExchangeNameForSending(type => type.Name + "Exchange");

                // Customize the naming convention for incoming queues
                x.QueueNameForListener(type => type.FullName.Replace('.', '-'));

                // Or maybe you want to conditionally configure listening endpoints
                x.ConfigureListeners((listener, context) =>
                    {
                        if (context.MessageType.IsInNamespace("MyApp.Messages.Important"))
                        {
                            listener.UseDurableInbox().ListenerCount(5);
                        }
                        else
                        {
                            // If not important, let's make the queue be
                            // volatile and purge older messages automatically
                            listener.TimeToLive(2.Minutes());
                        }
                    })
                    // Or maybe you want to conditionally configure the outgoing exchange
                    .ConfigureSending((ex, _) => { ex.ExchangeType(ExchangeType.Direct); });
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L210-L244' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_activating_rabbit_mq_conventional_routing_customized' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


TODO -- add content on filtering message types
TODO -- link to MassTransit interop

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L90-L107' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_rabbitmq_queue' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L112-L135' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_rabbitmq_exchange' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L140-L160' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_rabbitmq_routing_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Working with Topics

Wolverine supports publishing to [Rabbit MQ topic exchanges](https://www.rabbitmq.com/tutorials/tutorial-one-dotnet.html)
with this usage:

<!-- snippet: sample_publishing_to_rabbit_mq_topics_exchange -->
<a id='snippet-sample_publishing_to_rabbit_mq_topics_exchange'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq();

        opts.Publish(x =>
        {
            x.MessagesFromNamespace("SomeNamespace");
            x.ToRabbitTopics("topics-exchange", ex =>
            {
                // optionally configure the exchange
            });
        });

        opts.ListenToRabbitQueue("");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L15-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_to_rabbit_mq_topics_exchange' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

While we're specifying the exchange name ("topics-exchange"), we did nothing to specify the topic
name. With this set up, when you publish a message in this application like so:

<!-- snippet: sample_sending_topic_routed_message -->
<a id='snippet-sample_sending_topic_routed_message'></a>
```cs
var publisher = host.Services.GetRequiredService<IMessageBus>();
await publisher.SendAsync(new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L36-L41' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_topic_routed_message' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You will be sending that message to the "topics-exchange" with a topic name derived from
the message type. By default that topic name will be Wolverine's [message type alias](/guide/messages.html#message-type-name-or-alias).
Unless explicitly overridden, that alias is the full type name of the message type.

That topic name derivation can be overridden explicitly by placing the `[Topic]` attribute
on a message type like so:

<!-- snippet: sample_using_topic_attribute -->
<a id='snippet-sample_using_topic_attribute'></a>
```cs
[Topic("one")]
public class TopicMessage1
{
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/TopicRoutingTester.cs#L8-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_attribute' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_using_topic_attribute-1'></a>
```cs
[Topic("color.blue")]
public class FirstMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L150-L158' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_attribute-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Of course, you can always explicitly send a message to a specific topic with this syntax:

<!-- snippet: sample_sending_to_a_specific_topic -->
<a id='snippet-sample_sending_to_a_specific_topic'></a>
```cs
await publisher.BroadcastToTopicAsync("color.*", new Message1());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L43-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_to_a_specific_topic' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note two things about the code above:

1. The `IMessageBus.BroadcastToTopicAsync()` method will fail if there is not the declared topic
   exchange endpoint that we configured above
2. You can use Rabbit MQ topic matching patterns in addition to using the exact topic

Lastly, to set up listening to specific topic names or topic patterns, you just need to
declare bindings between a topic name or pattern, the topics exchange, and the queues you're listening
to in your application. Lot of words, here's some code from the Wolverine test suite:

<!-- snippet: sample_binding_topics_and_topic_patterns_to_queues -->
<a id='snippet-sample_binding_topics_and_topic_patterns_to_queues'></a>
```cs
theSender = Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq("host=localhost;port=5672").AutoProvision();
        opts.PublishAllMessages().ToRabbitTopics("wolverine.topics", exchange =>
        {
            exchange.BindTopic("color.green").ToQueue("green");
            exchange.BindTopic("color.blue").ToQueue("blue");
            exchange.BindTopic("color.*").ToQueue("all");
        });
    }).Start();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/send_by_topics.cs#L24-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_binding_topics_and_topic_patterns_to_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Managing Rabbit MQ Objects

::: tip
Wolverine assumes that exchanges should be "fanout" unless explicitly configured
otherwise
:::

Reusing a code sample from up above, the `AutoProvision()` declaration will
direct Wolverine to create any missing Rabbit MQ [exchanges, queues, or bindings](https://www.rabbitmq.com/tutorials/amqp-concepts.html)
declared in the application configuration at application bootstrapping time.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L140-L160' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_rabbitmq_routing_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At development time -- or occasionally in production systems -- you may want to have the messaging
queues purged of any old messages at application startup time. Wolverine supports that with
Rabbit MQ using the `AutoPurgeOnStartup()` declaration:

<!-- snippet: sample_autopurge_rabbitmq -->
<a id='snippet-sample_autopurge_rabbitmq'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq()
            .AutoPurgeOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L165-L174' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_autopurge_rabbitmq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or you can be more selective and only have certain queues of volatile messages purged
at startup as shown below:

<!-- snippet: sample_autopurge_selective_queues -->
<a id='snippet-sample_autopurge_selective_queues'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq()
            .DeclareQueue("queue1")
            .DeclareQueue("queue2", q => q.PurgeOnStartup = true);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.RabbitMQ.Tests/Samples.cs#L180-L190' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_autopurge_selective_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine's Rabbit MQ integration also supports the [Oakton stateful resource](https://jasperfx.github.io/oakton/guide/host/resources.html) model,
so you can make a generic declaration to auto-provision the Rabbit MQ objects at startup time
(as well as any other stateful Wolverine resources like envelope storage) with the Oakton
declarations as shown in the setup below that uses the `AddResourceSetupOnStartup()` declaration:

<!-- snippet: sample_kitchen_sink_bootstrapping -->
<a id='snippet-sample_kitchen_sink_bootstrapping'></a>
```cs
var builder = WebApplication.CreateBuilder(args);

builder.Host.ApplyOaktonExtensions();

builder.Host.UseWolverine(opts =>
{
    // I'm setting this up to publish to the same process
    // just to see things work
    opts.PublishAllMessages()
        .ToRabbitExchange("issue_events", exchange => exchange.BindQueue("issue_events"))
        .UseDurableOutbox();

    opts.ListenToRabbitQueue("issue_events").UseDurableInbox();

    opts.UseRabbitMq(factory =>
    {
        // Just connecting with defaults, but showing
        // how you *could* customize the connection to Rabbit MQ
        factory.HostName = "localhost";
        factory.Port = 5672;
    });
});

// This is actually important, this directs
// the app to build out all declared Postgresql and
// Rabbit MQ objects on start up if they do not already
// exist
builder.Services.AddResourceSetupOnStartup();

// Just pumping out a bunch of messages so we can see
// statistics
builder.Services.AddHostedService<Worker>();

builder.Services.AddMarten(opts =>
{
    // I think you would most likely pull the connection string from
    // configuration like this:
    // var martenConnectionString = builder.Configuration.GetConnectionString("marten");
    // opts.Connection(martenConnectionString);

    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "issues";

    // Just letting Marten know there's a document type
    // so we can see the tables and functions created on startup
    opts.RegisterDocumentType<Issue>();

    // I'm putting the inbox/outbox tables into a separate "issue_service" schema
}).IntegrateWithWolverine("issue_service");

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

// Actually important to return the exit code here!
return await app.RunOaktonCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/KitchenSink/MartenAndRabbitIssueService/Program.cs#L11-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_kitchen_sink_bootstrapping' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that this stateful resource model is also available at the command line as well for deploy time
management.



## Connecting to non-Wolverine Applications

TODO -- MORE HERE.
