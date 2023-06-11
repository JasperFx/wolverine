# Test Automation Support

The Wolverine absolutely believes in Test Driven Development and the importance of strong test automation strategies as a key part of sustainable development. To that end,
Wolverine's conceptual design from the very beginning (Wolverine started as "Jasper" in 2015!) has been to maximize testability by trying
to decouple application code from framework or other infrastructure concerns.

See Jeremy's blog post [How Wolverine allows for easier testing](https://jeremydmiller.com/2022/12/13/how-wolverine-allows-for-easier-testing/) for an introduction to unit testing Wolverine message handlers.

Also see [Wolverine Best Practices](/tutorials/best-practices) for other helpful tips.

## Extension Methods for Outgoing Messages

Your Wolverine message handlers will often have some need to publish, send, or schedule other messages as part of their work. At the unit 
test level you'll frequently want to validate the *decision* about whether or not to send a message. To aid
in those assertions, Wolverine out of the box includes some testing helper extension methods on `IEnumerable<object>`
inspired by the [Shouldly](https://github.com/shouldly/shouldly) project.

For an example, let's look at this message handler for applying a debit to a bank account that
will use [cascading messages](/guide/handlers/cascading) to raise a variable number of additional messages:

<!-- snippet: sample_AccountHandler_for_testing_examples -->
<a id='snippet-sample_accounthandler_for_testing_examples'></a>
```cs
[Transactional] 
public static IEnumerable<object> Handle(
    DebitAccount command, 
    Account account, 
    IDocumentSession session)
{
    account.Balance -= command.Amount;
 
    // This just marks the account as changed, but
    // doesn't actually commit changes to the database
    // yet. That actually matters as I hopefully explain
    session.Store(account);

    // Conditionally trigger other, cascading messages
    if (account.Balance > 0 && account.Balance < account.MinimumThreshold)
    {
        yield return new LowBalanceDetected(account.Id);
    }
    else if (account.Balance < 0)
    {
        yield return new AccountOverdrawn(account.Id);
     
        // Give the customer 10 days to deal with the overdrawn account
        yield return new EnforceAccountOverdrawnDeadline(account.Id);
    }

    yield return new AccountUpdated(account.Id, account.Balance);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L38-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_accounthandler_for_testing_examples' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The testing extensions can be seen in action by the following test:

<!-- snippet: sample_handle_a_debit_that_makes_the_account_have_a_low_balance -->
<a id='snippet-sample_handle_a_debit_that_makes_the_account_have_a_low_balance'></a>
```cs
[Fact]
public void handle_a_debit_that_makes_the_account_have_a_low_balance()
{
    var account = new Account
    {
        Balance = 1000,
        MinimumThreshold = 200,
        Id = 1111
    };

    // Let's otherwise ignore this for now, but this is using NSubstitute
    var session = Substitute.For<IDocumentSession>();

    var message = new DebitAccount(account.Id, 801);
    var messages = AccountHandler.Handle(message, account, session);
    
    // Now, verify that the only the expected messages are published:
    
    // Exactly one message of type LowBalanceDetected
    messages
        .ShouldHaveMessageOfType<LowBalanceDetected>()
        .AccountId.ShouldBe(account.Id);
    
    // Assert that there are no messages of type AccountOverdrawn
    messages.ShouldHaveNoMessageOfType<AccountOverdrawn>();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L74-L103' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handle_a_debit_that_makes_the_account_have_a_low_balance' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The supported extension methods so far are in the [TestingExtensions](https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/TestingExtensions.cs) class.

As we'll see in the next section, you can also find a matching `Envelope` for a message type.

::: tip
I'd personally organize the testing against that handler with a context/specification pattern, but I just wanted to show the extension methods here.
:::

## TestMessageContext

In the section above we used cascading messages, but since there are some use cases -- or maybe even just
user preference -- that would lead you to directly use `IMessageContext` to send additional messages
from a message handler, Wolverine comes with the `TestMessageContext` class that can be used as a 
[test double spy](https://martinfowler.com/bliki/TestDouble.html) within unit tests.

Here's a different version of the message handler from the previous section, but this time using `IMessageContext`
directly:

<!-- snippet: sample_DebitAccountHandler_that_uses_IMessageContext -->
<a id='snippet-sample_debitaccounthandler_that_uses_imessagecontext'></a>
```cs
[Transactional] 
public static async Task Handle(
    DebitAccount command, 
    Account account, 
    IDocumentSession session, 
    IMessageContext messaging)
{
    account.Balance -= command.Amount;
 
    // This just marks the account as changed, but
    // doesn't actually commit changes to the database
    // yet. That actually matters as I hopefully explain
    session.Store(account);

    // Conditionally trigger other, cascading messages
    if (account.Balance > 0 && account.Balance < account.MinimumThreshold)
    {
        await messaging.SendAsync(new LowBalanceDetected(account.Id));
    }
    else if (account.Balance < 0)
    {
        await messaging.SendAsync(new AccountOverdrawn(account.Id), new DeliveryOptions{DeliverWithin = 1.Hours()});
     
        // Give the customer 10 days to deal with the overdrawn account
        await messaging.ScheduleAsync(new EnforceAccountOverdrawnDeadline(account.Id), 10.Days());
    }
    
    // "messaging" is a Wolverine IMessageContext or IMessageBus service 
    // Do the deliver within rule on individual messages
    await messaging.SendAsync(new AccountUpdated(account.Id, account.Balance),
        new DeliveryOptions { DeliverWithin = 5.Seconds() });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L119-L154' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_debitaccounthandler_that_uses_imessagecontext' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To test this handler, we can use `TestMessageContext` as a stand in to just record
the outgoing messages and even let us do some assertions on exactly *how* the messages
were published. I'm using [xUnit.Net](https://xunit.net/) here, but this is certainly usable from other
test harness tools:

<!-- snippet: sample_when_the_account_is_overdrawn -->
<a id='snippet-sample_when_the_account_is_overdrawn'></a>
```cs
public class when_the_account_is_overdrawn : IAsyncLifetime
{
    private readonly Account theAccount = new Account
    {
        Balance = 1000,
        MinimumThreshold = 100,
        Id = Guid.NewGuid()
    };
 
    private readonly TestMessageContext theContext = new TestMessageContext();
     
    // I happen to like NSubstitute for mocking or dynamic stubs
    private readonly IDocumentSession theDocumentSession = Substitute.For<IDocumentSession>();

    public async Task InitializeAsync()
    {
        var command = new DebitAccount(theAccount.Id, 1200);
        await DebitAccountHandler.Handle(command, theAccount, theDocumentSession, theContext);
    }
 
    [Fact]
    public void the_account_balance_should_be_negative()
    {
        theAccount.Balance.ShouldBe(-200);
    }
 
    [Fact]
    public void raises_an_account_overdrawn_message()
    {
        // ShouldHaveMessageOfType() is an extension method in 
        // Wolverine itself to facilitate unit testing assertions like this
        theContext.Sent.ShouldHaveMessageOfType<AccountOverdrawn>()
            .AccountId.ShouldBe(theAccount.Id);
    }
 
    [Fact]
    public void raises_an_overdrawn_deadline_message_in_10_days()
    {
        theContext.ScheduledMessages()
            // Find the wrapping envelope for this message type,
            // then we can chain assertions against the wrapping Envelope
            .ShouldHaveEnvelopeForMessageType<EnforceAccountOverdrawnDeadline>()
            .ScheduleDelay.ShouldBe(10.Days());
    }
 
    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware.Tests/try_out_the_middleware.cs#L99-L152' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_when_the_account_is_overdrawn' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `TestMessageContext` mostly just collects an array of objects that are sent, published, or scheduled. The
same extension methods explained in the previous section can be used to verify the outgoing messages
and even *how* they were published.

## Stubbing All External Transports

::: tip
In all cases here, Wolverine is disabling all external listeners, stubbing all outgoing
subscriber endpoints, and **not** making any connection to external brokers.
:::

Unlike some older .NET messaging tools, Wolverine comes out of the box
with its in-memory "mediator" functionality that allows you to directly
invoke any possible message handler in the system on demand without
any explicit configuration. Great, and that means that there's value
in just spinning up the application as is and executing locally -- but what
about any external transport dependencies that may be very inconvenient
to utilize in automated tests?

To that end, Wolverine allows you to completely disable all external
transports including the built in TCP transport. There's a couple different ways
to go about it. The simplest conceptual approach is to leverage the .NET environment
name like this:

<!-- snippet: sample_conditionally_disable_transports -->
<a id='snippet-sample_conditionally_disable_transports'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine((context, opts) =>
    {
        // Other configuration...

        // IF the environment is "Testing", turn off all external transports
        if (context.HostingEnvironment.EnvironmentName.EqualsIgnoreCase("Testing"))
        {
            opts.StubAllExternalTransports();
        }
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L17-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conditionally_disable_transports' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

I'm not necessarily comfortable with a lot of conditional hosting setup all the time,
so there's another option to use the `IServiceCollection.DisableAllExternalWolverineTransports()`
extension method as shown below:

<!-- snippet: sample_disabling_external_transports -->
<a id='snippet-sample_disabling_external_transports'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // do whatever you need to configure Wolverine
    })
    
    // Override the Wolverine configuration to disable all
    // external transports, broker connectivity, and incoming/outgoing
    // messages to run completely locally
    .ConfigureServices(services => services.DisableAllExternalWolverineTransports())
    
    .StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/disabling_all_external_transports.cs#L13-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_external_transports' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Finally, to put that in a little more context about how you might go about using it
in real life, let's say that we have out main application with a relatively clean
bootstrapping setup and a separate integration testing project. In this case we'd 
like to bootstrap the application from the integration testing project **as it is, except
for having all the external transports disabled**. In the code below, I'm using the [Alba](https://jasperfx.github.io/alba)
and [WebApplicationFactory](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests):

<!-- snippet: sample_disabling_the_transports_from_web_application_factory -->
<a id='snippet-sample_disabling_the_transports_from_web_application_factory'></a>
```cs
// This is using Alba to bootstrap a Wolverine application
// for integration tests, but it's using WebApplicationFactory
// to do the actual bootstrapping
await using var host = await AlbaHost.For<Program>(x =>
{
    // I'm overriding 
    x.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware.Tests/try_out_the_middleware.cs#L33-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_the_transports_from_web_application_factory' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the sample above, I'm bootstrapping the `IHost` for my production application with 
all the external transports turned off in a way that's appropriate for integration testing
message handlers within the main application.

## Integration Testing with Tracked Sessions

So far we've been mostly focused on unit testing Wolverine handler methods individually with 
unit tests without any direct coupling to infrastructure. Great, that's a great start,
but you're eventually going to also need some integration tests, and invoking or publishing messages
is a very logical entry point for integration testing.

First, why integration testing with Wolverine?

1. Wolverine is probably most effective when you're heavily leveraging middleware or Wolverine conventions, and only an integration test is really going to get through the entire "stack"
2. You may frequently want to test the interaction between your application code and infrastructure concerns like databases
3. Handling messages will frequently spawn other messages that will be executed in other threads or other processes, and you'll frequently want to write bigger tests that span across messages

::: tip
I'm not getting into it here, but remember that `IHost` is relatively
expensive to build, so you'll probably want it cached between
tests. Or at least be aware that it's expensive.
:::

This sample was taken from [an introductory blog post](https://jeremydmiller.com/2022/12/12/introducing-wolverine-for-effective-server-side-net-development/) that may give you some additional context for what's happening here.

Going back to our sample message handler for the `DebitAccount` in the previous sections,
let's say that we want an integration test that spans the middleware that looks up the `Account` data,
the Fluent Validation middleware, [Marten](https://martendb.io) usage, and even across to any cascading
messages that are also handled in process as a result of the original message. One of the big challenges
with automated testing against asynchronous processing is *knowing* when the "action" part of the "arrange/act/assert"
phase of the test is complete and it's safe to start making assertions. Anyone who has had the misfortune
to work with complicated Selenium test suites is very aware of this challenge.

Not to fear though, Wolverine comes out of the box with the concept of "tracked sessions" that you can use
to write predictable and reliable integration tests.

::: warning
I'm omitting the code necessary to set up system state first just to concentrate on
the Wolverine mechanics here.
:::

To start with tracked sessions, let's assume that you have an `IHost` for your Wolverine
application in your testing harness. Assuming you do, you can start a tracked session using 
the `IHost.InvokeMessageAndWaitAsync()` extension method in Wolverine like this:

<!-- snippet: sample_using_tracked_session -->
<a id='snippet-sample_using_tracked_session'></a>
```cs
public async Task using_tracked_sessions()
{
    // The point here is just that you somehow have
    // an IHost for your application
    using var host = await Host.CreateDefaultBuilder()
        .UseWolverine().StartAsync();

    var debitAccount = new DebitAccount(111, 300);
    var session = await host.InvokeMessageAndWaitAsync(debitAccount);

    var overdrawn = session.Sent.SingleMessage<AccountOverdrawn>();
    overdrawn.AccountId.ShouldBe(debitAccount.AccountId);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L108-L124' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_tracked_session' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The tracked session mechanism utilizes Wolverine's internal instrumentation to "know" when all the outstanding
work in the system is complete. In this case, if the `AccountOverdrawn` message spawned from `DebitAccount`
is handled locally, the `InvokeMessageAndWaitAsync()` call will not return until the other messages
that are routed locally are finished processing or the test times out. The tracked session will also throw
an `AggregateException` with any exceptions encountered by any message being handled within the activity
that is tracked.

Note that you'll probably *mostly* *invoke* messages in these tests, but there are additional extension
methods on `IHost` for other `IMessageBus` operations.

Finally, there are some more advanced options in tracked sessions you may find useful as 
shown below:

<!-- snippet: sample_advanced_tracked_session_usage -->
<a id='snippet-sample_advanced_tracked_session_usage'></a>
```cs
public async Task using_tracked_sessions_advanced(IHost otherWolverineSystem)
{
    // The point here is just that you somehow have
    // an IHost for your application
    using var host = await Host.CreateDefaultBuilder()
        .UseWolverine().StartAsync();

    var debitAccount = new DebitAccount(111, 300);
    var session = await host
            
        // Start defining a tracked session 
        .TrackActivity()
        
        // Override the timeout period for longer tests
        .Timeout(1.Minutes())
        
        // Be careful with this one! This makes Wolverine wait on some indication
        // that messages sent externally are completed
        .IncludeExternalTransports()
        
        // Make the tracked session span across an IHost for another process
        // May not be super useful to the average user, but it's been crucial
        // to test Wolverine itself
        .AlsoTrack(otherWolverineSystem)

        // This is actually helpful if you are testing for error handling 
        // functionality in your system
        .DoNotAssertOnExceptionsDetected()
        
        // Again, this is testing against processes, with another IHost
        .WaitForMessageToBeReceivedAt<LowBalanceDetected>(otherWolverineSystem)
        
        // There are many other options as well
        .InvokeMessageAndWaitAsync(debitAccount);

    var overdrawn = session.Sent.SingleMessage<AccountOverdrawn>();
    overdrawn.AccountId.ShouldBe(debitAccount.AccountId);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L126-L167' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_advanced_tracked_session_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

