# Marten as Inbox

On the flip side of using Wolverine's "outbox" support for outgoing messages, you can also choose to use the same message persistence for incoming messages such that
incoming messages are first persisted to the application's underlying Postgresql database before being processed. While
you *could* use this with external message brokers like Rabbit MQ, it's more likely this will be valuable for Wolverine's [local queues](/guide/messaging/transports/local).

Back to the sample Marten + Wolverine integration from this page:

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

But this time, focus on the Wolverine configuration of the local queue named "important." By marking this local queue as persistent, any messages sent to this queue
in memory are first persisted to the underlying Postgresql database, and deleted when the message is successfully processed. This allows Wolverine to grant a stronger
delivery guarantee to local messages and even allow messages to be processed if the current application node fails before the message is processed.

::: tip
There are some vague plans to add a little more efficient integration between Wolverine and ASP.Net Core Minimal API, but we're not there yet.
:::

Or finally, it's less code to opt into Wolverine's outbox by delegating to the [command bus](/guide/in-memory-bus) functionality as in this sample [Minimal API](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis?view=aspnetcore-6.0) usage:

<!-- snippet: sample_delegate_to_command_bus_from_minimal_api -->
<a id='snippet-sample_delegate_to_command_bus_from_minimal_api'></a>
```cs
// Delegate directly to Wolverine commands -- More efficient recipe coming later...
app.MapPost("/orders/create2", (CreateOrder command, IMessageBus bus)
    => bus.InvokeAsync(command));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Program.cs#L53-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_delegate_to_command_bus_from_minimal_api' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
