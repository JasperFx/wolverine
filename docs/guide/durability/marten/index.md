# Marten Integration

::: info
There is also some HTTP specific integration for Marten with Wolverine. See [Integration with Marten](/guide/http/marten) for more information.
:::

[Marten](https://martendb.io) and Wolverine are sibling projects under the [JasperFx organization](https://github.com/JasperFx), and as such, have quite a bit of synergy when
used together. At this point, adding the `WolverineFx.Marten` Nuget dependency to your application adds the capability to combine Marten and Wolverine to:

* Simplify persistent handler coding with transactional middleware
* Use Marten and Postgresql as a persistent inbox or outbox with Wolverine messaging
* Support persistent sagas within Wolverine applications
* Effectively use Wolverine and Marten together for a [Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider) function workflow with event sourcing
* Selectively publish events captured by Marten through Wolverine messaging
* Process events captured by Marten through Wolverine message handlers through either [subscriptions](./subscriptions) or the older [event forwarding](./event-forwarding).

## Getting Started

To use the Wolverine integration with Marten, just install the Wolverine.Persistence.Marten Nuget into your application. Assuming that you've [configured Marten](https://martendb.io/configuration/)
in your application (and Wolverine itself!), you next need to add the Wolverine integration to Marten as shown in this sample application bootstrapping:

<!-- snippet: sample_integrating_wolverine_with_marten -->
<a id='snippet-sample_integrating_wolverine_with_marten'></a>
```cs
var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyJasperFxExtensions();

builder.Services.AddMarten(opts =>
    {
        opts.Connection(Servers.PostgresConnectionString);
        opts.DatabaseSchemaName = "chaos2";
    })
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.OnAnyException().RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

    opts.Services.AddScoped<IMessageRecordRepository, MartenMessageRecordRepository>();

    opts.Policies.DisableConventionalLocalRouting();
    opts.UseRabbitMq().AutoProvision();

    opts.Policies.UseDurableInboxOnAllListeners();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

    opts.ListenToRabbitQueue("chaos2");
    opts.PublishAllMessages().ToRabbitQueue("chaos2");

    opts.Policies.AutoApplyTransactions();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/ChaosSender/Program.cs#L13-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_integrating_wolverine_with_marten' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_integrating_wolverine_with_marten-1'></a>
```cs
var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyJasperFxExtensions();

builder.Services.AddMarten(opts =>
    {
        var connectionString = builder
            .Configuration
            .GetConnectionString("postgres");

        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "orders";
    })
    // Optionally add Marten/Postgresql integration
    // with Wolverine's outbox
    .IntegrateWithWolverine();

// You can also place the Wolverine database objects
// into a different database schema, in this case
// named "wolverine_messages"
//.IntegrateWithWolverine("wolverine_messages");

builder.Host.UseWolverine(opts =>
{
    // I've added persistent inbox
    // behavior to the "important"
    // local queue
    opts.LocalQueue("important")
        .UseDurableInbox();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Program.cs#L8-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_integrating_wolverine_with_marten-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For more information, see [durable messaging](/guide/durability/) and the [sample Marten + Wolverine project](https://github.com/JasperFx/wolverine/tree/main/src/Samples/WebApiWithMarten).

Using the `IntegrateWithWolverine()` extension method behind your call to `AddMarten()` will:

* Register the necessary [inbox and outbox](/guide/durability/) database tables with [Marten's database schema management](https://martendb.io/schema/migrations.html)
* Adds Wolverine's "DurabilityAgent" to your .NET application for the inbox and outbox
* Makes Marten the active [saga storage](/guide/durability/sagas) for Wolverine
* Adds transactional middleware using Marten to your Wolverine application

## Entity Attribute Loading

The Marten integration is able to completely support the [Entity attribute usage](/guide/handlers/persistence.html#automatically-loading-entities-to-method-parameters).

## Marten as Outbox

See the [Marten as Outbox](./outbox) page.

## Transactional Middleware

See the [Transactional Middleware](./transactional-middleware) page.


## Marten as Inbox

See the [Marten as Inbox](./inbox) page. 

## Saga Storage

See the [Marten as Saga Storage](./sagas) page.




