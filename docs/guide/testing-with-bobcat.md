# Specifications with Bobcat

[Bobcat](https://bobcat.jasperfx.net) is a spec-driven integration testing framework for .NET: your specifications are
ordinary xUnit, TUnit, NUnit or MSTest tests that Bobcat records and renders as readable Given/When/Then
specifications. Bobcat itself takes no dependency on JasperFx or Wolverine. Everything that knows about the Critter
Stack ships from this repository instead, as **WolverineFx.Bobcat** and **WolverineFx.Bobcat.Http**, and this page is
the reference for all of it:

* the Given/When/Then vocabulary for a Wolverine application: arrange events, act through Wolverine's tracked
  session, and assert what the act appended, what it cascaded, and what the aggregates and read models became
* the store-only vocabulary for an application on Marten, Polecat or Fisher with no message bus
* the store helpers underneath both, [`SpecIdentityAudit`](#spec-identity-audit), handler warm-up, transport draining and [container checks](#container-checks)

Every call is a step, and every step renders. For writing specifications in general (step helpers, `SpecAssert`,
tables, binding a test to an Event Model slice) and for running them, see the
[Bobcat documentation](https://bobcat.jasperfx.net).

```bash
dotnet add package WolverineFx.Bobcat
dotnet add package WolverineFx.Bobcat.Http   # only for Wolverine.HTTP acts
```

## Packages and layers {#packages-and-layers}

The vocabulary is built in layers, each deriving from the one above it:

| | Package | Adds |
|---|---|---|
| `CritterStackScenario` / `CritterStackSpec` (namespace `Bobcat.CritterStack`) | `WolverineFx.Bobcat` | everything a store can answer: arranging events and read models, store-only acts, event, aggregate, document and read model assertions, refusals, clocks. No message bus. See [Without Wolverine: the store-only vocabulary](#store-only) |
| `WolverineScenario` / `WolverineSpec` (namespace `Wolverine.Bobcat`) | `WolverineFx.Bobcat` | acts through Wolverine's tracked session, message assertions, scheduled messages, Dynamic Consistency Boundary steps, and `ThenEventuallyCaused` |
| `WolverineHttpScenario` / `WolverineHttpSpec` (namespace `Wolverine.Bobcat.Http`) | `WolverineFx.Bobcat.Http` | HTTP acts through Alba: `WhenPosted`, `WhenPut`, `WhenPatched`, `WhenDeleted`, the body-less acts and the reads, with the route resolved from the request type, a template or a route name |

`CritterStackScenario` lived in the `Bobcat` package before Bobcat 1.0.0-rc.2. It moved here, under the same
namespace, when Bobcat dropped its JasperFx dependency, so a `using Bobcat.CritterStack;` doesn't change; reference
`WolverineFx.Bobcat` beside `Bobcat`.

The `*Scenario` class holds the steps. The `*Spec` class is an abstract base for a test class that exposes every
step of its scenario as its own member, so a test reads `await WhenReceived(...)` rather than
`await Scenario.WhenReceived(...)`. Every member is public, so your own helpers in another class can drive a spec
too, and `Scenario` is there when you need the object itself.

## A first specification {#a-first-specification}

```csharp
using Bobcat;
using Wolverine.Bobcat;

[BobcatFeature("Appointments")]
public class confirming_an_appointment(AppFixture app) : WolverineSpec(app.Host)
{
    [Fact]
    public async Task a_scheduled_appointment_is_confirmed()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id));

        await WhenReceived(new ConfirmAppointment(id));

        ThenEvents(typeof(AppointmentConfirmed));
        ThenMessageSent<SendConfirmationEmail>();
    }
}
```

`AppFixture` is your own: an xUnit assembly, collection or class fixture that boots the application's `IHost` once
(below). `[BobcatFeature]` is what makes the test a specification; on TUnit, NUnit and MSTest put `[BobcatScenario]`
beside it (see [Writing Specifications](https://bobcat.jasperfx.net/marker-steps#what-you-have-to-add)).

**A wrong is gathered, not thrown.** Inside a recording scenario a failed `Then…` reports on its own step and the
test carries on, so a specification shows every disagreement, and the adapter fails the test once at the end.
Outside one (the same test with no adapter) the wrong is thrown, `SpecificationFailedException` from a Wolverine
scenario and `SpecAssertionException` from a `CritterStackScenario`, so a plain test cannot pass by accident.

### Hosting the application once {#hosting-the-application-once}

Bobcat doesn't own your test lifecycle; your test runner does. Boot the application in a fixture exactly as you would
for any integration test. This one is from the
[BankAccountES sample](https://github.com/JasperFx/wolverine/tree/main/src/Samples/Bobcat/BankAccountES):

```cs
public class BankAccountHost : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await AlbaHost.For<Program>();

        // Not load-bearing for correctness: every spec mints its own ids, so no run can collide
        // with the last one. It is here so the event store does not grow without bound across runs.
        // ResetEventStoresAsync empties every store the host registers -- documents AND streams --
        // through JasperFx.Events.IEventStore, so it reads the same on Marten and Fisher.
        await Host.ResetEventStoresAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }
}
```

The sample registers it once for the run, with `[assembly: AssemblyFixture(typeof(BankAccountHost))]`. Any `IHost`
works; Alba is only there because the sample also drives HTTP endpoints. A `CritterStackSpec` can be built from an
`IServiceProvider` instead, for a test that has a container and no host.

::: tip Resetting between specifications
Mint new ids in every specification and most suites never need a reset between tests. When one does, call
`ResetAsync()` on the spec (it clears every event store and forgets the last act), or `host.ResetEventStoresAsync()`
from your own fixture. Both pause the async daemons while the stores are emptied and resume them afterwards, so a
host running async projections needs no store-specific reset. A broker is a different matter: see
[Cold hosts and brokers](#cold-hosts-and-brokers).
:::

## Values a step takes {#values-a-step-takes}

Most steps take events, commands or documents as `object`, and accept any of these:

| | |
|---|---|
| a whole object | compared **structurally**: property by property, nested properties and collections included, never through `Equals` |
| `Expect.Value(x).Ignoring(e => e.OccurredAt)` | a whole object with members nobody controls shown but not judged |
| `Specify<T>().With(x => x.Prop, value)` | a **partial object**: judged and shown on only the members it names. Handed to a Given or a When, it is *built*, with every unspecified member filled by `UnspecifiedValues` |
| `Specify<T>(table)` | a partial object written as a table: `Property \| Value` rows, or the members as headers over one row |
| `Specify<T>(x => x.Age.ShouldBe(4), …)` | a partial object to check against, written as assertions on its members. **Needs C# 14**, see below |
| `typeof(T)` | the type alone, whatever its values (`ThenEvents(params Type[])`) |
| `Tagged(value, tags…)` / `Specify<T>().Tagged(tags…)` | an event expected (or arranged) with exactly these Dynamic Consistency Boundary tags |

`Specify`, `Tagged` and `Expect.Value` are static, so a `*Spec` class reaches them unqualified. Prefer a
deterministic clock to `Ignoring(...)` for a timestamp.

Every form, with what each one judges and how it renders, is in
[Expected Objects](https://bobcat.jasperfx.net/expected-objects).

::: warning A member check needs C# 14
`Specify<T>(x => x.Age.ShouldBe(4))` is an expression tree (that is how Bobcat reads the member path and the expected
value out of it), and before C# 14 an expression tree cannot leave out an optional argument, which every Shouldly
assertion has (`customMessage`). The error is **CS0854**, "An expression tree may not contain a call or invocation
that uses optional arguments".

On C# 12 or 13 (net9.0, or a pinned `LangVersion`; Wolverine's own tests pin C# 12) either write every optional
argument out positionally, `x => x.Age.ShouldBe(4, null)`, or use the `.With(…)` chain or a table instead.
[The full table is in Expected Objects](https://bobcat.jasperfx.net/expected-objects#member-checks-and-the-c-version-they-need).
:::

## Arrange {#arrange}

| Step | |
|---|---|
| `GivenEvents<TAggregate>(id, params events)` | the stream the scenario acts against already holds exactly these events. `id` is a `Guid`, a `string` key, or a strong-typed id around either |
| `GivenEvents(Type? aggregate, id, params events)` | the same with the type as a value. A null aggregate arranges a stream with no aggregate type |
| `GivenEvents(id, params events)` | a stream with no aggregate type, for an event model that names none |
| `GivenNoEventsFor<TAggregate>(id)` | the stream has no events yet. Appends nothing, says so, and names the stream the act's events are read from |
| `GivenEventsOn<TAggregate>(id, params events)` | events on a *different* stream from the one the act runs against: a second aggregate for a rule that spans two, or history a read model fans in from |
| `GivenContext<TContext>(id, params events)` | a stream the act only *looks up* (the Client an `OpenAccount` loads), not the one it decides against. The same append and step as `GivenEvents`; on the Event Model it is the slice's `ReadsFrom`, never an aggregate. Also `GivenContext(Type, id, params events)` |
| `GivenReadModel<T>(document)` | a read model (or any document) already exists, stored directly; it reads `there is a TodoList with Title: "Duplicate"`, leaving out an `Id` the store has yet to assign. It bypasses the projection, so a spec about how the view is built should arrange the events instead. On the Event Model it is the slice's read model only on a View slice; before a command it is a lookup, the slice's `ReadsFrom`. `T` is inferred from a typed document or a `Specify<T>()`: `GivenReadModel(new Flight { … })`. Give it explicitly only for a document held as `object` |
| `GivenReceived(message, configureTracking?, options?)` (`WolverineSpec`) | arrange by handling a message: it is sent through Wolverine and the scenario waits for everything it caused, exactly as for `WhenReceived`, but the step is a **Given** and it is *not* the act. `LastAct` is left alone, and whatever its handlers appended is arranged history: the streams they wrote are the specification's own |
| `GivenTheDateIs(date)` / `GivenTheTimeIs(now)` | freeze the ambient clock (`BobcatClock.Current`, also `Clock`) |
| `WhenTheClockAdvancesBy(duration)` / `WhenTimePasses(duration)` / `WhenTheClockAdvancesTo(instant)` | move it forward. Each renders as its own step |

`GivenReceived` fails the step and stops the scenario when a handler throws, since an arrangement that failed leaves
nothing for the act to be judged against, and when the message is only scheduled for later ("it was only scheduled
for later, so its handler never ran and it arranged nothing. Send it without a delay."). It carries a Document role
on the Event Model, never a Command role or a trigger, so the arranging message never becomes the slice's act.

## Act {#act}

The **last** act a test runs is what every `Then…` judges. Nothing an act throws is rethrown: the exception is
captured on `LastAct`, so a refusal can be asserted afterwards.

| Step | |
|---|---|
| `WhenReceived(message, configureTracking?, options?)` | `message` arrives at the application through Wolverine, and the scenario waits for everything it caused to settle: cascades, forwarded events, local queues. `options` is Wolverine's `DeliveryOptions`, so `new DeliveryOptions { TenantId = "red" }` sends it for a tenant |
| `WhenPublished(message, configureTracking?, options?)` | published to every subscriber, and the scenario waits for what it caused |
| `WhenScheduledMessagesAreExecuted(timeout?)` | the messages the last act scheduled for later are executed now, and this is the new act. See [Scheduled messages](#scheduled-messages) |
| `WhenTracked(description, act, configureTracking?)` | any act that reaches the application from outside (an HTTP call, a client SDK, a hosted service poke) run inside the tracked session. What the act reaches sends or schedules is what the act caused. It cannot tell the act's events from its cascades' and never records a refusal: see [HTTP without WolverineFx.Bobcat.Http](#http-without-wolverinefx-bobcat-http) |
| `WhenTracked(description, bus => …, configureTracking?)` | an act of your own, handed the tracked session's `IMessageBus`. What it sends, publishes or schedules through `bus` is the act's **own** message, exactly as `WhenReceived`'s message is: never counted as sent, and when only scheduled, caught by the [only-scheduled guard](#the-only-scheduled-guard) |
| `WhenActing(description, services => …)` | any act at all, handed a fresh service scope. In a `WolverineScenario` it runs inside the tracked session too, so it waits for what it caused and carries the messages it sent. A message its own code sends through an `IMessageBus` or `IMessageContext` it resolves from `services` is its own message, as for the `bus` overload above |
| `WhenActingOn<T>(id, description?, act, params events)` | one paragraph of three sentences: capture the stream's version, run the act, and check the stream gained exactly `events` past that version |
| `WhenPosted(command, configure?)` (`WolverineHttpSpec`) | POST `command` to the one Wolverine.HTTP endpoint that accepts its type, filling route parameters from its properties. Several candidates are refused rather than guessed |
| `WhenPosted(command, route, configure?)` / `WhenPosted<TResponse>(…)` | POST to a route template; the generic form hands back the response body |
| `WhenPostedToRoute(command, routeName, configure?)` | POST to the endpoint with that ASP.NET Core route name (`[WolverinePost("/api/orders", RouteName = "PlaceOrder")]`) |
| `WhenPut` / `WhenPatched` / `WhenDeleted` (and `…ToRoute`) | the same three forms for PUT, PATCH and DELETE. A DELETE sends the command as its JSON body |
| `WhenPostedTo(route, params routeValues)` / `WhenPutTo` / `WhenPatchedTo` / `WhenDeletedAt` | an HTTP write with **no body**, for an endpoint that takes everything from its route: `WhenPostedTo("/api/proposals/{id}/accept", id)`. The template's values come after it, in order |
| `WhenPostedTo(route, configure, params routeValues)` (and the same for the other three) | the same, with an Alba `Action<Scenario>` applying headers or authentication on top: `WhenDeletedAt("/api/appointments/{id}", x => x.WithRequestHeader("x-tenant", "red"), id)` |
| `WhenReading<T>(route, params routeValues)` / `WhenReadingRoute<T>(routeName, …)` | GET a route template, its values after it in order, and hand back the 2xx body as a `T`, or null for a 4xx. A spec whose last act is a read describes a **Query** slice |

`configureTracking` adjusts the `TrackedSessionConfiguration` for one act. Over HTTP, any 2xx is success, a 4xx is a
*refusal* (an answer, captured for `ThenRefusedWith` and `ThenResponseIs`) and a 5xx is the act failing. The
`Action<Scenario>` overloads apply Alba overrides (headers, authentication) on top. Every HTTP act's request and
response land in the *HTTP exchanges* report a failing spec writes out.

A body-less act's step reads as the route template, which says which endpoint; the filled URL is in the HTTP
exchanges report. Write the template as a constant, so the generator can read it: the act reaches the Event Model as
an HTTP command slice on that route, with no command type, since it sends none. Before these acts existed,
`WhenPosted(new { }, route)` worked but read as an anonymous type.

`WhenActing` in a `WolverineScenario` reads the act's events from what the tracked session heard committed. When it
heard no commit at all (the act appended through a session Wolverine didn't open), it falls back to the store's
high-water read, store by store, as [the store-only act](#the-store-only-act) does.

### Scheduled messages {#scheduled-messages}

An act sends its message exactly as given, as Wolverine sends it. A `TimeoutMessage`, a `DelayedFor` or
`ScheduledAt` wrapper, or `DeliveryOptions` with a delay is only **scheduled**, and its handler does not run within
the act. That is consistent with Wolverine's own tracked sessions, where running scheduled messages is the explicit
`ITrackedSession.PlayScheduledMessagesAsync()`. Bobcat's explicit step is `WhenScheduledMessagesAreExecuted`:

```csharp
await WhenReceived(new ShipmentDispatched(id));          // the saga schedules DeliverySlaExpired
ThenMessageScheduled<DeliverySlaExpired>(5.Days());

await WhenScheduledMessagesAreExecuted();                // the new act: the timeout handler runs now
ThenMessageSent<EscalateLateShipment>();
```

`WhenScheduledMessagesAreExecuted(TimeSpan? timeout = null)` executes every message the last act scheduled, a local
one inline and any other sent to its destination, and becomes the new act: `LastAct` is what the replay did, so the
Then steps after it judge the scheduled messages' handlers (a saga's timeout, a `DelayedFor` reminder, a
`TimeoutMessage` sent with `WhenReceived`). `timeout` is how long it waits for everything the replay caused to settle,
`Timeout` by default. The replay keeps the scheduled messages' correlation id, so `ThenEventuallyCaused` still finds
what they caused. With no act before it, or an act that scheduled nothing, the step fails and stops the scenario,
saying what the last act did instead. It stamps nothing on the Event Model: which messages are scheduled is only
known at run time.

#### The only-scheduled guard {#the-only-scheduled-guard}

When the act's **own** message was only scheduled, no handler ran, so a step about what a handler did has nothing to
judge, and the steps that would otherwise pass on nothing fail instead:

```
The act's EscalationDue was only scheduled for later, so no handler ran and nothing this step checks could have
happened. To run it, add `await WhenScheduledMessagesAreExecuted();` after the act.
```

That covers the positive message steps (`ThenMessageSent`, `ThenHandled`, …), the negative ones (`ThenNoMessageSent`,
`ThenNoMessageScheduled<T>` for any other type, `ThenNotHandled`), `ThenMessagesSent`, and the store steps that judge
an absence: `ThenNoEvents`, `ThenNotEmitted` and `ThenCommandRefused`. `ThenTheActSucceeded` still passes, because
scheduling the message did succeed.

The act's own message is the one `WhenReceived` or `WhenPublished` dispatched, or what `WhenTracked`'s `bus` or
`WhenActing`'s resolved bus sent. **A timeout scheduled by a handler or an HTTP endpoint is not the act's own
message**, so the guard says nothing about it: after `WhenPosted(...)` to an endpoint that schedules a timeout,
`ThenMessageScheduled<T>()` sees the timeout, and running its handler still takes an explicit
`WhenScheduledMessagesAreExecuted()`.

### HTTP without WolverineFx.Bobcat.Http {#http-without-wolverinefx-bobcat-http}

`WolverineFx.Bobcat.Http` depends on Alba 9 (currently 9.0.0-rc.2, see Bobcat's
[Compatibility](https://bobcat.jasperfx.net/compatibility) page), so a suite on Alba 8.x often acts over HTTP with
`WhenTracked("…", () => host.Scenario(…))` instead. That works, but two verdicts differ from `WhenPosted` and the other
HTTP acts:

- **Cascades count as the act's own events.** A message-based act knows the envelope it dispatched, so `ThenEvents`
  judges only what that message's handler appended. `WhenTracked` dispatches nothing the scenario can see, so every
  event committed during the tracked session is the act's. When the endpoint's handler cascades a message whose
  handler appends too, `ThenEvents(typeof(AssignmentAccepted))` fails with `Expected AssignmentAccepted but the act
  appended AssignmentAccepted, AppointmentProposed`. Judge with `ThenEmitted(...)`, which ignores what else was
  appended, or list the cascaded events in `ThenEvents` as well.
- **A 4xx is not a refusal.** Only the `WolverineFx.Bobcat.Http` acts read the response status, so after `WhenTracked`
  a 400 ProblemDetails leaves `LastAct` successful and `ThenRefusedWith("…")` fails with
  `Expected the act to be refused with "…", but it succeeded.` Assert the status and the body inside the Alba
  scenario (`x.StatusCodeShouldBe(400)`, `x.ContentShouldContain("…")`): a failure there is thrown by Alba and
  captured as the act's error, not as a refusal.

`WhenTracked` also stamps nothing on the Event Model (see [The Event Model, from the vocabulary](#the-event-model-from-the-vocabulary)).
Until `WolverineFx.Bobcat.Http` ships against a GA Alba, the `WolverineFx.Bobcat.Http` acts are the ones that
attribute and refuse.

## Assert: events {#assert-events}

| Step | |
|---|---|
| `ThenEvents(params events)` | the act appended exactly these events, in order |
| `ThenEvents(params Type[] events)` | exactly these event types, in order |
| `ThenEventsInAnyOrder(params events)` / `(params Type[])` | exactly these, in any order |
| `ThenEmitted(params events)` / `ThenEmitted<T>()` | these among whatever else it appended; nothing else is judged, or shown |
| `ThenNotEmitted<T>()` / `ThenNotEmitted(params events)` | none of these. A partial object forbids only the events that agree on the members it names |
| `ThenNoEvents()` | the act appended nothing: the refusal half of a guard |
| `ThenCaused(params events)` / `ThenCaused<T>()` | these among everything appended while the act ran, **its cascades included** (`LastAct.AllNewEvents`): an event a cascaded handler on a durable local queue appended. Needs no correlation ids, unlike `ThenEventuallyCaused`; for a store-only act it reads the same events as `ThenEmitted`. Stamps no Event Model role, since a cascaded event belongs to its own handler's slice |
| `ThenEventsOn<TAggregate>(id, params events)` / `ThenNoEventsOn<TAggregate>(id)` | exactly these on that stream, for a command that decides against several. Only what the act added counts |
| `ThenStreamIsStarted<TAggregate>(id)` | the act started that stream |
| `ThenStreamIsStartedWithEvents<TAggregate>(params events)` | one paragraph: a stream was started, with an id the handler assigned, and these events are on it |

**"The act's events" in a `WolverineScenario` are its own**: what the sessions handling the act's message (or its
HTTP request) committed. A cascade's events are in `LastAct.AllNewEvents`, not `LastAct.NewEvents`. A store-only act
can't tell the two apart, so there they are the same list.

**A store-only act attributes what it reads back by stream.** An event counts when it is on a stream the
specification arranged (any Given), addressed (`WhenActingOn`), or that one of its acts wrote to or started. Anything
appended meanwhile to another stream that already existed is `LastAct.ForeignEvents`: never judged by the
whole-store steps above, and named by them when they fail ("cross-talk suspected: N event(s) on streams this
specification never arranged or started were appended during the act"). A step that names a stream
(`ThenEventsOn<T>(id, …)`) or a tag query still sees what was appended there. A stream the host seeds outside the
vocabulary is foreign until the spec arranges or addresses it.

### A named stream from a named version {#a-named-stream-from-a-named-version}

For an act whose events nothing can attribute (a domain service, a background job run by hand, an application with
no message bus), the spec names **which stream, and past which version**:

```csharp
await GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"), new WalletCredited(id, 10));
var before = await TheStreamVersion(id);                       // 2

await WhenActing("the wallet is credited by hand",
    services => services.GetRequiredService<WalletService>().Credit(id, 25));

await ThenEventsOn<Wallet>(id, before, new WalletCredited(id, 25));
await ThenNoEventsOn<Wallet>(id, before + 1);
```

| Step | |
|---|---|
| `TheStreamVersion(id)` | the version the stream stands at now; 0 with no events. Not a step |
| `ThenEventsOn<T>(id, afterVersion, params events)` | the stream gained exactly these, in order, past that version, whoever appended them |
| `ThenNoEventsOn<T>(id, afterVersion)` | it gained nothing |

A version past the stream's end is refused as a broken spec rather than passing vacuously. `WhenActingOn<T>` does all
three in one call and reads as one paragraph:

```
Given the Wallet stream Wallet is at version 2
When the wallet is credited by hand
Then WalletCredited is emitted on it
```

## Assert: aggregates, documents and read models {#assert-aggregates-documents-and-read-models}

Every step that reads a projection first waits for the async daemons to catch up, up to `ProjectionTimeout` (20
seconds by default). An async projection read straight after the act would read a stale document.

**A wait that times out is an error, not a failed spec.** A daemon that is behind, stopped or not running says
nothing about the document, so the step errors with a `ProjectionCatchUpTimeoutException` (a `TimeoutException`). Its
message names the wait, the projection shards that were behind (`'WalletSummary:All' at 7 of 12`, when the store can
say), the timeout, and the type and id being read. The store's own exception is the `InnerException`. A store with
no events at all has nothing to be stale, so the wait returns at once there. `AfterAsyncDaemonsCatchUpAsync()` times
out the same way, with no read named (`ReadType` is null), and your own waits outside the vocabulary get the same
diagnosis from `EventStores.DiagnoseCatchUpTimeoutAsync` ([below the vocabulary](#below-the-vocabulary)).

- **`at N of M`: M is the highest event sequence number, not a count of events.** N is how far the shard has got.
  Deleting event data (Marten's `DeleteAllEventDataAsync`, say) does not reset the sequence, so 67 events published
  after a wipe can read as `at 0 of 461`. Compare N with M, not M with the events you appended.
- **"The store could not say which projection was behind (…)"** means reading the shards' progress itself failed,
  and the parentheses carry that exception's type and message (also on `BehindFailure`). A `42P01` "relation … does
  not exist" there usually means another process dropped or rebuilt the schema mid-run (a test sharing the database,
  a parallel lane), not that a projection is slow.

| Step | |
|---|---|
| `ThenAggregate<T>(id, expected?)` | the aggregate as the store serves it through `FetchLatest<T>`: a projected snapshot where there is one, else built from the stream. Throws when there is no such stream |
| `ThenReadModel<T>(id, expected?)` | the read model with this identity, loaded with `LoadAsync` once projections have caught up. Throws when there is none |
| `ThenDocument<T>(id, expected?)` | the same load, for a plain document or a projection stored as one |
| `ThenNoReadModel<T>(id)` / `ThenNoReadModel<T>()` | no such read model: with that id, or at all |
| `ThenSingleReadModel<T>(expected?)` | the `T` of the stream this scenario is about: the one the act started, else the one the givens arranged. Single-stream projections only. With no stream, the one `T` in the store, as below |
| `ThenSingleDocument<T>(expected?)` | the `T` document stored under that stream's id. With no stream (a document app, where the act's document gets its id from the endpoint or a cascaded handler), **the one `T` in the store**: none or several is a failure naming the count |
| `ThenDocuments<T>(params expected)` | **every** `T` document in the store is exactly these, in any order, judged as a set (MISSING and EXTRA rows named); none given means there are none. For several documents under ids the act chose. On a shared store, give them a value only this spec uses |
| `TheDocuments<T>()` | every `T` document in the store, once projections have caught up. A query, not a step |
| `ThenProjectionsAreCaughtUp(Type readModel)` | the projections that build it have caught up |
| `AfterAsyncDaemonsCatchUpAsync(tenantId?)` | wait for every async daemon, on every database or one tenant's: the database the store's `FindDatabaseForTenantAsync` finds for it, else every database. Not a step |
| `ForTenant(tenantId)` | a view whose Givens append, and whose Thens read, as one tenant of a multi-tenanted store, on the database that tenant lives in ([details](#one-tenant-of-a-multi-tenanted-store)). Send the act for the tenant yourself, with `DeliveryOptions { TenantId = … }` |
| `Verify(subject, table)` | selected properties of anything, as a one-row table: `"\| Name \| Address.City \|\n\| Ann \| Austin \|"` |
| `ThenMatches(subject, expected)` | anything, structurally, by the same rules as `ThenEvents` |

`expected` is a partial object, a whole object or an `Expect.Value`, and each of these hands the loaded object back
for further checks.

## Assert: refusals {#assert-refusals}

| Step | |
|---|---|
| `ThenValidationFails(reason)` | the act failed with `reason` |
| `ThenRefusedWith(reason)` | the same check, worded for any refusal |
| `ThenRefusedWith(reason, params mentioning)` | and the refusal names each of these values |
| `ThenRefusedWith<TException>(expected?)` | the act threw a `TException`, matching a partial object when one is given. Hands it back |
| `ThenTheActSucceeded()` | the act ran, threw nothing and was not refused. Write it when no other Then step would notice a failed act: a spec whose only claims are about what is *absent* |
| `ThenCommandRefused()` | a *clean* refusal: the act ran, nothing threw, and it appended nothing. A handler that stops without throwing, or an HTTP 4xx |
| `ThenRefusedWithProblem(expected?)` (`WolverineHttpSpec`) | the last HTTP act answered with a ProblemDetails body matching `Specify<ProblemDetails>()…` |
| `ThenResponseIs(status)` (`WolverineHttpSpec`) | the last HTTP act answered `status` |
| `ThenNotFound()` (`WolverineHttpSpec`) | the last HTTP act answered 404: a read of something that does not exist |

A reason is found in an HTTP refusal's body, or in the exception the act raised **and every inner exception** (each
of an `AggregateException`'s too), so a refusal wrapped by the act's plumbing is still found. The match is a
substring, **case-sensitive** (`StringComparison.Ordinal`). An empty reason is refused, since every refusal contains
one.

**A timed-out act is not a refusal.** When the act throws a `TimeoutException` (most often the tracked session timing
out: `This TrackedSession timed out before all activity completed.`), its When step reads `error` with an
`ActTimedOutException`, `The act timed out: …`, instead of a green When over red Then steps saying "The act failed".
Two common causes: a `Timeout` shorter than the act (the default is 5 s), and the first request to a Wolverine.HTTP
endpoint compiling its chain under dynamic code generation, which can take seconds under load. Raise `Timeout` in
your base spec class, or pre-generate the code.

## Assert: messages {#assert-messages}

| Step | |
|---|---|
| `ThenMessageSent<T>(destination?)` | the act sent or published a `T`, to `destination` when given, matched as a prefix of the URI (`"rabbitmq://queue/billing"`, `"local://"`) |
| `ThenMessageSent(expected)` | a message structurally equal to `expected`, or matching an `Expect.Value` or a partial object |
| `ThenMessageSentLocally<T>()` / `ThenMessageSentExternally<T>()` | to a local queue / to anything but one |
| `ThenMessagesSent<T>(count = 1)` | at least `count` of them |
| `ThenNoMessageSent<T>()` | none was sent |
| `ThenMessageScheduled<T>(delay?)` | scheduled for later, due no sooner than `delay` after the act |
| `ThenMessageScheduled(expected)` | a scheduled message matching `expected`: whole, an `Expect.Value`, or a partial object judged on the members it names |
| `ThenMessagesScheduled(params messages)` | the act scheduled **exactly** these messages, in any order, and nothing else: whole objects, `Expect.Value`s, partial objects, types (any one of that type), or the rows of `PartialObjects.FromTable`. What a timeout-setting handler scheduled, judged as a whole |
| `ThenNoMessageScheduled<T>()` | none was scheduled |
| `ThenHandled<T>()` / `ThenHandled(Type)` | a `T` was handled **successfully** while the act ran: the act's own message, or one it caused. At least one execution completed without an exception |
| `ThenNotHandled<T>()` / `ThenNotHandled(Type)` | no `T` was handled: no execution of it even **started**. A failed attempt still counts as handled, because its handler ran |

A `T` matches its subtypes too.

**Sent and scheduled are two separate sets.** A message scheduled for later is never "sent", even one a transport
records as sent, so `ThenMessageSent` doesn't see it and `TheMessagesSent` doesn't list it; when one of the expected
type was scheduled instead, the failure says so ("one was SCHEDULED for later: use ThenMessageScheduled"), and the
other way round. **The act's own message is never counted as sent**: it is what the act dispatched, not what the
handling produced. It is exempt from the sent count only, so when it is only scheduled it is a scheduled message like
any other, and `ThenNoMessageScheduled<T>()` of its type fails plainly.

**`ThenHandled` is judged whether or not the act failed**, since an act's failure may be exactly the story. When it
fails, it says what happened to the message instead: it ran and every attempt failed, each attempt's exception named
(and whether it was moved to the error queue); it was only scheduled, with the hint to add
`WhenScheduledMessagesAreExecuted()`; Wolverine found no route or handler for it; it was sent elsewhere and no
handler of it executed within the act; or it never appeared in the act's message activity. It then lists every
other exception the act's tracked session captured.

### When a message step fails {#when-a-message-step-fails}

Every failed message step ends with the act's message activity in one line:

```
Activity: 2 sent, 1 scheduled, 1 executed, 0 failed — see the Message activity report
```

`sent` leaves out the act's own message; `scheduled` counts everything scheduled, the act's own message included;
`failed` counts failed attempts. The **Message activity** report it points to is every envelope record of the act's
tracked session, in order, with the session's clock in ms, the event, the message type (the act's own messages
marked `(the act's own)`), destination, attempt and service. A failed execution or a dead-lettered envelope is a red
row carrying the exception's type and message. An *Events appended* report sits beside it.

Like every Bobcat scenario report, these are written out **only when the scenario fails** (`ReportVisibility.OnFailure`),
so a green suite pays nothing for them, and each is capped at 200 rows with "…and N more" rather than a silent cut.
`bobcat run --verbose` (or `BOBCAT_VERBOSE_REPORTS=1`) writes them for passing specifications too. See
[Custom Telemetry on Failure](https://bobcat.jasperfx.net/failure-telemetry).

## Dynamic Consistency Boundaries {#dynamic-consistency-boundaries}

For a decision whose consistency boundary is a tag query rather than one stream:

```csharp
await GivenEvents<SeatAvailability>(theScreeningId.Value,
    Tagged(new ScreeningScheduled(theScreeningId, 10), theScreeningId));

await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));

ThenEvents(Specify<SeatReserved>().With(x => x.Seat, "4C").Tagged(theCustomerId, theScreeningId));
ThenEventsOn(EventTagQuery.For(theCustomerId), Specify<SeatReserved>().With(x => x.Seat, "4C"));
await ThenDcbModel<SeatAvailability>(EventTagQuery.For(theScreeningId),
    Specify<SeatAvailability>().With(x => x.Seats, 9));
```

| Step | |
|---|---|
| `Tagged(event, tags…)` / `.Tagged(tags…)` | an event arranged or expected with exactly these tags, in any order. A tag left out is as wrong as a wrong one |
| `ThenEventsOn(EventTagQuery, params events)` / `ThenNoEventsOn(EventTagQuery)` | exactly these, in order, among the act's events the query selects. Arranged history never counts |
| `TheDcbModel<T>(query)` | `T` projected over the events the query selects; null when nothing matches. Not a step |
| `ThenDcbModel<T>(query, expected)` | and it matches `expected` |

Tag-judging steps need the tags the act appended, which the tracked session sees. **No store reads an event's tags
back**, so after a store-only act a tag-judging step fails and says why, rather than reporting every tagged event
missing.

### The boundary the decision loaded {#the-boundary-the-decision-loaded}

The boundary steps judge what the act's own `[DcbModel]` handler *loaded*: the tag query a concurrent append is
checked against when the boundary is saved. They are deterministic: nothing is appended and nothing races, because
a hypothetical event is matched against the recorded query.

```csharp
await WhenReceived(new ClaimBadge(theConference, theAttendee));

// Exactly the tag query the handler's Load() returned
ThenTheConsistencyBoundaryIs(EventTagQuery.For(theConference).AndEventsOfType<BadgeClaimed, BadgeRevoked>());

// Another badge claimed or revoked for the same conference would have conflicted...
ThenAConcurrentEventWouldConflict<BadgeClaimed>(theConference, new AttendeeId("ATT-99"));
ThenAConcurrentEventWouldConflict(Specify<BadgeRevoked>().Tagged(theConference));

// ...but not one for another conference, nor an event type the decision never read
ThenAConcurrentEventWouldNotConflict<BadgeClaimed>(new ConferenceId(Guid.NewGuid()), theAttendee);
ThenAConcurrentEventWouldNotConflict<SessionProposed>(theConference);
```

| Step | |
|---|---|
| `ThenTheConsistencyBoundaryIs(query)` | the act loaded exactly one boundary, and its query is exactly this: the same conditions, in any order, none missing and none extra |
| `ThenTheConsistencyBoundaryIs<TModel>(query)` | the boundary loaded for `TModel`, for an act that loads several |
| `ThenAConcurrentEventWouldConflict(event)` / `<TEvent>(params tags)` | another writer appending this between the act's read and its write would have made it fail with a concurrency conflict |
| `ThenAConcurrentEventWouldNotConflict(event)` / `<TEvent>(params tags)` | it falls outside every boundary the act loaded |

They're judged whether the act succeeded or was refused, because a refusal is decided over the same boundary.
`LastAct.Boundaries` holds what was loaded, in load order; a cascade's loads are not there.

### A concurrent writer {#a-concurrent-writer}

`WhileActing(params events)` arranges a real concurrent append: while the next act runs, another writer appends these
events between the act's read and its write, on a new stream. Tag them to put them inside or outside a boundary.
Inside, the act's save fails with a `DcbConcurrencyException` (`ThenRefusedWith<DcbConcurrencyException>()`), or an
error policy retries it and `ThenRetried()` passes; outside, the act is not disturbed. It needs the store integrated
with Wolverine (`IntegrateWithWolverine()`). An act that saves nothing never gives the append a moment to land, and
fails saying so.

## What the act eventually caused {#what-the-act-eventually-caused}

The tracked session sees what happens inside the act. `ThenEventuallyCaused` reaches past it, to events appended
across an asynchronous boundary: a message an event subscription published, an async projection's side effect:

```csharp
await WhenReceived(new ShipParcel(theParcel));

await ThenEventuallyCaused(new ParcelShipped(), new LabelPrinted(theParcel));
await ThenEventuallyCaused<LabelPrinted>();
```

Every act runs under a correlation id of its own (`ActCorrelationId`, `LastAct.CorrelationId`). Once the async
daemons have caught up, the step queries the event store for every event recorded with that id, judged as
`ThenEmitted` judges, and keeps looking for up to `EventuallyTimeout` (10 seconds by default) for work that lands
after the daemons.

Three things it needs from you:

- **The store has to record correlation ids**, which Marten, Polecat and Fisher all leave off by default; on Marten,
  `Events.MetadataConfig.CorrelationIdEnabled = true`. When it doesn't, the step fails and says how to turn them on.
- **Every hop has to carry the id.** Wolverine's handlers do. An event subscription or a projection's side effect that
  publishes a message has to pass the event's `CorrelationId` on itself, as in
  `bus.PublishAsync(message, new DeliveryOptions { CorrelationId = e.CorrelationId })`.
- **An HTTP act sends the id as the `X-Correlation-ID` header.** For Wolverine.HTTP to apply it, turn on
  `WolverineHttpOptions.ApplyCorrelationIdHeaderToAllEndpoints`.

An act sent for a tenant is looked for within that tenant: filtered by it on a conjoined store, and in the tenant's
own database on a store that spreads its tenants across several. An act with no tenant on such a store is looked for
in every database. The same mechanism without Bobcat is
[Finding events caused past an asynchronous boundary](/guide/testing#finding-events-caused-past-an-asynchronous-boundary).

## Reading what happened {#reading-what-happened}

None of these is a step: each reads the capture, and the assertion that follows is the step.

| | |
|---|---|
| `LastAct` | what the last act did: `Session` (the tracked session), `NewEvents`, `AllNewEvents`, `ForeignEvents`, `StartedStreams`, `MessagesSent`, `OwnMessages`, `OnlyScheduled`, `Error`, `Refusal`, `Appended` (each committing session with the message it handled), `Boundaries`, `CorrelationId`, `Attempts`, `TenantId` |
| `TheEvent<T>()` | the one `T` the act appended; throws unless exactly one was |
| `TheEvents` | every event the act appended |
| `TheMessagesSent` | every message the act sent or published. A message scheduled for later is not among them |
| `TheAggregate<T>(id)` | the write model, folded from its stream |
| `TheStartedStream<TAggregate>()` / `TheStartedStreamKey<TAggregate>()` | the id the last act assigned to the stream it started |
| `TheStreamVersion(id)` | the stream's version now |
| `LastResponse` (`WolverineHttpSpec`) | the last HTTP act's Alba result |
| `TheResponse<T>()` (`WolverineHttpSpec`) | the last HTTP act's 2xx body, for the acts that don't hand one back |

`LastAct.Session` is Wolverine's `ITrackedSession`, and its own diagnostics are the material for a red spec:
`Status`, `AllExceptions()`, and above all `AllRecordsInOrder()`, which is the causal chain of what was sent, handled,
retried and dead-lettered, in order. That is what an assertion message alone throws away, and what the
[Message activity report](#when-a-message-step-fails) writes out for you on failure. For why that matters to an AI
agent fixing a failing test, see
[Agent Friendly Integration Tests](https://bobcat.jasperfx.net/tutorials/agent-friendly-tests).

## Settings {#settings}

| | Default | |
|---|---|---|
| `ProjectionTimeout` | 20 s | how long a step that reads a projection waits for the async daemons |
| `Timeout` | 5 s | how long an act's tracked session waits for everything it caused to settle, and the default for `WhenScheduledMessagesAreExecuted` |
| `IncludeExternalTransports` | `true` | whether the tracked session also waits on external transports |
| `EventuallyTimeout` | 10 s | how long `ThenEventuallyCaused` keeps looking once the daemons have caught up |
| `UnspecifiedValues` | Bobcat's | what fills the members a partial object leaves unspecified when it is built for a Given or a When |

All of them are on the scenario and on its `*Spec`. A `WolverineScenario` and a `CritterStackScenario` take a
`storeName` naming the default store of an application with several event stores (steps go to the store that owns
their types, and `In<TStore>()` / `In("name")` address one explicitly, see [Several event stores](#several-event-stores)),
and `ResetAsync()` clears every store and forgets the last act.

## The Event Model, from the vocabulary {#the-event-model-from-the-vocabulary}

Every step above that names a command, an aggregate, an event, a read model, a document or a message carries
`[EventModelRole]`, and the HTTP acts carry `[EventModelTrigger(ActTrigger.Http, Route = nameof(route))]`. So a
specification bound to a slice with `[BobcatSpec]` or `[BobcatSlice]` hands Bobcat's generator the slice's whole shape
with nothing authored for the model:

```csharp
[BobcatFeature("Confirming appointments over HTTP")]
public class projected_http_slice(AppHost app) : WolverineHttpSpec(app.Host)
{
    [Fact]
    public async Task a_scheduled_appointment_is_confirmed_over_http()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, "Ann"));

        await WhenPosted(new ConfirmAppointmentRequest(id), "/api/appointments/confirm");

        ThenEvents(typeof(AppointmentConfirmed));
        ThenMessageSent<SendConfirmationEmail>();
    }
}
```

That slice reaches the model as an HTTP slice with its route, its command `ConfirmAppointmentRequest` and the message
it publishes. Negative steps (`ThenNoMessageSent<T>`, `ThenNotEmitted<T>`, …), refusals, waits, `WhenTracked` and
`WhenScheduledMessagesAreExecuted` stamp nothing, and `GivenReceived` stamps only a Document role, so an arranging
message never becomes the act. The DCB model steps stamp an aggregate. See
[Binding, not roles](https://bobcat.jasperfx.net/marker-steps#event-model-roles) for how the generator reads them.

An act with a `GET` or `HEAD` trigger and a `ReadModel` role (`WhenReading`, `WhenReadingRoute`) makes its slice a
**Query**. Wolverine derives a read-only GET chain as a Query slice too, so the two halves agree when the model is
merged (see [Exporting the Event Model](/guide/command-line#exporting-the-event-model)).

### Your own acts {#your-own-acts}

A store-only scenario has no HTTP or message act of its own, and an application often wants its own spelling of one.
Write it on your base spec class, run it through `WhenActing`, and **re-declare the attributes**: the generator reads
them off the method a test calls, never off what that method's body calls. From the
[BankAccountES sample](https://github.com/JasperFx/wolverine/tree/main/src/Samples/Bobcat/BankAccountES):

```cs
[EventModelRole(SliceRole.Command, nameof(command))]
[EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "POST")]
public Task WhenPosted(object command, string route, params object[] routeValues)
    => send("POST", command, route, routeValues);
```

```cs
[EventModelRole(SliceRole.Command, nameof(message))]
[EventModelTrigger(ActTrigger.MessageHandler)]
public Task WhenReceived(object message)
    => WhenActing($"{message.GetType().Name} is received",
        services => services.GetRequiredService<IMessageBus>().InvokeAsync(message));
```

An act can also address an endpoint by its route name: `[WolverinePut("/api/clients/{clientId}", RouteName =
"UpdateClient")]` on the endpoint, and `[EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName))]` on the
act. The generator resolves a constant route name to the endpoint's template at compile time. It also matches an
interpolated route against the endpoints' templates. See
[The act's route](https://bobcat.jasperfx.net/marker-steps#act-route). The route name is stamped as
`TriggerOrigin.HttpRouteName`.

## Checking spec identities against the model {#spec-identity-audit}

Nothing checks a specification's identity (`{Feature}/{Scenario}`) against the design on its own. A projected spec
added under a scenario name the model did not declare produces a green suite, a clean build, no diagnostic, and no
warning, and a check on slice names alone would pass it too, since the spec is bound to a real slice.
`SpecIdentityAudit` (namespace `Bobcat`, in `WolverineFx.Bobcat` because it reads the generated JasperFx descriptor)
is the join between the identities your compiled specs declare and the ones your design-time Event Model declares.

It is not a build diagnostic: the generator holds the compiled identities but not the design model. The spec
assembly's **own test run** is where both can be loaded, so the whole thing is one assertion. Here the design is an
`EventModelDefinition` whose slices name the scenarios they expect with
`LinksToSpecification("{Feature}/{Scenario}")`:

```csharp
[Fact]
public void the_model_and_the_specs_declare_the_same_scenarios()
{
    var builder = new EventModelBuilder();
    new BookingModel().Configure(builder);   // your EventModelDefinition
    var design = builder.Build("Booking");

    var audit = SpecIdentityAudit.Compare(design, typeof(BookingAppointments).Assembly);

    audit.Drifted.ShouldBeFalse(audit.Report());
}
```

It compares `EventModelDescriptor`s, so any design-time source works: a hand-written definition as above, or an
imported eventmodelers.ai board through `Bobcat.EventModel`'s `ImportedModelMapper.ToDescriptor`. Three findings make
`Drifted` true:

- **Orphans**: a test identity the model does not declare. The canvas shows a specification for something nobody
  designed.
- **Holes**: a model scenario no test covers. The design declares behaviour nothing verifies.
- **Misbound**: an identity both sides declare, on *different slices*. The spec points at the wrong behaviour; both
  slice names are real, which is why no slice-name check can see it.

Two things are reported and are deliberately **not** drift. **Pending**: the identity joins, and the specification
is a pending one (`[BobcatSpec(…, Pending = true)]`). **Excused**: identities you named, each with a reason, because
"deliberately not a spec" is a claim someone should have to make out loud:

```csharp
var audit = SpecIdentityAudit.Compare(design, new Dictionary<string, string>
{
    ["BookingAppointments/Counters never go negative"] = "chapter-wide invariant, bound to no slice"
}, typeof(BookingAppointments).Assembly);
```

`Report()` is one line per finding, and a single summary line when there is nothing to say:

```
Spec identities: 41 matched, 1 orphaned, 0 uncovered, 0 misbound.

Orphans — a test declares this identity and the model does not:
  BookingAppointments/An appointment that does not exist is 404  (bound to slice ConfirmAppointment)
```

**Auditing against nothing is refused.** `Compare(design, assembly)` throws when no assembly you passed has a
generated Event Model source. The generator emits one only for an assembly whose specs declare slices, so the usual
causes are naming the wrong assembly or having no `[BobcatSpec]`, `[BobcatTheory]` or `[BobcatSlice]` binding
anywhere. Reporting every declared scenario as uncovered would be a confident lie. How identities are derived is
covered in [Spec Identities](https://bobcat.jasperfx.net/spec-identities).

## Cold hosts and brokers {#cold-hosts-and-brokers}

**Handler warm-up is automatic.** Under Wolverine's default `TypeLoadMode.Dynamic` a handler compiles the first time a
message reaches it, which on a cold host lands *inside* the first act's tracked window. Every act warms the host
first: by default (`HandlerWarmUp.Automatic = AutomaticWarmUp.PrimeCompiler`) it compiles one chain, which pays the
one-time cost of starting the compiler. `AutomaticWarmUp.AllHandlers` compiles every chain, and
`host.WarmUpHandlers()` does that up front, before any specification, so no scenario's timing includes codegen. A
chain that can't compile is reported in one `HandlerWarmUpException`.

Wolverine.HTTP routes are not covered by that. Wolverine owns that switch:
`app.MapWolverineEndpoints(opts => opts.WarmUpRoutes = RouteWarmup.Eager)`.

**A broker is not reset with the store.** Messages a scenario cascaded to a real broker survive a store reset and
arrive inside the *next* scenario, failing it with an id that belongs to one that already passed.
`host.DrainTransportsAsync()` purges every broker queue the application declares; call it wherever your suite
already resets the store. It isn't automatic, because purging a broker someone else is watching is destructive.

An application that embeds CritterWatch has an envelope in flight for the life of the host, which keeps every
tracked session, and so every act, from finishing. See
[Ignoring traffic in every session on a host](/guide/testing#ignoring-traffic-in-every-session-on-a-host).

## Environment and container checks {#container-checks}

When a suite runs under Bobcat's supervisor (`Bobcat.Supervisor`), the environment is checked **once, before any
worker is launched**: not even the discovery worker starts until every check has run. The checks are JasperFx's, not
a Bobcat interface, and the contract is the same everywhere: a check that throws has failed.
`AddContainerChecks` (namespace `Bobcat`, in `WolverineFx.Bobcat`) adds every check the host's container knows about
as one preflight check, by delegating to JasperFx's own `EnvironmentChecker.ExecuteAllEnvironmentChecks`:

```csharp
var supervisor = new Supervisor(new MtpWorkerFactory(path));

// An ad hoc check
supervisor.Preflight.Add("Postgres is reachable", async token =>
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(token);
});

// Everything a container knows about: ISystemPart.AssertEnvironmentAsync,
// IStatefulResource.Check and registered IHealthChecks, through JasperFx's EnvironmentChecker
supervisor.Preflight.AddContainerChecks("the application", host.Services);
```

The container check fails with JasperFx's `EnvironmentCheckException`, whose message lists every failure, and that
message is what the preflight reports. `AddContainerChecks` was a method on `Preflight` itself until Bobcat
1.0.0-rc.2 dropped its JasperFx dependency; it is an extension method from `WolverineFx.Bobcat` now, so a call site
reads exactly as it did, with `using Bobcat;` and a reference to `WolverineFx.Bobcat`.

Every check runs, even after one fails, so one preflight reports everything that is wrong. A failed check aborts the
run: no worker is launched, no test runs, `SupervisorResults.AbortReason` lists each failed check, and the exit code is
**2** (an aborted run), not **1** (test failures). An in-process `dotnet test` run has no preflight, so a missing broker
or database shows up as every test that touches it failing with its own connection error. What the supervisor does
with the rest of the run is in
[Parallel-Ready Suites](https://bobcat.jasperfx.net/parallel-ready-suites).

## Without Wolverine: the store-only vocabulary {#store-only}

`CritterStackScenario` and `CritterStackSpec`, in `Bobcat.CritterStack`, are the store-only Given/When/Then
vocabulary: arrange events and read models, act through your application's own services, and assert what the act
appended, what the aggregates and read models became, and how the act was refused. **No message bus is involved** in
these steps. Plenty of applications use Marten, Polecat or Fisher without Wolverine, and this is the vocabulary for
them, though `WolverineFx.Bobcat` still brings Wolverine with it as a package dependency.

**There is no `Bobcat.Marten` package, and you do not need one.** The vocabulary reaches an event or document store
through the **JasperFx.Events** abstractions, which Marten, Polecat and Fisher all register, so the same specification
runs against any of them and nothing in your test project has to name Marten at all.

```csharp
using Bobcat;
using Bobcat.CritterStack;
using Microsoft.Extensions.DependencyInjection;

[BobcatFeature("Wallets")]
public class crediting_a_wallet(AppFixture app) : CritterStackSpec(app.Host)
{
    [Fact]
    public async Task a_credit_is_recorded()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"));

        await WhenActing("the wallet is credited",
            services => services.GetRequiredService<Wallets>().Credit(id, 10));

        ThenEvents(Specify<WalletCredited>().With(x => x.Amount, 10m));
        await ThenAggregate<Wallet>(id, Specify<Wallet>().With(x => x.Balance, 10m));
    }
}
```

The constructors take an `IHost`, or an `IServiceProvider` for a test that has a container and no host (then `Host`
throws, and every step reads `Services`), plus an optional `storeName` naming the default store of an application with
several. An application with one main store and ancillary ones needs no `storeName`: the main store is the default
([Several event stores](#several-event-stores)). Don't want a base class? `new CritterStackScenario(host)` is the
same vocabulary as an object, and `CritterStackSpec` only delegates to one.

**Your application configures its store however it already does.** The spec project boots it (in an xUnit assembly or
collection fixture, say) and Bobcat finds the store in the container. Changing `Program.cs` from Marten to Fisher is
the only change it takes to run the same specifications against the other store; the
[BankAccountES sample](https://github.com/JasperFx/wolverine/tree/main/src/Samples/Bobcat/BankAccountES) runs its
specifications on both.

Everything on this page that isn't marked as a Wolverine or HTTP act, a message step, a boundary step or
`ThenEventuallyCaused` is `CritterStackSpec`'s:

| | |
|---|---|
| [Arrange](#arrange) | `GivenEvents`, `GivenContext`, `GivenNoEventsFor`, `GivenEventsOn`, `GivenReadModel`, the clock steps |
| [Act](#act) | `WhenActing`, `WhenActingOn` |
| [Events](#assert-events) | `ThenEvents`, `ThenEventsInAnyOrder`, `ThenEmitted`, `ThenNotEmitted`, `ThenNoEvents`, `ThenCaused`, `ThenEventsOn`, `ThenStreamIsStarted`, `ThenStreamIsStartedWithEvents` |
| [Aggregates, documents and read models](#assert-aggregates-documents-and-read-models) | `ThenAggregate`, `ThenDocument`, `ThenReadModel`, `ThenNoReadModel`, `ThenSingleReadModel`, `ThenSingleDocument`, `ThenDocuments`, `TheDocuments`, `Verify`, `ThenMatches` |
| [Refusals](#assert-refusals) | `ThenValidationFails`, `ThenRefusedWith`, `ThenCommandRefused`, `ThenTheActSucceeded` |
| [DCB models](#dynamic-consistency-boundaries) | `ThenEventsOn(EventTagQuery, …)`, `TheDcbModel`, `ThenDcbModel` |

Every step that reads a projection waits for the async daemons first, up to `ProjectionTimeout`: **20 seconds** by
default, which is what a cold daemon on a loaded CI runner needs. A wait that runs out is an error, a
`ProjectionCatchUpTimeoutException` naming the projections that were behind and what was being read, never a failed
spec ([details](#assert-aggregates-documents-and-read-models)).

### The store-only act {#the-store-only-act}

`WhenActing(description, act)` runs any act an application performs (a domain service, an endpoint called
in-process, a background job run by hand), handed a fresh service scope. What it appended is read back from the
store afterwards: the act takes the store's high-water mark before it runs, reads every event appended since, and
keeps the ones on the specification's own streams: those its Givens arranged, `WhenActingOn` addressed, or one of
its acts wrote to or started. The rest are `LastAct.ForeignEvents`. An exception it throws is captured, not rethrown,
so a refusal can be asserted with `ThenValidationFails` or `ThenRefusedWith`.

**An act that times out is the one exception: its own When step goes red.** When what the act threw is, or wraps, a
`TimeoutException`, the When step reads `error` with an `ActTimedOutException` (`The act timed out: …`, the timeout as
its inner exception), so the report starts at the act instead of at the Then steps that found nothing to judge. The
scenario still carries on, `LastAct.Error` still holds what the act threw, and the Then steps still say "The act
failed". Every other exception leaves the When green, because a refusal that throws is exactly what a Then step
asserts. A scenario deriving from `CritterStackScenario` (a tracked session's) gets this for free, since its act sets
`LastAct` with its When step open, and overrides `actTimeoutHint` to add a sentence on what to raise.

Two consequences of reading by sequence, worth designing around:

- **Whole-store assertions on a store-only act need serial execution on a shared store, or a store or tenant per
  test. For real parallelism, use `WolverineScenario`.** Attribution by stream drops what another test appends to
  *its* streams, but a stream another test *starts* while the act runs looks exactly like one the act started, and
  still counts. A whole-store step (`ThenEvents`, `ThenNoEvents`, `ThenStreamIsStartedWithEvents`, …) that fails while
  foreign events were seen says "cross-talk suspected" in its message; a passing one says nothing. The BankAccountES
  sample runs serially, with `[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]`. A
  `WolverineScenario` reads its act from Wolverine's tracked session, which knows exactly what the act committed.
- **No store reads an event's tags back**, so a step that judges Dynamic Consistency Boundary tags after a store-only
  act fails and says why, rather than reporting every tagged event missing.

When an act's events can't be attributed that way at all (several writers, or a stream someone else also appends
to), name the stream and the version instead, with `TheStreamVersion` and `ThenEventsOn<T>(id, afterVersion, …)`, or
`WhenActingOn<T>` for the whole paragraph in one call. See
[A named stream from a named version](#a-named-stream-from-a-named-version).

A Wolverine application usually wants to act through the bus or over HTTP instead. Derive from `WolverineSpec`, or
write your own act on a base spec class the way BankAccountES does; see [Your own acts](#your-own-acts).

## Several event stores {#several-event-stores}

An application with an ancillary store (Marten's `AddMartenStore<IInvoicingStore>()`, Polecat's
`AddPolecatStore<T>()`, Fisher's `AddFisherStore<T>()`) is specified in one scenario, whichever store a step is
about.

<!-- JEREMY -->

**Each step goes to the store that owns its types.** Bobcat reads what each store registers through the same
JasperFx abstractions it reads everything else through: the aggregate and document types of its projections, the
events they apply and the event types it has registered, its DCB tag types (`IEventStore.TryCreateUsage`), and its
document mappings (`IDocumentStoreUsageSource.TryCreateUsage`). `GivenEvents<Invoice>`, `ThenAggregate<Invoice>` and
`ThenDocument<InvoiceSummary>` go to the store that registers `Invoice` and `InvoiceSummary`; a Given with no
aggregate type goes where its events are registered; `TheDcbModel<T>` goes by `T`, then by the query's event and tag
types.

- **One store claims the type:** that store.
- **None, or several:** the default store (the constructor's `storeName`, else the only store, else the one main
  store) when it can take it: it is among the claimants, or nobody claims the type.
- **Otherwise** the step throws a `SpecCriticalException` listing the stores that claim the type and telling you to
  name one. A stream addressed by its id alone (`TheStreamVersion(id)`) has no type, so it is read from the default
  store.

**The main store is the default.** With no `storeName`, a host whose stores include exactly one *main* store (the one
`AddMarten`, `AddPolecat` or `AddFisher` registered, beside any number of `AddMartenStore<T>()`-style ancillary ones)
uses that store as the default, for `EventStore`, for `services.EventStore()` and for every step that falls back to
the default. That is what makes an application that embeds CritterWatch (`opts.AddCritterWatchEmbedded(...)`, which
registers the console's own ancillary store) specifiable unchanged. Bobcat tells a main store from an ancillary one
by its registration, not its name: a main store is the instance its product's `IDocumentStore` service resolves to,
while an ancillary store is registered under its marker interface only. With no main store, or more than one (say
`AddMarten` and `AddFisher` in one host), there is no default: name it with `storeName`, or address each step with
`In<TStore>()` / `In("name")`.

The default store is the scenario's (and `CritterStackSpec`'s) `EventStore` property. It was called `Store` before
Bobcat 1.0, and was renamed because a spec base class with its own `Store` (a Polecat or Marten `IDocumentStore`, say)
hid it with nothing louder than warning CS0108, so the spec read the wrong store.

What a store registers is read once per scenario. Stores register some types lazily (an event type the first time it
is appended, a document type the first time it is stored or loaded), so a type you only ever store by hand is claimed
by nobody until something has stored it.

**Name a store when the type doesn't say.** `In<TStore>()` addresses the store registered under a marker interface,
and `In("name")` the store whose `IEventStore.Identity` name matches, ignoring case (`"main"` for Marten's primary
store; an ancillary store is named after its marker type, `"iinvoicingstore"`). Each returns a view with the same
Given and Then steps, pinned to that store. The view shares the scenario's specification: what it arranges is the
scenario's, an act through it is the scenario's act, and its steps render in the same specification.
`CritterStackSpec` has `In` too, and on a `WolverineSpec` or `WolverineHttpSpec` the view is still a
`WolverineScenario` or `WolverineHttpScenario`, so the message and boundary steps work on it.

```csharp
await GivenEvents<Wallet>(wallet, new WalletOpened(wallet, "Ann"));          // the main store registers Wallet
await GivenEventsOn<Invoice>(invoice, new InvoiceRaised(invoice, 40m));      // IInvoicingStore registers Invoice
await In<IInvoicingStore>().GivenReadModel<SharedNote>(note);                // both register SharedNote

await WhenActing("the invoice is paid from the wallet", services => /* ... */);

ThenEventsInAnyOrder(new WalletDebited(wallet, 40m), new InvoicePaid(invoice));
await ThenAggregate<Invoice>(invoice, Specify<Invoice>().With(x => x.Paid, true));
```

**An act is observed on every store.** The act takes a high-water mark on each store, reads back what each one
gained, and attributes each store's events to the specification as a single-store act does. `LastAct` lists them store
by store (sequences belong to a store, so there is no order across stores), and `LastAct.StoreOf(e)` says which store
an event came from. A failing whole-store step adds an "Appended by store -- 'main': …; 'iinvoicingstore': …" line.
In a `WolverineScenario`, a store the tracked session heard commit is read from the session; only a store it heard
nothing from is read back from its high-water mark.

**Waits cover the stores the specification involves.** `AfterAsyncDaemonsCatchUpAsync` waits on the default store and
on every store the specification touched (a step went to it, a view was pinned to it, or an act appended the
specification's events there), so a store it never involves, such as CritterWatch's, whose daemon may never catch up
in a test host, cannot hold it up or time it out. With no default store it waits on every store. A view waits on its
own store, and a read waits on the store it reads.

A host with one store behaves exactly as before. Nothing is read about what it registers.

## One tenant of a multi-tenanted store {#one-tenant-of-a-multi-tenanted-store}

On a store with conjoined tenancy, or one that spreads its tenants over several databases, the Givens and Thens use
the store's default session unless told otherwise: the arranged events land in the default tenant, and
`ThenAggregate` reads a stream the tenant's act never wrote. `ForTenant("id")` is a view that arranges and reads as
one tenant:

```csharp
await ForTenant("58123462").GivenEvents<ProvidedCare>(id, imported);
await WhenReceived(new IgnoreInflux(id), options: new DeliveryOptions { TenantId = "58123462" });
ThenEvents(new InfluxIgnored(userId));
await ForTenant("58123462").ThenAggregate<ProvidedCare>(id, Specify<ProvidedCare>().With(x => x.Ignored, true));
```

- **What the view covers:** every Given (`GivenEvents`, `GivenContext`, `GivenEventsOn`, `GivenReadModel`) appends or
  stores as the tenant, and every Then that reads the store (`ThenAggregate`, `TheAggregate`, `ThenEventsOn`,
  `ThenStreamIsStarted`, `ThenReadModel`/`ThenDocument`, `ThenNoReadModel`, `ThenSingleReadModel`, `TheDcbModel`)
  reads as the tenant. A read's projection wait, and the view's `AfterAsyncDaemonsCatchUpAsync()`, wait on the
  tenant's database. Its steps render with `for tenant "58123462"` on the end.
- **Which database:** the store's own answer (`IEventStore.FindDatabaseForTenantAsync`), else Marten's
  `Storage.FindOrCreateDatabase(tenantId)`, else the store's only database. A store with several databases that can
  name none of them for the tenant is refused with a message naming the tenant, never guessed at.
- **The act is not the view's.** An act through the view is the specification's act, so send it for the tenant the
  way the application takes one (`DeliveryOptions { TenantId = … }`, a claim on the request). `ThenEvents` judges what
  the act saw. A store-only `WhenActing` reads its events back from the default session, so on a conjoined store judge
  a tenant's act with the view's `ThenEventsOn` or `ThenAggregate`.
- **Views compose.** `In<IInvoicingStore>().ForTenant("red")` and `ForTenant("red").In<IInvoicingStore>()` are the
  same view: one store, one tenant. `TenantId` says which tenant a view is for. `CritterStackSpec` has `ForTenant` too.

For a step of your own: `EventStores.FetchStreamAsync`, `AggregateStreamAsync<T>`, `FetchLatestAsync<T>` and
`AggregateByTagsAsync<T>`, and `DocumentStores.StoreAllAsync`, each have an overload taking a `tenantId` (null is the
default session), and `EventStores.OpenTenantSessionAsync(store, tenantId)` /
`DocumentStores.TenantSessionAsync(store, tenantId)` open the tenant's session on its database, to append or load
through.

## Below the vocabulary {#below-the-vocabulary}

The store helpers the vocabulary is built from are public, in `Bobcat.CritterStack`, and all of them are
store-agnostic:

| | |
|---|---|
| `services.EventStore(storeName?)` / `services.EventStores()` | the registered store: the only one, the one named, or the one main store among several |
| `services.MainEventStores()` / `services.IsMainEventStore(store)` | the stores registered as an application's main store (`AddMarten`, `AddPolecat`, `AddFisher`), not under a marker interface |
| `host.ResetEventStoresAsync()` | empty **every** store the host registers: documents *and* event streams. The snapshots are documents, the events that produced them are not, and deleting one half leaves the other. Every async daemon is paused around the delete and resumed after it, as Marten's own `ResetAllMartenDataAsync` does, so a running daemon never sees its store emptied under it |
| `host.ClearStatefulResourcesAsync()` | clear every JasperFx `IStatefulResource` the host registers, such as Wolverine's envelope storage |
| `services.ProjectionCoordinators()` | every projection coordinator the host runs, an ancillary store's included: what the reset pauses |
| `host.WaitForNonStaleProjectionsAsync(…)` | wait for the async daemon rather than sleeping |
| `EventStores.FetchStreamAsync` / `AggregateStreamAsync<T>` / `FetchLatestAsync<T>` | a stream's events, the aggregate rebuilt from them, the latest projected snapshot |
| `EventStores.QueryEventsSinceAsync` / `HighWaterSequenceAsync` | the store's tail |
| `EventStores.ProjectionProgressAsync` | per-shard progress, for when a wait times out |
| `EventStores.DiagnoseCatchUpTimeoutAsync(store, wait, timeout, e, readType?, id?)` | turn your own wait's `TimeoutException` (`WaitForNonStaleProjectionDataAsync`, a daemon's `WaitForNonStaleData`, a shard-state wait) into the `ProjectionCatchUpTimeoutException` the vocabulary throws, naming the shards that were behind: `catch (TimeoutException e) { throw await EventStores.DiagnoseCatchUpTimeoutAsync(store, "the daemon has caught up", timeout, e); }` |
| `DocumentStores.LoadAsync` / `StoreAllAsync` | documents by type |
| `EventStoreAuthoring.AppendAsync` | append arranged history |

In `'shard' at N of M`, M is the highest event sequence number, which `DeleteAllEventDataAsync` does not reset, so it
can be far above the events the spec appended.

### Why there is no Bobcat.Marten package {#why-there-is-no-package}

<!-- JEREMY -->
`Bobcat.Marten` existed and was deleted, so that support could be rebuilt from what applications turn out to need
rather than from what an abstraction seemed like it should offer. It never came back. Seven of nine sample suites had
hand-written a Marten-specific reset (`DeleteAllDocumentsAsync` plus `DeleteAllEventDataAsync`) that Bobcat already
shipped store-agnostically. Swapping it for `host.ResetEventStoresAsync()` left every sample scenario passing and took
`using Marten` out of every spec project.

## See also {#see-also}

- [Bobcat](https://bobcat.jasperfx.net): writing specifications, running them, and the `bobcat` tool
- [Writing Specifications](https://bobcat.jasperfx.net/marker-steps): step helpers, `SpecAssert`, tables, and slice binding
- [Test Automation Support](/guide/testing): Wolverine's tracked sessions, which every act here runs through
- [Event Modeling with the Critter Stack](/tutorials/event-modeling): from a board to generated specifications
