# Test Automation Support

The Wolverine team absolutely believes in Test Driven Development and the importance of strong test automation strategies as a key part of sustainable development. To that end,
Wolverine's conceptual design from the very beginning (Wolverine started as "Jasper" in 2015!) has been to maximize testability by trying
to decouple application code from framework or other infrastructure concerns.

See Jeremy's blog post [How Wolverine allows for easier testing](https://jeremydmiller.com/2022/12/13/how-wolverine-allows-for-easier-testing/) for an introduction to unit testing Wolverine message handlers.

Also see [Wolverine Best Practices](/introduction/best-practices) for other helpful tips.

::: warning Standing up multiple Wolverine hosts in one test process
Wolverine resolves the *application assembly* it scans for handlers once per process and caches it in a static, so
whichever host runs first can pin handler discovery for every later host that doesn't set one explicitly. If handlers
seem to vanish only in full-suite runs (order-dependent `No routes can be determined` errors), see
[the application-assembly warning under Assembly Discovery](/guide/handlers/discovery.html#assembly-discovery) — set
`opts.ApplicationAssembly` or `opts.Discovery.IncludeAssembly(...)` explicitly on the affected hosts.
:::

And this:

@[youtube](ODSAGAllsxw)

## Integration Testing with Tracked Sessions

::: tip
This is the recommended approach for integration testing against Wolverine message handlers
if there are any outgoing messages or asynchronous behavior as a result of the messages being
handled in your test scenario.
:::

::: info
As of Wolverine 3.13, the same extension methods shown here are available off of `IServiceProvider`
in addition to the original support off of `IHost` if you happen to be writing integration tests
by spinning up just an IoC container and not the full `IHost` in your test harnesses.
:::

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L119-L134' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_tracked_session' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The tracked session mechanism utilizes Wolverine's internal instrumentation to "know" when all the outstanding
work in the system is complete. In this case, if the `AccountOverdrawn` message spawned from `DebitAccount`
is handled locally, the `InvokeMessageAndWaitAsync()` call will not return until the other messages
that are routed locally are finished processing or the test times out. The tracked session will also throw
an `AggregateException` with any exceptions encountered by any message being handled within the activity
that is tracked.

Note that you'll probably *mostly* *invoke* messages in these tests, but there are additional extension
methods on `IHost` for other `IMessageBus` operations.

:::info
The Tracked Session includes only messages sent, published, or scheduled during the tracked session.
Messages sent before the tracked session are not included in the tracked session.
:::

Finally, there are some more advanced options in tracked sessions you may find useful as
shown below:

<!-- snippet: sample_advanced_tracked_session_usage -->
<a id='snippet-sample_advanced_tracked_session_usage'></a>
```cs
private static async Task using_tracked_sessions_advanced(IHost otherWolverineSystem)
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
        
        // Hey, just in case failure acks are getting into your testing session
        // and you do not care for the tests, tell Wolverine to ignore them
        .IgnoreFailureAcks()

        // Again, this is testing against processes, with another IHost
        .WaitForMessageToBeReceivedAt<LowBalanceDetected>(otherWolverineSystem)

        // Continue tracking until at least this many messages of the
        // type have finished execution. Use this when messages are
        // published out-of-band from the tracked execution -- e.g. by a
        // Marten async daemon subscription or projection side effect --
        // where the tracked session could otherwise complete during a
        // momentary lull before every expected message has been published.
        // Multiple calls combine, requiring every count to be reached.
        .WaitForExecutionOf<AccountUpdated>(1)

        // Wolverine does this automatically, but it's sometimes
        // helpful to tell Wolverine to not track certain message
        // types during testing. Especially messages originating from
        // some kind of polling operation
        .IgnoreMessageType<IAgentCommand>()
        
        // Another option
        .IgnoreMessagesMatchingType(type => type.CanBeCastTo<IAgentCommand>())

        // And when the message TYPE cannot tell the traffic apart, filter
        // on the envelope itself
        .IgnoreEnvelopes(e => e.Destination == new Uri("rabbitmq://queue/audit-log"))

        // There are many other options as well
        .InvokeMessageAndWaitAsync(debitAccount);

    var overdrawn = session.Sent.SingleMessage<AccountOverdrawn>();
    overdrawn.AccountId.ShouldBe(debitAccount.AccountId);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L136-L202' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_advanced_tracked_session_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
As of 6.17.3, tracked sessions ignore Wolverine's own framework traffic by default — anything marked
`INotToBeRouted`, which covers agent commands and the telemetry a monitored host publishes to
CritterWatch. Before 6.17.3 that telemetry could be picked up by a tracked session and keep it
waiting on messages your test never sent. If you are on an older version and see that, filter it
yourself:

```csharp
.IgnoreMessagesMatchingType(type => type.CanBeCastTo<INotToBeRouted>())
```

`Acknowledgement` and `FailureAcknowledgement` are deliberately still tracked — the session's own
acknowledgement APIs depend on them.
:::

### Scoping a session by envelope

`IgnoreMessageType<T>()` and `IgnoreMessagesMatchingType()` can only separate traffic by *message type*,
which does not help on a shared host where the same message type is legitimately in flight for more than
one reason at once — background subscriptions, hosted services, or another test's tail. A session that
keeps recording that traffic waits on it, and times out.

`IgnoreEnvelopes()` is the envelope-level twin. The predicate sees the whole `Envelope`, so you can scope
on destination, tenant id, correlation id, a header your test stamped, or anything else that actually
distinguishes the flow under test:

<!-- snippet: sample_ignore_envelopes_on_a_shared_host -->
<a id='snippet-sample_ignore_envelopes_on_a_shared_host'></a>
```cs
private static async Task scoping_a_tracked_session_by_envelope(IHost host, Guid tenantId)
{
    var debitAccount = new DebitAccount(111, 300);

    var session = await host
        .TrackActivity()

        // This host also runs background subscriptions and other tests' work, so the same
        // message types are legitimately in flight for more than one reason at once and
        // IgnoreMessageType cannot separate them. The predicate sees the whole Envelope, so
        // scope on whatever actually distinguishes this flow -- here, the tenant under test.
        .IgnoreEnvelopes(e => e.TenantId != tenantId.ToString())

        .InvokeMessageAndWaitAsync(debitAccount);

    session.Sent.SingleMessage<AccountOverdrawn>()
        .AccountId.ShouldBe(debitAccount.AccountId);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L204-L223' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ignore_envelopes_on_a_shared_host' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Two things are worth knowing before reaching for it:

* It narrows what gets **recorded**, not what completion requires. A session can still time out waiting on
  a message it deliberately never consumes.
* Acknowledgements are never ignored, whatever the filter says, because the session's own acknowledgement
  APIs depend on them being recorded.

If you want to scope by correlation id, write that predicate yourself —
`IgnoreEnvelopes(e => e.CorrelationId != mine)`. Wolverine deliberately does not offer a built-in version,
because that would be a promise that a correlation id survives every transport, HTTP endpoint and interop
mapping, and any gap would surface as a session that silently stops waiting rather than as an error.

### Ignoring traffic in every session on a host <Badge type="tip" text="6.50" />

Some hosts always have an envelope in flight. An embedded CritterWatch console (`opts.AddCritterWatchEmbedded()`)
polls, raises alerts and pushes batches to its browser clients for the life of the host, and any library with
background messaging of its own does the same. A tracked session waits for every envelope it saw to finish, so on a
host like that it never finishes, and every test that uses one times out, including every WolverineFx.Bobcat act.

Rather than filtering each session, write the rules once on `WolverineOptions.Tracking`, and every tracked session
against that host applies them:

```cs
builder.Host.UseWolverine(opts =>
{
    // Everything whose message type is declared in these assemblies, a batch of them included
    opts.Tracking.IgnoreMessagesFromAssembly(typeof(SomeEmbeddedToolMessage).Assembly);

    // Any envelope sent to, or received by, this endpoint
    opts.Tracking.IgnoreEndpoint(new Uri("local://embedded-tool"));

    // And the same type and envelope rules a single session has
    opts.Tracking.IgnoreMessageType<HeartbeatTick>();
    opts.Tracking.IgnoreMessagesMatchingType(t => t.Namespace == "SomeTool.Messages");
    opts.Tracking.IgnoreEnvelopes(e => e.Headers.ContainsKey("x-background"));
});
```

In a test project, the same rules go into the test host's configuration, for example
`services.ConfigureWolverine(opts => opts.Tracking.IgnoreMessagesFromAssembly(...))` in an Alba or
WolverineFx.Bobcat fixture. `WolverineScenario` opens its tracked sessions from the host, so the acts honor the rules
with nothing to configure on the specification.

An ignored envelope is never recorded:

* it does not hold the session open, so the session completes on the application's own messages;
* it does not appear in the session's history (`AllRecordsInOrder()`, `Sent`, `Executed` and the rest). When a
  session times out, the activity grid says how many envelope events the rules dropped;
* an exception it throws is not the session's: `AllExceptions()` leaves it out and `AssertNoExceptionsWereThrown()`
  does not fail on it;
* events it appends, and consistency boundaries it loads, are not reported to `AppendedEvents` or `LoadedBoundaries`.

A batch is an array of the batched message type, so every rule that tests a message type, the default ignoring of
`ISystemCommand` included, also matches a batch of that type. Acknowledgements are never ignored.

A single session can add rules of its own with `IgnoreMessagesFromAssembly()` and `IgnoreEndpoint()` on
`TrackActivity()`, and a test that asserts on the traffic the host ignores, such as an extension's own tests,
opts back in with `IncludeHostIgnoredTraffic()`:

```cs
var session = await host.TrackActivity()
    .IncludeHostIgnoredTraffic()
    .InvokeMessageAndWaitAsync(new SomeEmbeddedToolMessage());
```

An extension that knows its own traffic is background noise can add the rule itself, from an `IWolverineExtension`
or its `WolverineOptions` registration method, so the applications that use it don't have to:

```cs
public class MyToolExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.Tracking.IgnoreMessagesFromAssembly(typeof(MyToolExtension).Assembly);
    }
}
```

### Timeouts govern the whole session

`Timeout()` bounds the *entire* tracked session, including any stage it runs — so a stage that does
slow work, like `PauseThenCatchUpOnMartenDaemonActivity()` waiting for projections to catch up, is
capped by the session's timeout and **not** by any budget internal to that stage. The default is only
5 seconds. Projection catch-up against a cold daemon, a large event backlog, or a busy CI machine
routinely needs more:

```csharp
await host.TrackActivity().Timeout(30.Seconds())
    .PauseThenCatchUpOnMartenDaemonActivity()
    .InvokeMessageAndWaitAsync(command);
```

### First-use handler compilation counts against the timeout

With `TypeLoadMode.Dynamic`, the usual setting in development and tests, Wolverine generates and compiles
a message handler the first time a message of that type is handled. Handlers compile one at a time, so a
first message also waits for any other handler that is compiling at that moment. On a small CI runner a
compile can take a second or more, which means a tracked session whose message and cascades are the
first of their types can spend most of its 5 seconds compiling, and time out with nothing actually stuck.

Rather than raising the timeout, compile every handler once while the test host starts:

```csharp
var handlers = host.GetRuntime().Handlers;
foreach (var chain in handlers.Chains)
{
    try
    {
        handlers.HandlerFor(chain.MessageType);
    }
    catch (NoHandlerForEndpointException)
    {
        // A chain with only sticky handlers compiles per endpoint, on first use
    }
}
```

HTTP endpoints compile on their first request in the same way; `WarmUpRoutes = RouteWarmup.Eager` (see
[the HTTP docs](/guide/http/#eager-warmup)) builds them at startup instead. Pre-generated code
(`TypeLoadMode.Static`) does no compilation at runtime at all.

### Forcing projection catch-up outside a tracked session

Under `UseWolverineManagedEventSubscriptionDistribution` the Marten store runs in
`DaemonMode.ExternallyManaged`, so Marten's own `IHost.ForceAllMartenDaemonActivityToCatchUpAsync()` is
deliberately a **passive** read-only wait — it will not drive a paused daemon forward, and it leaves active
catch-up to the external coordinator, which is Wolverine. Use Wolverine's own helper instead:

```csharp
// Drive every projection and subscription up to the current high water mark,
// then leave the daemons running
await host.PauseThenCatchUpOnMartenDaemonActivityAsync();

// ...or leave them paused, so nothing races your assertions
await host.PauseThenCatchUpOnMartenDaemonActivityAsync(CatchUpMode.AndDoNothing);

// Ancillary stores take the store type
await host.PauseThenCatchUpOnMartenDaemonActivityAsync<ILetterStore>();
```

This is the standalone equivalent of the `PauseThenCatchUpOnMartenDaemonActivity()` tracked-session stage
shown above, for suites that are not driving the work through `TrackActivity()`. It covers every
locally reachable daemon, including per-(database, tenant) shards.

::: warning
Do **not** hand-roll this by pausing the coordinator and then calling `IProjectionDaemon.CatchUpAsync()` on
each daemon. That introduces a *second* writer of each projection's progression row alongside the shard's
own agent, which is what produces intermittent `ProgressionProgressOutOfOrderException` ("multiple processes
try to process the projection") and `23505: duplicate key value violates unique constraint
"pk_mt_event_progression"` errors. Wolverine's helper never calls `CatchUpAsync` — it resumes the agents
that already own those shards and waits for non-stale data, so there is only ever one writer.
:::

The samples shown above inlcude `Sent` message records, but there are more properties available in the `TrackedSession` object.
In accordance with the `MessageEventType` enum, you can access these properties on the `TrackedSession` object:

<!-- snippet: sample_record_collections -->
<a id='snippet-sample_record_collections'></a>
```cs
public enum MessageEventType
{
    Received,
    Sent,
    ExecutionStarted,
    ExecutionFinished,
    MessageSucceeded,
    MessageFailed,
    NoHandlers,
    NoRoutes,
    MovedToErrorQueue,
    Requeued,
    Scheduled,
    Discarded,
    Status,
    AutoFaultPublished,
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Tracking/MessageEventType.cs#L3-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_record_collections' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Let's consider we're testing a Wolverine application which publishes a message, when a change to a watched folder is detected. The part we want to test is that a message is actually published when a file is added to the watched folder. We can use the `TrackActivity` method to start a tracked session and then use the `ExecuteAndWaitAsync` method to wait for the message to be published when the file change has happened.

<!-- snippet: sample_send_message_on_file_change -->
<a id='snippet-sample_send_message_on_file_change'></a>
```cs
public record FileAdded(string FileName);

public class FileAddedHandler
{
    public Task Handle(
        FileAdded message
    ) =>
        Task.CompletedTask;
}

public class RandomFileChange
{
    private readonly IMessageBus _messageBus;

    public RandomFileChange(
        IMessageBus messageBus
    ) => _messageBus = messageBus;

    public async Task SimulateRandomFileChange()
    {
        // Delay task with a random number of milliseconds
        // Here would be your FileSystemWatcher / IFileProvider
        await Task.Delay(
            TimeSpan.FromMilliseconds(
                new Random().Next(100, 1000)
            )
        );
        var randomFileName = Path.GetRandomFileName();
        await _messageBus.SendAsync(new FileAdded(randomFileName));
    }
}

public class When_message_is_sent : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        var hostBuilder = Host.CreateDefaultBuilder();
        hostBuilder.ConfigureServices(
            services => { services.AddSingleton<RandomFileChange>(); }
        );
        hostBuilder.UseWolverine();

        _host = await hostBuilder.StartAsync();
    }
    
    [Fact]
    public async Task should_be_in_session_using_service_provider()
    {
        var randomFileChange = _host.Services.GetRequiredService<RandomFileChange>();

        var session = await _host.Services
            .TrackActivity()
            .Timeout(2.Seconds())
            .ExecuteAndWaitAsync(
                (Func<IMessageContext, Task>)(
                    async (
                        _
                    ) => await randomFileChange.SimulateRandomFileChange()
                )
            );

        session
            .Sent
            .AllMessages()
            .Count()
            .ShouldBe(1);
        
        session
            .Sent
            .AllMessages()
            .First()
            .ShouldBeOfType<FileAdded>();
    }

    [Fact]
    public async Task should_be_in_session()
    {
        var randomFileChange = _host.Services.GetRequiredService<RandomFileChange>();

        var session = await _host
            .TrackActivity()
            .Timeout(2.Seconds())
            .ExecuteAndWaitAsync(
                (Func<IMessageContext, Task>)(
                    async (
                        _
                    ) => await randomFileChange.SimulateRandomFileChange()
                )
            );

        session
            .Sent
            .AllMessages()
            .Count()
            .ShouldBe(1);
        
        session
            .Sent
            .AllMessages()
            .First()
            .ShouldBeOfType<FileAdded>();
    }

    public async ValueTask DisposeAsync() => await _host.StopAsync();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L247-L355' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_message_on_file_change' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As you can see, we just have to start our application, attach a tracked session to it, and then wait for the message to be published. This way, we can test the whole process of the application, from the file change to the message publication, in a single test.

### Finding events caused past an asynchronous boundary <Badge type="tip" text="6.49" />

A tracked session can only wait on what it can see. Let's say your handler appends an event, and an event
subscription running in the async daemon picks that event up later and publishes a message whose handler
appends more events. The tracked session is long done by the time that happens, so `AppendedEvents` will never
show you those downstream events.

What *can* follow the work across that boundary is the correlation id. If you run the tracked work under a known
correlation id, every message it causes carries that id, and an event store with correlation ids enabled records
it on every event those messages append:

```csharp
var session = await host.TrackActivity()
    .WithCorrelationId("the-trip-test")
    .InvokeMessageAndWaitAsync(new CompleteTrip(tripId));
```

From there you can query the event store by that correlation id once the async daemons have caught up.

::: warning
Correlation ids are **off by default** in Marten, Polecat and Fisher. You'll need
`opts.Events.MetadataConfig.CorrelationIdEnabled = true` in `AddMarten()`, or
`opts.Events.EnableCorrelationId = true` in `AddPolecat()` or `AddFisher()`, and the new column in your schema.
:::

The correlation id only reaches an event if every hop carries it. Wolverine's own handlers do that for you, but an
event subscription or a projection's side effect that publishes a message has to pass the event's correlation id
along itself:

<!-- snippet: sample_bobcat_pass_the_correlation_id_on -->
<a id='snippet-sample_bobcat_pass_the_correlation_id_on'></a>
```cs
public static class ParcelSubscription
{
    // The subscription passes the event's correlation id on to the message it publishes, so the events that
    // message's handler appends are recorded with the act's correlation id too
    public static ValueTask Relay(IEvent<ParcelShipped> e, IMessageBus bus)
        => bus.PublishAsync(new NotifyRecipient(e.StreamId), new DeliveryOptions { CorrelationId = e.CorrelationId });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Bobcat/Wolverine.Bobcat.Tests/events_the_act_eventually_caused.cs#L72-L82' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bobcat_pass_the_correlation_id_on' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
Polecat's async daemon does not currently load the correlation id onto the events it hands to a subscription, so on
Polecat there is nothing to pass along yet. Marten does.
:::

#### With WolverineFx.Bobcat

If you're writing specifications with WolverineFx.Bobcat, every act already runs under a correlation id of its own,
and the `ThenEventuallyCaused()` step does the waiting and the querying for you. It waits for the async daemons to
catch up, queries the event store for every event recorded with the act's correlation id, and keeps looking for a
little while (`EventuallyTimeout` on the spec or its scenario, ten seconds by default) for work that lands after the daemons, like a handler on a
local queue. Using this Marten configuration:

<!-- snippet: sample_bobcat_events_past_an_async_boundary_marten -->
<a id='snippet-sample_bobcat_events_past_an_async_boundary_marten'></a>
```cs
public sealed class MartenParcelHost : ParcelHost
{
    public override string? WhyNoAsyncBoundary => null;

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "bobcat_eventually_caused";
                m.DisableNpgsqlLogging = true;

                // ThenEventuallyCaused queries the store by the act's correlation id, which Marten only
                // records when this is on
                m.Events.MetadataConfig.CorrelationIdEnabled = true;
            })
            .IntegrateWithWolverine()
            .AddAsyncDaemon(DaemonMode.Solo)
            .PublishEventsToWolverine("parcels", relay => relay.PublishEvent<ParcelShipped>(ParcelSubscription.Relay));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Bobcat/Wolverine.Bobcat.Tests/events_the_act_eventually_caused.cs#L121-L143' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bobcat_events_past_an_async_boundary_marten' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

the specification reads like this:

<!-- snippet: sample_bobcat_then_eventually_caused -->
<a id='snippet-sample_bobcat_then_eventually_caused'></a>
```cs
var theParcel = await aBookedParcel();

await WhenReceived(new ShipParcel(theParcel));

// RecipientNotified is appended by the handler of a message an event subscription published, after the
// tracked session is over: found in the store by the act's correlation id
await ThenEventuallyCaused(new RecipientNotified(theParcel));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Bobcat/Wolverine.Bobcat.Tests/events_the_act_eventually_caused.cs#L253-L263' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bobcat_then_eventually_caused' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`ThenEventuallyCaused()` checks the events you name are *among* the ones the act caused, the same way `ThenEmitted()`
does, because the act's own events and its cascades' carry the correlation id too. Whole objects, `Expect.Value()`
and partial `Specify<T>()` objects all work.

A few things to know:

* If the event store does not record correlation ids, the step **fails** and tells you the exact setting to turn on
  for Marten, Polecat or Fisher. It does not skip, because a skipped "then" looks green.
* The act carries its tenant (`LastAct.TenantId`): the tenant an HTTP endpoint's tenant detection found, or the entry
  message's, which `WhenReceived(message, options: new DeliveryOptions { TenantId = "red" })` sets. On a store with
  conjoined tenancy (one database, the tenant as a column) the step looks for the act's events within that tenant only.
* For now, the step only works against a single database. A store that spreads its tenants across several databases
  is refused with a clear message.
* For an HTTP act, `WhenPosted()` sends the act's correlation id as the `X-Correlation-ID` header. For the events an
  endpoint appends to record it, turn on `WolverineHttpOptions.ApplyCorrelationIdHeaderToAllEndpoints` -- see
  [applying the correlation id to every endpoint](/guide/http/middleware#applying-the-correlation-id-to-every-endpoint).
  If you make your own HTTP call inside `WhenTracked()`, send `ActCorrelationId` as that header yourself.

### More WolverineFx.Bobcat: several stores, context, and foreign events <Badge type="tip" text="6.50" />

`WolverineSpec` and `WolverineScenario` inherit everything Bobcat's `CritterStackSpec` does, and a few of those steps
are worth knowing about from the Wolverine side:

* **Several event stores.** On a host with a main store and an ancillary one, such as Marten's
  `AddMartenStore<IInvoicingStore>()`, each Given and Then goes to the store whose registrations claim its type. When the
  type doesn't decide it, address the store explicitly with `In<IInvoicingStore>()` or `In("name")`. The view `In()`
  returns is still a `WolverineScenario`: an act through it is the specification's act, and the message and consistency
  boundary steps work on it. The act itself is observed on every store, so a `[MartenStore(typeof(IInvoicingStore))]`
  handler that reads the main store and appends to the ancillary one is specified in one test, and
  `ITrackedSession.AppendedEvents` says which store each commit belonged to.
* **`GivenContext<T>()`** arranges events for something the slice *reads* (its `ReadsFrom`), like a customer an
  `[Entity]` parameter loads, without claiming it as the slice's aggregate.
* **`ForeignEvents`.** An act that appends through a session Wolverine did not open is read back from the store, store by
  store. Events on streams the specification never arranged or started go to `LastAct.ForeignEvents` instead of being
  judged, and a failing whole-store step adds a "cross-talk suspected" line and, with several stores, which store each
  event came from. To claim a stream the host seeded, address it in the specification: `WhenActingOn<T>(id, ...)`,
  `GivenContext`, or `GivenEvents` on that stream.
* **A concurrent writer.** `WhileActing(concurrently: Tagged(new ItemReserved(sku, 1), sku))` lands a real append
  between the next act's read and its write, on the act's first save, so a specification can prove a Dynamic
  Consistency Boundary: inside it the act is refused with a `DcbConcurrencyException` (`ThenRefusedWith<T>()`), or a
  retry policy retries it and `ThenRetried()` passes; outside it, the act succeeds. The append is keyed by the act's own
  correlation id and lands on the first save of a session Wolverine opens for the act (a handler's or an HTTP
  endpoint's, with the store integrated with Wolverine); a session the act opens straight from the store carries no
  correlation id, so it never fires there. The hook is only added while a tracked session runs.
* **`ProjectionCatchUpTimeoutException`.** A document or read-model step that waits for the async daemons and runs out of
  time throws this (it's a `TimeoutException`), naming the shards that were behind and the identity being read.

### WolverineFx.Bobcat.Http: reads and the other verbs <Badge type="tip" text="6.50" />

`WolverineHttpSpec` (and `WolverineHttpScenario`, and its `In<T>()` views) can read as well as post. A read is an act
like any other: it runs inside the tracked session, and its exchange lands in the HTTP report a failing spec writes out.

```cs
[Fact]
public async Task a_scheduled_appointment_is_read()
{
    var id = Guid.NewGuid();
    await GivenEvents<Appointment>(id, new AppointmentScheduled(id, "Ann"));

    // GET the template, filled with its values in order; the 2xx body comes back as the type
    var appointment = await WhenReading<Appointment>("/api/appointments/{id}", id);
    appointment!.Patient.ShouldBe("Ann");

    // or address the endpoint by its RouteName
    var summary = await WhenReadingRoute<AppointmentSummary>("AppointmentSummary", id);
}

[Fact]
public async Task a_missing_appointment_is_not_found()
{
    await WhenReading<Appointment>("/api/appointments/{id}", Guid.NewGuid());
    ThenNotFound();
}
```

* **`WhenReading<T>(route, params routeValues)`** and **`WhenReadingRoute<T>(routeName, params routeValues)`** GET the
  endpoint and hand back the 2xx body as a `T`, or null for a 4xx. A 404 is a refusal, not a failure, so
  `ThenNotFound()` (or `ThenResponseIs(404)`) asserts it.
* **A read is a Query slice.** Both acts declare a GET trigger and a read model role with no command, so Bobcat puts a
  spec whose last act is a read on the Event Model as a **Query** slice that reads `T`. The slice carries the route, and
  for `WhenReadingRoute` the route name too. Pass the template or the route name as a constant so the generator can
  read it.
* **PUT, PATCH and DELETE** have the same three forms as `WhenPosted`, each a command act with its verb on the trigger:
  `WhenPut`/`WhenPatched`/`WhenDeleted(command)` find the endpoint from the command's type,
  `(command, route)` takes the route, and `WhenPutToRoute`/`WhenPatchedToRoute`/`WhenDeletedToRoute`/`WhenPostedToRoute(command, routeName)`
  address the endpoint by route name. Route parameters are filled from the command's properties, and `DELETE` sends the
  command as its JSON body.
* **`TheResponse<T>()`** reads the last HTTP act's 2xx body, for the acts that don't hand one back.

## Dealing with Scheduled Messages  <Badge type="tip" text="4.12" />

As I'm sure you can imagine, [scheduled local execution](/guide/messaging/transports/local.html#scheduling-local-execution) and [scheduled message delivery](/guide/messaging/message-bus.html#scheduling-message-delivery-or-execution)
can easily be confusing for testing and have occasionally caused trouble for Wolverine users using the tracked session functionality. At this point,
Wolverine now tracks any scheduled messages a little separately under an `ITrackedSession.Scheduled` collection, and any message that is
scheduled for later execution or delivery is automatically interpreted as "complete" in the tracked session.

You can also force the "tracked session" to immediately "replay" any scheduled messages tracked in the original session by:

1. Invoking any messages that were scheduled for local execution
2. Sending any messages that were scheduled for delivery to the original destination

and returning a brand new tracked session for the "replay."

Here's an example from our test suite. First though, here's the message handlers in question (remember, this is rigged up 
for testing):

<!-- snippet: sample_handlers_for_trigger_scheduled_message -->
<a id='snippet-sample_handlers_for_trigger_scheduled_message'></a>
```cs
public static DeliveryMessage<ScheduledMessage> Handle(TriggerScheduledMessage message)
{
    // This causes a message to be scheduled for delivery in 5 minutes from now
    return new ScheduledMessage(message.Text).DelayedFor(5.Minutes());
}

public static void Handle(ScheduledMessage message) => Debug.WriteLine("Got scheduled message");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/SlowTests/tracked_session_mechanics.cs#L151-L160' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handlers_for_trigger_scheduled_message' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And the test that exercises this functionality:

<!-- snippet: sample_dealing_with_locally_scheduled_messages -->
<a id='snippet-sample_dealing_with_locally_scheduled_messages'></a>
```cs
// In this case we're just executing everything in memory
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine");
        opts.Policies.UseDurableInboxOnAllListeners();
    }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

// Should finish cleanly
var tracked = await host.SendMessageAndWaitAsync(new TriggerScheduledMessage("Chiefs"));

// Here's how you can query against the messages that were detected to be scheduled
tracked.Scheduled.SingleMessage<ScheduledMessage>()
    .Text.ShouldBe("Chiefs");

// This API will try to immediately play any scheduled messages immediately
var replayed = await tracked.PlayScheduledMessagesAsync(10.Seconds());
replayed.Executed.SingleMessage<ScheduledMessage>().Text.ShouldBe("Chiefs");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/SlowTests/tracked_session_mechanics.cs#L71-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_dealing_with_locally_scheduled_messages' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And now, a slightly more complicated test that tests the replay of a message scheduled
to go to a completely separate application:

<!-- snippet: sample_handling_scheduled_delivery_to_external_transport -->
<a id='snippet-sample_handling_scheduled_delivery_to_external_transport'></a>
```cs
var port1 = PortFinder.GetAvailablePort();
var port2 = PortFinder.GetAvailablePort();

using var sender = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PublishMessage<ScheduledMessage>().ToPort(port2);
        opts.ListenAtPort(port1);
    }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

using var receiver = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.ListenAtPort(port2);
    }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

// Should finish cleanly
var tracked = await sender
    .TrackActivity()
    .IncludeExternalTransports()
    .AlsoTrack(receiver)
    .InvokeMessageAndWaitAsync(new TriggerScheduledMessage("Broncos"));

tracked.Scheduled.SingleMessage<ScheduledMessage>()
    .Text.ShouldBe("Broncos");

var replayed = await tracked.PlayScheduledMessagesAsync(10.Seconds());
replayed.Executed.SingleMessage<ScheduledMessage>().Text.ShouldBe("Broncos");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/SlowTests/tracked_session_mechanics.cs#L97-L127' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handling_scheduled_delivery_to_external_transport' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Extension Methods for Outgoing Messages

Your Wolverine message handlers will often have some need to publish, send, or schedule other messages as part of their work. At the unit 
test level you'll frequently want to validate the *decision* about whether or not to send a message. To aid
in those assertions, Wolverine out of the box includes some testing helper extension methods on `IEnumerable<object>`
inspired by the [Shouldly](https://github.com/shouldly/shouldly) project.

For an example, let's look at this message handler for applying a debit to a bank account that
will use [cascading messages](/guide/handlers/cascading) to raise a variable number of additional messages:

<!-- snippet: sample_accounthandler_for_testing_examples -->
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
        yield return new LowBalanceDetected(account.Id)
            .WithDeliveryOptions(new DeliveryOptions { ScheduleDelay = 1.Hours() });
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L42-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_accounthandler_for_testing_examples' title='Start of snippet'>anchor</a></sup>
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
    var messages = AccountHandler.Handle(message, account, session).ToList();

    // Now, verify that the only the expected messages are published:

    // One message of type AccountUpdated
    messages
        .ShouldHaveMessageOfType<AccountUpdated>()
        .AccountId.ShouldBe(account.Id);

    // You can optionally assert against DeliveryOptions
    messages
        .ShouldHaveMessageOfType<LowBalanceDetected>(delivery =>
        {
            delivery!.ScheduleDelay!.Value.ShouldNotBe(TimeSpan.Zero);
        })
        .AccountId.ShouldBe(account.Id);

    // Assert that there are no messages of type AccountOverdrawn
    messages.ShouldHaveNoMessageOfType<AccountOverdrawn>();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L78-L114' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handle_a_debit_that_makes_the_account_have_a_low_balance' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The supported extension methods so far are in the [TestingExtensions](https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/TestingExtensions.cs) class.

As we'll see in the next section, you can also find a matching `Envelope` for a message type.

::: tip
I'd personally organize the testing against that handler with a context/specification pattern, but I just wanted to show the extension methods here.
:::


## TestMessageContext

::: tip
This testing mechanism is admittedly just a copy of the test support in older messaging frameworks in 
.NET. It's only useful as an argument passed into a handler method. We recommend using the "Tracked Session"
approach instead.
:::

In the section above we used cascading messages, but since there are some use cases -- or maybe even just
user preference -- that would lead you to directly use `IMessageContext` to send additional messages
from a message handler, Wolverine comes with the `TestMessageContext` class that can be used as a 
[test double spy](https://martinfowler.com/bliki/TestDouble.html) within unit tests.

Here's a different version of the message handler from the previous section, but this time using `IMessageContext`
directly:

<!-- snippet: sample_debitaccounthandler_that_uses_imessagecontext -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L121-L155' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_debitaccounthandler_that_uses_imessagecontext' title='Start of snippet'>anchor</a></sup>
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

    public async ValueTask InitializeAsync()
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

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware.Tests/try_out_the_middleware.cs#L88-L140' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_when_the_account_is_overdrawn' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `TestMessageContext` mostly just collects an array of objects that are sent, published, or scheduled. The
same extension methods explained in the previous section can be used to verify the outgoing messages
and even *how* they were published.

As of Wolverine 1.8, `TestMessageContext` also supports limited expectations for request and reply using `IMessageBus.InvokeAsync<T>()`
as shown below:

<!-- snippet: sample_using_invoke_with_expected_response_with_test_message_context -->
<a id='snippet-sample_using_invoke_with_expected_response_with_test_message_context'></a>
```cs
var spy = new TestMessageContext();
var context = (IMessageContext)spy;

// Set up an expected response for a message
spy.WhenInvokedMessageOf<NumberRequest>()
    .RespondWith(new NumberResponse(12));

// Used for:
var response1 = await context.InvokeAsync<NumberResponse>(new NumberRequest(4, 5));

// Set up an expected response with a matching filter
spy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 4)
    .RespondWith(new NumberResponse(12));

// Set up an expected response for a message to an explicit destination Uri
spy.WhenInvokedMessageOf<NumberRequest>(destination:new Uri("rabbitmq://queue/incoming"))
    .RespondWith(new NumberResponse(12));

// Used to set up:
var response2 = await context.EndpointFor(new Uri("rabbitmq://queue/incoming"))
    .InvokeAsync<NumberResponse>(new NumberRequest(5, 6));

// Set up an expected response for a message to a named endpoint
spy.WhenInvokedMessageOf<NumberRequest>(endpointName:"incoming")
    .RespondWith(new NumberResponse(12));

// Used to set up:
var response3 = await context.EndpointFor("incoming")
    .InvokeAsync<NumberResponse>(new NumberRequest(5, 6));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/TestMessageContextTests.cs#L566-L597' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_invoke_with_expected_response_with_test_message_context' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // Other configuration...

    // IF the environment is "Testing", turn off all external transports
    if (builder.Environment.IsDevelopment())
    {
        opts.StubAllExternalTransports();
    }
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/TestingSupportSamples.cs#L20-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conditionally_disable_transports' title='Start of snippet'>anchor</a></sup>
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

    .StartAsync(cancellationToken: TestContext.Current.CancellationToken);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/disabling_all_external_transports.cs#L12-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_external_transports' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware.Tests/try_out_the_middleware.cs#L23-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_the_transports_from_web_application_factory' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the sample above, I'm bootstrapping the `IHost` for my production application with 
all the external transports turned off in a way that's appropriate for integration testing
message handlers within the main application.

## Resetting All Wolverine Storage in Tests <Badge type="tip" text="6.22" />

If your application uses durable messaging and/or one of the
[database-backed queue transports](/guide/messaging/transports/postgresql), stale rows carried between
test runs are a classic source of order-dependent failures. `IHost.ClearAllWolverineStorageAsync()` is
the recommended reset for that situation:

<!-- snippet: sample_clear_all_wolverine_storage -->
<a id='snippet-sample_clear_all_wolverine_storage'></a>
```cs
// IHost would be your application in a testing harness
public static async Task reset_everything(IHost host)
{
    // Rebuilds the envelope storage schema for every known message store -- the main
    // store, every tenant database, and every ancillary store -- AND leaves the tables
    // of every database-backed queue transport built, but empty.
    //
    // RebuildAsync() / ClearAllAsync() only ever touch envelope storage. This is the
    // one call that also reaches the queue transport tables.
    await host.ClearAllWolverineStorageAsync();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L51-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_clear_all_wolverine_storage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The method is the union of two things:

1. A `RebuildAsync()` against **every** known message store — the main store, every tenant database in a
   separate-database-per-tenant system, and every ancillary store.
2. Every database-backed queue transport's tables left **built but empty**, fanning out across every tenant
   database. That covers PostgreSQL, SQL Server, MySQL, Oracle, SQLite, and Redis streams.

"Built but empty" means the tables are created if they are missing, then emptied — so the reset works
whether the previous run left rows behind or tore the schema down completely.

::: tip
This is deliberately opt-in rather than folded into `RebuildAsync()` / `ClearAllAsync()`, which stay scoped
to [envelope storage only](/guide/durability/managing). Queue transport tables are transport data, and other
tables registered on the message store — SQL Server's rate-limit table, for example — must survive a reset.
:::

It is safe to call on a host with no message store and no database-backed queues, so you can put it in a
shared test fixture base class without breaking your storeless tests.

## Running Wolverine in "Solo" Mode <Badge type="tip" text="3.0" />

Wolverine's [leadership election](/guide/durability/leadership-and-troubleshooting.html#troubleshooting-and-leadership-election) process is necessary for distributing several background tasks in real life production,
but that subsystem can lead to some inconvenient sluggishness in [cold start times](https://dontpaniclabs.com/blog/post/2022/09/20/net-cold-starts/#:~:text=In%20software%20development%2C%20cold%20starts,have%20an%20increased%20start%20time.) in automation testing.

To sidestep that problem, you can direct Wolverine to run in "Solo" mode where the current process assumes that it's the 
only running node and automatically starts up all known background tasks immediately. 

To do so, you could do something like this in your main `Program` file:

<!-- snippet: sample_configuring_the_solo_mode -->
<a id='snippet-sample_configuring_the_solo_mode'></a>
```cs
var builder = Host.CreateApplicationBuilder();

builder.UseWolverine(opts =>
{
    opts.Services.AddMarten("some connection string")

        // This adds quite a bit of middleware for
        // Marten
        .IntegrateWithWolverine();

    // You want this maybe!
    opts.Policies.AutoApplyTransactions();

    if (builder.Environment.IsDevelopment())
    {
        // But wait! Optimize Wolverine for usage as
        // if there would never be more than one node running
        opts.Durability.Mode = DurabilityMode.Solo;
    }
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DurabilityModes.cs#L53-L79' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_the_solo_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or if you're using something like [WebHostFactory](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-8.0) to 
bootstrap your Wolverine application in an integration testing harness, you can use this helper to override Wolverine into being
"Solo":

<!-- snippet: sample_using_run_wolverine_in_solo_mode_with_extension -->
<a id='snippet-sample_using_run_wolverine_in_solo_mode_with_extension'></a>
```cs
// This is bootstrapping the actual application using
// its implied Program.Main() set up
// For non-Alba users, this is using IWebHostBuilder 
Host = await AlbaHost.For<WolverineWebApi.Program>(x =>
{
    x.ConfigureServices(services =>
    {
        // Override the Wolverine configuration in the application
        // to run the application in "solo" mode for faster
        // testing cold starts
        services.RunWolverineInSoloMode();

        // And just for completion, disable all Wolverine external
        // messaging transports
        services.DisableAllExternalWolverineTransports();
    });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/IntegrationContext.cs#L25-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_run_wolverine_in_solo_mode_with_extension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Stubbing Message Handlers <Badge type="tip" text="5.1" />

To extend the test automation support even further, Wolverine now has a capability to "stub"
out message handlers in testing scenarios with pre-canned behavior for more reliable testing
in some situations. This feature was mostly conceived of for stubbing out calls to external
systems through `IMessageBus.InvokeAsync<T>()` where the request would normally be sent to 
an external system through a subscriber. 

Jumping into an example, let's say that your system interacts with another service that estimates
delivery costs for ordering items. At some point in the system you might reach out through
a request/reply call in Wolverine to estimate an item delivery before making a purchase
like this code:

<!-- snippet: sample_code_showing_remote_request_reply -->
<a id='snippet-sample_code_showing_remote_request_reply'></a>
```cs
// This query message is normally sent to an external system through Wolverine
// messaging
public record EstimateDelivery(int ItemId, DateOnly Date, string PostalCode);

// This message type is a response from an external system
public record DeliveryInformation(TimeOnly DeliveryTime, decimal Cost);

public record MaybePurchaseItem(int ItemId, Guid LocationId, DateOnly Date, string PostalCode, decimal BudgetedCost);
public record MakePurchase(int ItemId, Guid LocationId, DateOnly Date);
public record PurchaseRejected(int ItemId, Guid LocationId, DateOnly Date);

public static class MaybePurchaseHandler
{
    public static Task<DeliveryInformation> LoadAsync(
        MaybePurchaseItem command, 
        IMessageBus bus, 
        CancellationToken cancellation)
    {
        var (itemId, _, date, postalCode, budget) = command;
        var estimateDelivery = new EstimateDelivery(itemId, date, postalCode);
        
        // Let's say this is doing a remote request and reply to another system
        // through Wolverine messaging
        return bus.InvokeAsync<DeliveryInformation>(estimateDelivery, cancellation);
    }
    
    public static object Handle(
        MaybePurchaseItem command, 
        DeliveryInformation estimate)
    {

        if (estimate.Cost <= command.BudgetedCost)
        {
            return new MakePurchase(command.ItemId, command.LocationId, command.Date);
        }

        return new PurchaseRejected(command.ItemId, command.LocationId, command.Date);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/StubbingHandlers.cs#L116-L157' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_code_showing_remote_request_reply' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And for a little more context, the `EstimateDelivery` message will always be sent to
an external system in this configuration:

<!-- snippet: sample_configuring_estimate_delivery -->
<a id='snippet-sample_configuring_estimate_delivery'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts
        .UseRabbitMq(builder.Configuration.GetConnectionString("rabbit")!)
        .AutoProvision();

    // Just showing that EstimateDelivery is handled by
    // whatever system is on the other end of the "estimates" queue
    opts.PublishMessage<EstimateDelivery>()
        .ToRabbitQueue("estimates");
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/StubbingHandlers.cs#L14-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_estimate_delivery' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using our 

In testing scenarios, maybe the external system isn't available at all, or it's just much more 
challenging to run tests that also include the external system, or maybe you'd just like to 
write more isolated tests against your service's behavior before even trying to integrate
with the other system (my personal preference anyway). To that end we can now stub
the remote handling like this:

<!-- snippet: sample_using_stub_handler_in_testing_code -->
<a id='snippet-sample_using_stub_handler_in_testing_code'></a>
```cs
public static async Task try_application(IHost host)
{
    host.StubWolverineMessageHandling<EstimateDelivery, DeliveryInformation>(
        query => new DeliveryInformation(new TimeOnly(17, 0), 1000));

    var locationId = Guid.NewGuid();
    var itemId = 111;
    var expectedDate = new DateOnly(2025, 12, 1);
    var postalCode = "78750";

    var maybePurchaseItem = new MaybePurchaseItem(itemId, locationId, expectedDate, postalCode,
        500);
    
    var tracked =
        await host.InvokeMessageAndWaitAsync(maybePurchaseItem);
    
    // The estimated cost from the stub was more than we budgeted
    // so this message should have been published
    
    // This line is an assertion too that there was a single message
    // of this type published as part of the message handling above
    var rejected = tracked.Sent.SingleMessage<PurchaseRejected>();
    rejected.ItemId.ShouldBe(itemId);
    rejected.LocationId.ShouldBe(locationId);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/StubbingHandlers.cs#L31-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_stub_handler_in_testing_code' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

After calling making this call:

```csharp
        host.StubWolverineMessageHandling<EstimateDelivery, DeliveryInformation>(
            query => new DeliveryInformation(new TimeOnly(17, 0), 1000));

```

Calling this from our Wolverine application:

```csharp
        // Let's say this is doing a remote request and reply to another system
        // through Wolverine messaging
        return bus.InvokeAsync<DeliveryInformation>(estimateDelivery, cancellation);
```

Will use the stubbed logic we registered. This is enabling you to use fake behavior for
difficult to use external services. 

For the next test, we can completely remove the stub behavior and revert back to the 
original configuration like this:

<!-- snippet: sample_clearing_out_stub_behavior -->
<a id='snippet-sample_clearing_out_stub_behavior'></a>
```cs
public static void revert_stub(IHost host)
{
    // Selectively clear out the stub behavior for only one message
    // type
    host.WolverineStubs(stubs =>
    {
        stubs.Clear<EstimateDelivery>();
    });
    
    // Or just clear out all active Wolverine message handler
    // stubs
    host.ClearAllWolverineStubs();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/StubbingHandlers.cs#L61-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_clearing_out_stub_behavior' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or instead, we can just completely replace the previously registered stub behavior
with completely new logic that will override our previous stub:

<!-- snippet: sample_override_previous_stub_behavior -->
<a id='snippet-sample_override_previous_stub_behavior'></a>
```cs
public static void override_stub(IHost host)
{
    host.StubWolverineMessageHandling<EstimateDelivery, DeliveryInformation>(
        query => new DeliveryInformation(new TimeOnly(17, 0), 250));

}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/StubbingHandlers.cs#L78-L86' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_override_previous_stub_behavior' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

So far, we've only looked at simple request/reply behavior, but what if a remote system 
receiving our message potentially makes multiple calls back to our system? Or really just
any kind of interaction more complicated than a single response for a request message?

We're still in business, we just have to use a little uglier signature for our stub:

<!-- snippet: sample_using_more_complex_stubs -->
<a id='snippet-sample_using_more_complex_stubs'></a>
```cs
public static void more_complex_stub(IHost host)
{
    host.WolverineStubs(stubs =>
    {
        stubs.Stub<EstimateDelivery>(async (
            EstimateDelivery message, 
            IMessageContext context, 
            IServiceProvider services,
            CancellationToken cancellation) =>
        {
            // do whatever you want, including publishing any number of messages
            // back through IMessageContext
            
            // And grab any other services you might need from the application 
            // through the IServiceProvider -- but note that you will have
            // to deal with scopes yourself here

            // This is an equivalent to get the response back to the 
            // original caller
            await context.PublishAsync(new DeliveryInformation(new TimeOnly(17, 0), 250));
        });
    });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/StubbingHandlers.cs#L88-L113' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_more_complex_stubs' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A few notes about this capability:

* You can use any number of stubs for different message types at the same time
* Most of the testing samples use extension methods on `IHost`, but we know there are some users who bootstrap only an IoC container for integration tests, so all of the extension methods shown in this section are also available off of `IServiceProvider`
* The "stub" functions are effectively singletons. There's nothing fancier about argument matching or anything you might expect from a full fledged mock library like NSubstitute or FakeItEasy
* You can actually fake out the routing to message types that are normally handled by handlers within the application
* We don't believe this feature will be helpful for "sticky" message handlers where you may have multiple handlers for the same message type interally



