# Custom Middleware

While reviewing a very large system that used asynchronous messaging I noticed a common pattern in many of the message handlers:

1. Attempt to load account data referenced by the incoming command
2. If the account didn't exist, log that the account referenced by the command didn't exist and stop the processing

Like this code:

<!-- snippet: sample_common_scenario -->
<a id='snippet-sample_common_scenario'></a>
```cs
public static async Task Handle(DebitAccount command, IDocumentSession session, ILogger logger)
{
    // Try to find a matching account for the incoming command
    var account = await session.LoadAsync<Account>(command.AccountId);
    if (account == null)
    {
        logger.LogInformation("Referenced account {AccountId} does not exist", command.AccountId);
        return;
    }
    
    // do the real processing
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L18-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_common_scenario' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That added up to a lot of repetitive code, and it'd be nice if we introduced some kind of middleware to eliminate the duplication -- so let's do just that!

Using Wolverine's [conventional middleware approach](/guide/handlers/middleware.html#conventional-middleware) strategy, we'll start by lifting a common interface for
command message types that reference an `Account` like so:

<!-- snippet: sample_IAccountCommand -->
<a id='snippet-sample_iaccountcommand'></a>
```cs
public interface IAccountCommand
{
    Guid AccountId { get; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L36-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iaccountcommand' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

So a command message might look like this:

<!-- snippet: sample_CreditAccount -->
<a id='snippet-sample_creditaccount'></a>
```cs
public record CreditAccount(Guid AccountId, decimal Amount) : IAccountCommand;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L45-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_creditaccount' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Skipping ahead a little bit, if we had a handler for the `CreditAccount` command type above that was counting on some kind of middleware to just "push" the matching
`Account` data in, the handler might just be this:

<!-- snippet: sample_CreditAccountHandler -->
<a id='snippet-sample_creditaccounthandler'></a>
```cs
public static class CreditAccountHandler
{
    public static void Handle(
        CreditAccount command, 
        
        // Wouldn't it be nice to just have Wolverine "push"
        // the right account into this method?
        Account account,

        // Using Marten for persistence here
        IDocumentSession session)
    {
        account.Balance += command.Amount;
        
        // Just mark this account as needing to be updated 
        // in the database
        session.Store(account);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L51-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_creditaccounthandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You'll notice at this point that the message handler is synchronous because it's no longer doing any calls to the database. Besides removing some repetitive code, this appproach
arguably makes the Wolverine message handler methods easier to unit test now that you can happily "push" in system state rather than fool around with stubs or mocks.

Next, let's build the actual middleware that will attempt to load an `Account` matching a command's `AccountId`, then determine if the message handling should continue
or be aborted. Here's sample code to do exactly that:

<!-- snippet: sample_AccountLookupMiddleware -->
<a id='snippet-sample_accountlookupmiddleware'></a>
```cs
// This is *a* way to build middleware in Wolverine by basically just
// writing functions/methods. There's a naming convention that
// looks for Before/BeforeAsync or After/AfterAsync
public static class AccountLookupMiddleware
{
    // The message *has* to be first in the parameter list
    // Before or BeforeAsync tells Wolverine this method should be called before the actual action
    public static async Task<(HandlerContinuation, Account?)> LoadAsync(
        IAccountCommand command, 
        ILogger logger, 
        
        // This app is using Marten for persistence
        IDocumentSession session, 
        
        CancellationToken cancellation)
    {
        var account = await session.LoadAsync<Account>(command.AccountId, cancellation);
        if (account == null)
        {
            logger.LogInformation("Unable to find an account for {AccountId}, aborting the requested operation", command.AccountId);
        }
        
        return (account == null ? HandlerContinuation.Stop : HandlerContinuation.Continue, account);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L76-L104' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_accountlookupmiddleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Now, some notes about the code above:

* Wolverine has a convention that generates a call to the middleware's `LoadAsync()` method before the actual message handler method (`CreditAccountHandler.Handle()`)
* The `ILogger` would be the `ILogger<T>` for the message type that is currently being handled. So in the case of the `CreditAccount`, the logger would be `ILogger<CreditAccount>`
* Wolverine can wire up the `Account` object returned from the middleware method to the actual `Handle()` method's `Account` argument
* By returning `HandleContinuation` from the `LoadAsync()` method, we can conditionally tell Wolverine to abort the message processing

Lastly, let's apply the newly built middleware to only the message handlers that work against some kind of `IAccountCommand` message:

<!-- snippet: sample_registering_middleware_by_message_type -->
<a id='snippet-sample_registering_middleware_by_message_type'></a>
```cs
builder.Host.UseWolverine(opts =>
{
    // This middleware should be applied to all handlers where the 
    // command type implements the IAccountCommand interface that is the
    // "detected" message type of the middleware
    opts.Policies.ForMessagesOfType<IAccountCommand>().AddMiddleware(typeof(AccountLookupMiddleware));
    
    opts.UseFluentValidation();

    // Explicit routing for the AccountUpdated
    // message handling. This has precedence over conventional routing
    opts.PublishMessage<AccountUpdated>()
        .ToLocalQueue("signalr")

        // Throw the message away if it's not successfully
        // delivered within 10 seconds
        .DeliverWithin(10.Seconds())
        
        // Not durable
        .BufferedInMemory();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Program.cs#L30-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_middleware_by_message_type' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
