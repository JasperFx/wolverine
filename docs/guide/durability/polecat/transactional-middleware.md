# Transactional Middleware

::: warning
When using the transactional middleware with Polecat, Wolverine is assuming that there will be a single,
atomic transaction for the entire message handler. Because of the integration with Wolverine's outbox and
the Polecat `IDocumentSession`, it is **very strongly** recommended that you do not call `IDocumentSession.SaveChangesAsync()`
yourself as that may result in unexpected behavior in terms of outgoing messages.
:::

::: tip
You will need to make the `IServiceCollection.AddPolecat(...).IntegrateWithWolverine()` call to add this middleware to a Wolverine application.
:::

It is no longer necessary to mark a handler method with `[Transactional]` if you choose to use the `AutoApplyTransactions()` option as shown below:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddPolecat("some connection string")
            .IntegrateWithWolverine();

        // Opt into using "auto" transaction middleware
        opts.Policies.AutoApplyTransactions();
    }).StartAsync();
```

With this enabled, Wolverine will automatically use the Polecat
transactional middleware for handlers that have a dependency on `IDocumentSession` (meaning the method takes in `IDocumentSession` or has
some dependency that itself depends on `IDocumentSession`) as long as the `IntegrateWithWolverine()` call was used in application bootstrapping.

### Opting Out with [NonTransactional]

When using `AutoApplyTransactions()`, there may be specific handlers or HTTP endpoints where you want to explicitly opt out of
transactional middleware even though they use `IDocumentSession`. You can do this with the `[NonTransactional]` attribute:

```cs
using Wolverine.Attributes;

public static class MySpecialHandler
{
    // This handler will NOT have transactional middleware applied
    // even when AutoApplyTransactions() is enabled
    [NonTransactional]
    public static void Handle(MyCommand command, IDocumentSession session)
    {
        // You're managing the session yourself here
    }
}
```

The `[NonTransactional]` attribute can be placed on individual handler methods or on the handler class itself to opt out all methods.

In the previous section we saw an example of incorporating Wolverine's outbox with Polecat transactions. Using Wolverine's transactional middleware support for Polecat, the long hand handler can become this equivalent:

```cs
// Note that we're able to avoid doing any kind of asynchronous
// code in this handler
[Transactional]
public static OrderCreated Handle(CreateOrder command, IDocumentSession session)
{
    var order = new Order
    {
        Description = command.Description
    };

    // Register the new document with Polecat
    session.Store(order);

    // Utilizing Wolverine's "cascading messages" functionality
    // to have this message sent through Wolverine
    return new OrderCreated(order.Id);
}
```

Or if you need to take more control over how the outgoing `OrderCreated` message is sent, you can use this slightly different alternative:

```cs
[Transactional]
public static ValueTask Handle(
    CreateOrder command,
    IDocumentSession session,
    IMessageBus bus)
{
    var order = new Order
    {
        Description = command.Description
    };

    // Register the new document with Polecat
    session.Store(order);

    // Utilizing Wolverine's "cascading messages" functionality
    return bus.SendAsync(
        new OrderCreated(order.Id),
        new DeliveryOptions { DeliverWithin = 5.Minutes() });
}
```

In both cases Wolverine's transactional middleware for Polecat is taking care of registering the Polecat session with Wolverine's outbox before you call into the message handler, and
also calling Polecat's `IDocumentSession.SaveChangesAsync()` afterward.

::: tip
This [Transactional] attribute can appear on either the handler class that will apply to all the actions on that class, or on a specific action method.
:::

If so desired, you *can* also use a policy to apply the Polecat transaction semantics with a policy:

```cs
public class CommandsAreTransactional : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        chains
            .Where(chain => chain.MessageType.Name.EndsWith("Command"))
            .Each(chain => chain.Middleware.Add(new CreateDocumentSessionFrame(chain)));
    }
}
```

Then add the policy to your application like this:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // And actually use the policy
        opts.Policies.Add<CommandsAreTransactional>();
    }).StartAsync();
```

## Using IDocumentOperations

When using the transactional middleware with Polecat, it's best to **not** directly call `IDocumentSession.SaveChangesAsync()`
yourself because that negates the transactional middleware's ability to mark the transaction boundary and can cause
unexpected problems with the outbox. As a way of preventing this problem, you can choose to directly use
Polecat's `IDocumentOperations` as an argument to your handler or endpoint methods, which is effectively `IDocumentSession` minus
the ability to commit the ongoing unit of work with a `SaveChangesAsync` API.

```cs
public class CreateDocCommand2Handler
{
    [Transactional]
    public void Handle(
        CreateDocCommand2 message,
        IDocumentOperations operations)
    {
        operations.Store(new FakeDoc { Id = message.Id });
    }
}
```
