# Connecting to Multiple Brokers <Badge type="tip" text="3.0" />

If you have a need to exchange messages with multiple Rabbit MQ brokers from one application, you have the option
to add additional, named brokers identified by Wolverine's `BrokerName` identity. Here's the syntax to work with
extra, named brokers:

<!-- snippet: sample_configure_additional_rabbit_mq_broker -->
<a id='snippet-sample_configure_additional_rabbit_mq_broker'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // Connect to the "main" Rabbit MQ broker for this application
    opts.UseRabbitMq(builder.Configuration.GetConnectionString("internal-rabbit-mq"));

    // Listen for incoming messages on the main broker at the queue named "incoming"
    opts.ListenToRabbitQueue("incoming");

    // Let's say there's one Rabbit MQ broker for internal communications
    // and a second one for external communications
    var external = new BrokerName("external");

    // BUT! Let's also use a second broker
    opts.AddNamedRabbitMqBroker(external, factory =>
    {
        factory.Uri = new Uri(builder.Configuration.GetConnectionString("external-rabbit-mq"));
    });

    // Listen to a queue on the named, secondary broker
    opts.ListenToRabbitQueueOnNamedBroker(external, "incoming");
    
    // Other options for publishing messages to the named broker
    opts.PublishAllMessages().ToRabbitExchangeOnNamedBroker(external, "exchange1");

    opts.PublishAllMessages().ToRabbitQueueOnNamedBroker(external, "outgoing");

    opts.PublishAllMessages().ToRabbitRoutingKeyOnNamedBroker(external, "exchange1", "key2");

    opts.PublishAllMessages().ToRabbitTopicsOnNamedBroker(external, "topics");
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L532-L566' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configure_additional_rabbit_mq_broker' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `Uri` values for endpoints to the additional broker follows the same rules as the normal usage of the Rabbit MQ
transport, but the `Uri.Scheme` is the name of the additional broker. For example, connecting to a queue named
"incoming" at a broker named by `new BrokerName("external")` would be `external://queue/incoming`.
