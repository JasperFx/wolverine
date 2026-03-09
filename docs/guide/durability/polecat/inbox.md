# Polecat as Inbox

On the flip side of using Wolverine's "outbox" support for outgoing messages, you can also choose to use the same message persistence for incoming messages such that
incoming messages are first persisted to the application's underlying SQL Server database before being processed. While
you *could* use this with external message brokers like Rabbit MQ, it's more likely this will be valuable for Wolverine's [local queues](/guide/messaging/transports/local).

Back to the sample Polecat + Wolverine integration:

```cs
var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyJasperFxExtensions();

builder.Services.AddPolecat(opts =>
    {
        opts.Connection(connectionString);
    })
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    // I've added persistent inbox
    // behavior to the "important"
    // local queue
    opts.LocalQueue("important")
        .UseDurableInbox();
});
```

By marking this local queue as persistent, any messages sent to this queue
in memory are first persisted to the underlying SQL Server database, and deleted when the message is successfully processed. This allows Wolverine to grant a stronger
delivery guarantee to local messages and even allow messages to be processed if the current application node fails before the message is processed.

Or finally, it's less code to opt into Wolverine's outbox by delegating to the [command bus](/guide/in-memory-bus) functionality:

```cs
// Delegate directly to Wolverine commands
app.MapPost("/orders/create2", (CreateOrder command, IMessageBus bus)
    => bus.InvokeAsync(command));
```
