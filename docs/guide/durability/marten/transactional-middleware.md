# Transactional Middleware

::: warning
When using the transactional middleware with Marten, Wolverine is assuming that there will be a single,
atomic transaction for the entire message handler. Because of the integration with Wolverine's outbox and
the Marten `IDocumentSession`, it is **very strongly** recommended that you do not call `IDocumentSession.SaveChangesAsync()`
yourself as that may result in unexpected behavior in terms of outgoing messages.
:::

::: tip
You will need to make the `IServiceCollection.AddMarten(...).IntegrateWithWolverine()` call to add this middleware to a Wolverine application.
:::

It is no longer necessary to mark a handler method with `[Transactional]` if you choose to use the `AutoApplyTransactions()` option as shown below:

<!-- snippet: sample_using_auto_apply_transactions_with_marten -->
<a id='snippet-sample_using_auto_apply_transactions_with_marten'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddMarten("some connection string")
            .IntegrateWithWolverine();

        // Opt into using "auto" transaction middleware
        opts.Policies.AutoApplyTransactions();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Sample/BootstrapWithAutoTransactions.cs#L12-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_auto_apply_transactions_with_marten' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With this enabled, Wolverine will automatically use the Marten
transactional middleware for handlers that have a dependency on `IDocumentSession` (meaning the method takes in `IDocumentSession` or has
some dependency that itself depends on `IDocumentSession`) as long as the `IntegrateWithWolverine()` call was used in application bootstrapping.

In the previous section we saw an example of incorporating Wolverine's outbox with Marten transactions. We also wrote a fair amount of code to do so that could easily feel
repetitive over time. Using Wolverine's transactional middleware support for Marten, the long hand handler above can become this equivalent:

<!-- snippet: sample_shorthand_order_handler -->
<a id='snippet-sample_shorthand_order_handler'></a>
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

    // Register the new document with Marten
    session.Store(order);

    // Utilizing Wolverine's "cascading messages" functionality
    // to have this message sent through Wolverine
    return new OrderCreated(order.Id);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Order.cs#L51-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_shorthand_order_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or if you need to take more control over how the outgoing `OrderCreated` message is sent, you can use this slightly different alternative:

<!-- snippet: sample_shorthand_order_handler_alternative -->
<a id='snippet-sample_shorthand_order_handler_alternative'></a>
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

    // Register the new document with Marten
    session.Store(order);

    // Utilizing Wolverine's "cascading messages" functionality
    // to have this message sent through Wolverine
    return bus.SendAsync(
        new OrderCreated(order.Id),
        new DeliveryOptions { DeliverWithin = 5.Minutes() });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Order.cs#L76-L99' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_shorthand_order_handler_alternative' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In both cases Wolverine's transactional middleware for Marten is taking care of registering the Marten session with Wolverine's outbox before you call into the message handler, and
also calling Marten's `IDocumentSession.SaveChangesAsync()` afterward. Used judiciously, this might allow you to avoid more messy or noisy asynchronous code in your
application handler code.

::: tip
This [Transactional] attribute can appear on either the handler class that will apply to all the actions on that class, or on a specific action method.
:::

If so desired, you *can* also use a policy to apply the Marten transaction semantics with a policy. As an example, let's say that you want every message handler where the message type
name ends with "Command" to use the Marten transaction middleware. You could accomplish that
with a handler policy like this:

<!-- snippet: sample_CommandsAreTransactional -->
<a id='snippet-sample_commandsaretransactional'></a>
```cs
public class CommandsAreTransactional : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        // Important! Create a brand new TransactionalFrame
        // for each chain
        chains
            .Where(chain => chain.MessageType.Name.EndsWith("Command"))
            .Each(chain => chain.Middleware.Add(new CreateDocumentSessionFrame(chain)));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/transactional_frame_end_to_end.cs#L87-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_commandsaretransactional' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then add the policy to your application like this:

<!-- snippet: sample_Using_CommandsAreTransactional -->
<a id='snippet-sample_using_commandsaretransactional'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // And actually use the policy
        opts.Policies.Add<CommandsAreTransactional>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/transactional_frame_end_to_end.cs#L45-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_commandsaretransactional' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
