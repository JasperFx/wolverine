# Queue, Topic, and Binding Management

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L241-L261' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_rabbitmq_routing_key' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L266-L275' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_autopurge_rabbitmq' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L280-L290' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_autopurge_selective_queues' title='Start of snippet'>anchor</a></sup>
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
}).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "issue_service");

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

// Actually important to return the exit code here!
return await app.RunOaktonCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/KitchenSink/MartenAndRabbitIssueService/Program.cs#L11-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_kitchen_sink_bootstrapping' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that this stateful resource model is also available at the command line as well for deploy time
management.

## Runtime Declaration

From a user request, there are some extension methods in the WolverineFx.RabbitMQ Nuget off of `IWolverineRuntime` that will enable you to 
first declare new exchanges, queues, and bindings at runtime, and also enable you to "unbind" a queue from an exchange. That
syntax is shown below:

<!-- snippet: sample_dynamic_creation_of_rabbit_mq_objects -->
<a id='snippet-sample_dynamic_creation_of_rabbit_mq_objects'></a>
```cs
// _host is an IHost
var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

// Declare new Exchanges, Queues, and Bindings at runtime
runtime.ModifyRabbitMqObjects(o =>
{
    var queue = o.DeclareQueue(queueName);
    var exchange = o.DeclareExchange(exchangeName);
    queue.BindExchange(exchange.ExchangeName, bindingKey);
});

// Unbind a queue from an exchange
runtime.UnBindRabbitMqQueue(queueName, exchangeName, bindingKey);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/dynamic_object_creation_smoke_tests.cs#L33-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_dynamic_creation_of_rabbit_mq_objects' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Quorum Queues or Streams <Badge type="tip" text="3.10" />

Wolverine can utilize [Rabbit MQ Quorum Queues](https://www.rabbitmq.com/docs/quorum-queues) or [Rabbit MQ Streams](https://www.rabbitmq.com/docs/streams), but ["Classic" queues](https://www.rabbitmq.com/docs/classic-queues) are the default. The only 
real difference as far as Wolverine is concerned is how the queues are declared to Rabbit MQ itself. Wolverine's internals
are largely not impacted otherwise.

Here are your options for configuring one or many queues as opting into being a "Quorum Queue" or a "Stream":

snippet: sample_configuring_quorum_or_streams_in_rabbit_MQ

There are just a few things to know:

* Wolverine's internal reply or control queues will still be declared as "classic" so they can be non-durable
* Streams cannot be purged, and Wolverine ignores the `AutoPurgeOnStartup()` setting for streams

## Inside of Wolverine Extensions

If you need to declare Rabbit MQ queues, exchanges, or bindings within a [Wolverine extension](/guide/extensions),
you can quickly access and make additions to the Rabbit MQ integration with your Wolverine application
like so:

<!-- snippet: sample_RabbitMQ_configuration_in_wolverine_extension -->
<a id='snippet-sample_rabbitmq_configuration_in_wolverine_extension'></a>
```cs
public class MyModuleExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.ConfigureRabbitMq()
            // Make any Rabbit Mq configuration or declare
            // additional Rabbit Mq options through the normal
            // syntax
            .DeclareExchange("my-module")
            .DeclareQueue("my-queue");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L545-L560' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rabbitmq_configuration_in_wolverine_extension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

