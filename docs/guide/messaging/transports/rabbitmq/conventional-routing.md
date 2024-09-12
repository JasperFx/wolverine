# Conventional Routing

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L295-L305' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_activating_rabbit_mq_conventional_routing' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L310-L344' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_activating_rabbit_mq_conventional_routing_customized' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Adjusting Routing Conventions

If the exchange/queue routing defaults don't suit your message routing requirements, they can be overridden as below. 
This keeps existing naming conventions intact and avoids the need to drop down to manual exchange/queue definitions.

<!-- snippet: sample_conventional_routing_exchange_conventions -->
<a id='snippet-sample_conventional_routing_exchange_conventions'></a>
```cs
var sender = WolverineHost.For(opts =>
{
    opts.UseRabbitMq()
        .UseConventionalRouting(conventions =>
        {
            conventions.ExchangeNameForSending(type => type.Name + "_custom");
            conventions.ConfigureSending((x, c) =>
            {
                // Route messages via headers exchange whilst taking advantage of conventional naming
                if (c.MessageType == typeof(HeadersMessage))
                {
                    x.ExchangeType(ExchangeType.Headers);
                }
            });
        });
});

var receiver = WolverineHost.For(opts =>
{
    opts.UseRabbitMq()
        .UseConventionalRouting(conventions =>
        {
            conventions.ExchangeNameForSending(type => type.Name + "_custom");
            conventions.ConfigureListeners((x, c) =>
            {
                if (c.MessageType == typeof(HeadersMessage))
                {
                    // Bind our queue based on the headers tenant-id
                    x.BindToExchange<HeadersMessage>(ExchangeType.Headers,
                        arguments: new Dictionary<string, object>()
                        {
                            { "tenant-id", "tenant-id" }
                        });
                }
            });
        });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L503-L541' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_routing_exchange_conventions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



TODO -- add content on filtering message types
