using JasperFx.Events;
using JasperFx.Events.Tags;
using Microsoft.Extensions.Hosting;

namespace Bobcat.CritterStack;

/// <summary>
/// The base class for specifying an event-sourced application on Marten, Polecat or Fisher in an ordinary
/// xUnit or TUnit test, with the whole store-only Given/When/Then vocabulary of
/// <see cref="CritterStackScenario" /> as its own members (bobcat#471). No message bus needed.
/// </summary>
/// <remarks>
/// <para>
/// Put Bobcat's <c>[BobcatFeature]</c> on the class (Bobcat.Xunit records every test in a feature class)
/// and every step renders as a specification; without a recording the same test runs as an ordinary
/// test and a wrong is thrown.
/// </para>
/// <code>
/// [BobcatFeature("Wallets")]
/// public class crediting_a_wallet(AppFixture app) : CritterStackSpec(app.Host)
/// {
///     [Fact]
///     public async Task a_credit_is_recorded()
///     {
///         var id = Guid.NewGuid();
///         await GivenEvents&lt;Wallet&gt;(id, new WalletOpened(id, "Ann"));
///         await WhenActing("the wallet is credited", s =&gt; s.GetRequiredService&lt;Wallets&gt;().Credit(id, 10));
///         ThenEvents(new WalletCredited(id, 10));
///     }
/// }
/// </code>
/// <para>
/// Every member is public, not protected, so a test's own helpers in another class can drive it too.
/// WolverineFx.Bobcat's <c>WolverineSpec</c> derives from this class.
/// </para>
/// </remarks>
public abstract class CritterStackSpec
{
    protected CritterStackSpec(IHost host, string? storeName = null) : this(new CritterStackScenario(host, storeName))
    {
    }

    /// <summary>A spec over an application's services alone, with no <see cref="IHost" />; <see cref="Host" /> throws.</summary>
    protected CritterStackSpec(IServiceProvider services, string? storeName = null)
        : this(new CritterStackScenario(services, storeName))
    {
    }

    protected CritterStackSpec(CritterStackScenario scenario)
    {
        Scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
    }

    /// <summary>The scenario every step delegates to.</summary>
    public CritterStackScenario Scenario { get; }

    /// <inheritdoc cref="CritterStackScenario.Host" />
    public IHost Host => Scenario.Host;

    /// <inheritdoc cref="CritterStackScenario.Services" />
    public IServiceProvider Services => Scenario.Services;

    /// <inheritdoc cref="CritterStackScenario.EventStore" />
    public IEventStore EventStore => Scenario.EventStore;

    /// <inheritdoc cref="CritterStackScenario.UnspecifiedValues" />
    public IUnspecifiedValues? UnspecifiedValues
    {
        get => Scenario.UnspecifiedValues;
        set => Scenario.UnspecifiedValues = value;
    }

    /// <inheritdoc cref="CritterStackScenario.ProjectionTimeout" />
    public TimeSpan ProjectionTimeout
    {
        get => Scenario.ProjectionTimeout;
        set => Scenario.ProjectionTimeout = value;
    }

    /// <summary>
    /// A partial object: only the members a specification is about —
    /// <c>Specify&lt;ShipmentConfirmed&gt;().With(x =&gt; x.TrackingNumber, "1Z999")</c>. Hand one to a
    /// Given or a When to build it, or to a Then to judge only those members.
    /// </summary>
    public static Specified<T> Specify<T>() => Specifications.Specify<T>();

    /// <summary>
    /// A partial <typeparamref name="T" /> written as a table (bobcat#450): <c>Property | Value</c> rows, or
    /// the members as headers over a single row — what the import writes for more than three members.
    /// </summary>
    public static Specified<T> Specify<T>(StepTable table) => Specifications.Specify<T>(table);

    /// <summary>
    /// A partial <typeparamref name="T" /> to check against, written as assertions on its members (bobcat#450):
    /// <c>Specify&lt;Dog&gt;(x =&gt; x.Age.ShouldBe(4), x =&gt; x.Name.ShouldStartWith("Re"))</c>.
    /// </summary>
    public static Specified<T> Specify<T>(params System.Linq.Expressions.Expression<Action<T>>[] checks)
        => Specifications.Specify(checks);

    /// <summary>
    /// A whole event expected with exactly these Dynamic Consistency Boundary tags, in any order
    /// (wolverine#4865): <c>ThenEvents(Tagged(new SeatReserved(theScreeningId, "4C"), theScreeningId, theCustomerId))</c>.
    /// A partial one says its tags itself: <c>Specify&lt;SeatReserved&gt;().Tagged(theScreeningId)</c>.
    /// </summary>
    public static TaggedValue Tagged(object value, params object[] tags) => Specifications.Tagged(value, tags);

    /// <inheritdoc cref="CritterStackScenario.In{TStore}" />
    public CritterStackScenario In<TStore>() where TStore : class => Scenario.In<TStore>();

    /// <inheritdoc cref="CritterStackScenario.In(string)" />
    public CritterStackScenario In(string storeName) => Scenario.In(storeName);

    /// <inheritdoc cref="CritterStackScenario.ForTenant" />
    public CritterStackScenario ForTenant(string tenantId) => Scenario.ForTenant(tenantId);

    /// <inheritdoc cref="CritterStackScenario.LastAct" />
    public CritterStackActOutcome LastAct => Scenario.LastAct;

    /// <inheritdoc cref="CritterStackScenario.ResetAsync" />
    public Task ResetAsync(CancellationToken token = default) => Scenario.ResetAsync(token);

    // ---- arrange ---------------------------------------------------------------------------

    /// <inheritdoc cref="CritterStackScenario.GivenEvents{TAggregate}(Guid, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => Scenario.GivenEvents<TAggregate>(id, events);

    /// <inheritdoc cref="CritterStackScenario.GivenEvents{TAggregate}(string, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents<TAggregate>(string key, params object[] events) where TAggregate : class
        => Scenario.GivenEvents<TAggregate>(key, events);

    /// <inheritdoc cref="CritterStackScenario.GivenEvents{TAggregate}(object, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents<TAggregate>(object id, params object[] events) where TAggregate : class
        => Scenario.GivenEvents<TAggregate>(id, events);

    /// <inheritdoc cref="CritterStackScenario.GivenEvents(Type, object, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(aggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents(Type? aggregate, object id, params object[] events) => Scenario.GivenEvents(aggregate, id, events);

    /// <inheritdoc cref="CritterStackScenario.GivenEvents(Guid, object[])" />
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents(Guid id, params object[] events) => Scenario.GivenEvents(id, events);

    /// <inheritdoc cref="CritterStackScenario.GivenEvents(string, object[])" />
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents(string key, params object[] events) => Scenario.GivenEvents(key, events);

    /// <inheritdoc cref="CritterStackScenario.GivenContext{TContext}(Guid, object[])" />
    [EventModelRole(SliceRole.Context, nameof(TContext))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenContext<TContext>(Guid id, params object[] events) where TContext : class
        => Scenario.GivenContext<TContext>(id, events);

    /// <inheritdoc cref="CritterStackScenario.GivenContext{TContext}(string, object[])" />
    [EventModelRole(SliceRole.Context, nameof(TContext))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenContext<TContext>(string key, params object[] events) where TContext : class
        => Scenario.GivenContext<TContext>(key, events);

    /// <inheritdoc cref="CritterStackScenario.GivenContext{TContext}(object, object[])" />
    [EventModelRole(SliceRole.Context, nameof(TContext))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenContext<TContext>(object id, params object[] events) where TContext : class
        => Scenario.GivenContext<TContext>(id, events);

    /// <inheritdoc cref="CritterStackScenario.GivenContext(Type, object, object[])" />
    [EventModelRole(SliceRole.Context, nameof(context))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenContext(Type context, object id, params object[] events) => Scenario.GivenContext(context, id, events);

    /// <inheritdoc cref="CritterStackScenario.GivenNoEventsFor{TAggregate}(Guid)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task GivenNoEventsFor<TAggregate>(Guid id) where TAggregate : class => Scenario.GivenNoEventsFor<TAggregate>(id);

    /// <inheritdoc cref="CritterStackScenario.GivenNoEventsFor{TAggregate}(string)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task GivenNoEventsFor<TAggregate>(string key) where TAggregate : class => Scenario.GivenNoEventsFor<TAggregate>(key);

    /// <inheritdoc cref="CritterStackScenario.GivenNoEventsFor{TAggregate}(object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task GivenNoEventsFor<TAggregate>(object id) where TAggregate : class => Scenario.GivenNoEventsFor<TAggregate>(id);

    /// <inheritdoc cref="CritterStackScenario.GivenReadModel{T}(object)" />
    [EventModelRole(SliceRole.ArrangedReadModel, nameof(T))]
    public Task GivenReadModel<T>(object document) where T : class => Scenario.GivenReadModel<T>(document);

    /// <inheritdoc cref="CritterStackScenario.GivenReadModel{T}(T)" />
    [EventModelRole(SliceRole.ArrangedReadModel, nameof(T))]
    public Task GivenReadModel<T>(T document) where T : class => Scenario.GivenReadModel<T>(document);

    /// <inheritdoc cref="CritterStackScenario.GivenReadModel{T}(Specified{T})" />
    [EventModelRole(SliceRole.ArrangedReadModel, nameof(T))]
    public Task GivenReadModel<T>(Specified<T> document) where T : class => Scenario.GivenReadModel<T>(document);

    /// <inheritdoc cref="CritterStackScenario.GivenEventsOn{TAggregate}(Guid, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEventsOn<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => Scenario.GivenEventsOn<TAggregate>(id, events);

    /// <inheritdoc cref="CritterStackScenario.GivenEventsOn{TAggregate}(string, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEventsOn<TAggregate>(string key, params object[] events) where TAggregate : class
        => Scenario.GivenEventsOn<TAggregate>(key, events);

    /// <inheritdoc cref="CritterStackScenario.GivenEventsOn{TAggregate}(object, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEventsOn<TAggregate>(object id, params object[] events) where TAggregate : class
        => Scenario.GivenEventsOn<TAggregate>(id, events);

    // ---- act -------------------------------------------------------------------------------

    /// <inheritdoc cref="CritterStackScenario.WhenActing" />
    [EventModelAct]
    public Task WhenActing(string description, Func<IServiceProvider, Task> act) => Scenario.WhenActing(description, act);

    /// <inheritdoc cref="CritterStackScenario.WhenActingOn{T}(Guid, Func{IServiceProvider, Task}, object[])" />
    [EventModelAct]
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task WhenActingOn<T>(Guid id, Func<IServiceProvider, Task> act, params object[] events) where T : class
        => Scenario.WhenActingOn<T>(id, act, events);

    /// <inheritdoc cref="CritterStackScenario.WhenActingOn{T}(string, Func{IServiceProvider, Task}, object[])" />
    [EventModelAct]
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task WhenActingOn<T>(string key, Func<IServiceProvider, Task> act, params object[] events) where T : class
        => Scenario.WhenActingOn<T>(key, act, events);

    /// <inheritdoc cref="CritterStackScenario.WhenActingOn{T}(Guid, string, Func{IServiceProvider, Task}, object[])" />
    [EventModelAct]
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task WhenActingOn<T>(Guid id, string description, Func<IServiceProvider, Task> act, params object[] events)
        where T : class
        => Scenario.WhenActingOn<T>(id, description, act, events);

    /// <inheritdoc cref="CritterStackScenario.WhenActingOn{T}(string, string, Func{IServiceProvider, Task}, object[])" />
    [EventModelAct]
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task WhenActingOn<T>(string key, string description, Func<IServiceProvider, Task> act, params object[] events)
        where T : class
        => Scenario.WhenActingOn<T>(key, description, act, events);

    // ---- the clock -------------------------------------------------------------------------

    /// <inheritdoc cref="CritterStackScenario.Clock" />
    public TimeProvider Clock => Scenario.Clock;

    /// <inheritdoc cref="CritterStackScenario.GivenTheDateIs" />
    public void GivenTheDateIs(DateOnly date) => Scenario.GivenTheDateIs(date);

    /// <inheritdoc cref="CritterStackScenario.GivenTheTimeIs" />
    public void GivenTheTimeIs(DateTimeOffset now) => Scenario.GivenTheTimeIs(now);

    /// <inheritdoc cref="CritterStackScenario.WhenTheClockAdvancesBy" />
    public void WhenTheClockAdvancesBy(TimeSpan duration) => Scenario.WhenTheClockAdvancesBy(duration);

    /// <inheritdoc cref="CritterStackScenario.WhenTimePasses" />
    public void WhenTimePasses(TimeSpan duration) => Scenario.WhenTimePasses(duration);

    /// <inheritdoc cref="CritterStackScenario.WhenTheClockAdvancesTo" />
    public void WhenTheClockAdvancesTo(DateTimeOffset instant) => Scenario.WhenTheClockAdvancesTo(instant);

    // ---- assert: events --------------------------------------------------------------------

    /// <inheritdoc cref="CritterStackScenario.ThenEvents(Type[])" />
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEvents(params Type[] events) => Scenario.ThenEvents(events);

    /// <inheritdoc cref="CritterStackScenario.ThenEvents(object[])" />
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEvents(params object[] events) => Scenario.ThenEvents(events);

    /// <inheritdoc cref="CritterStackScenario.ThenEventsInAnyOrder(object[])" />
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsInAnyOrder(params object[] events) => Scenario.ThenEventsInAnyOrder(events);

    /// <inheritdoc cref="CritterStackScenario.ThenEventsInAnyOrder(Type[])" />
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsInAnyOrder(params Type[] events) => Scenario.ThenEventsInAnyOrder(events);

    /// <inheritdoc cref="CritterStackScenario.ThenEmitted(object[])" />
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEmitted(params object[] events) => Scenario.ThenEmitted(events);

    /// <inheritdoc cref="CritterStackScenario.ThenEmitted{T}" />
    [EventModelRole(SliceRole.Event, nameof(T))]
    public void ThenEmitted<T>() => Scenario.ThenEmitted<T>();

    /// <inheritdoc cref="CritterStackScenario.ThenNotEmitted{T}" />
    public void ThenNotEmitted<T>() => Scenario.ThenNotEmitted<T>();

    /// <inheritdoc cref="CritterStackScenario.ThenNotEmitted(object[])" />
    public void ThenNotEmitted(params object[] events) => Scenario.ThenNotEmitted(events);

    /// <inheritdoc cref="CritterStackScenario.ThenCaused(object[])" />
    public void ThenCaused(params object[] events) => Scenario.ThenCaused(events);

    /// <inheritdoc cref="CritterStackScenario.ThenCaused{T}" />
    public void ThenCaused<T>() => Scenario.ThenCaused<T>();

    /// <inheritdoc cref="CritterStackScenario.ThenNoEvents" />
    public void ThenNoEvents() => Scenario.ThenNoEvents();

    /// <inheritdoc cref="CritterStackScenario.TheEvent{T}" />
    [EventModelRole(SliceRole.Event, nameof(T))]
    public T TheEvent<T>() => Scenario.TheEvent<T>();

    /// <inheritdoc cref="CritterStackScenario.TheEvents" />
    public IReadOnlyList<object> TheEvents => Scenario.TheEvents;

    /// <inheritdoc cref="CritterStackScenario.ThenEventsOn{TAggregate}(Guid, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsOn<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => Scenario.ThenEventsOn<TAggregate>(id, events);

    /// <inheritdoc cref="CritterStackScenario.ThenEventsOn{TAggregate}(string, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsOn<TAggregate>(string key, params object[] events) where TAggregate : class
        => Scenario.ThenEventsOn<TAggregate>(key, events);

    /// <inheritdoc cref="CritterStackScenario.ThenEventsOn{TAggregate}(object, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsOn<TAggregate>(object id, params object[] events) where TAggregate : class
        => Scenario.ThenEventsOn<TAggregate>(id, events);

    /// <inheritdoc cref="CritterStackScenario.ThenNoEventsOn{TAggregate}(Guid)" />
    public void ThenNoEventsOn<TAggregate>(Guid id) where TAggregate : class => Scenario.ThenNoEventsOn<TAggregate>(id);

    /// <inheritdoc cref="CritterStackScenario.ThenNoEventsOn{TAggregate}(string)" />
    public void ThenNoEventsOn<TAggregate>(string key) where TAggregate : class => Scenario.ThenNoEventsOn<TAggregate>(key);

    /// <inheritdoc cref="CritterStackScenario.ThenNoEventsOn{TAggregate}(object)" />
    public void ThenNoEventsOn<TAggregate>(object id) where TAggregate : class => Scenario.ThenNoEventsOn<TAggregate>(id);

    /// <inheritdoc cref="CritterStackScenario.ThenEventsOn(EventTagQuery, object[])" />
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsOn(EventTagQuery query, params object[] events) => Scenario.ThenEventsOn(query, events);

    /// <inheritdoc cref="CritterStackScenario.ThenNoEventsOn(EventTagQuery)" />
    public void ThenNoEventsOn(EventTagQuery query) => Scenario.ThenNoEventsOn(query);

    /// <inheritdoc cref="CritterStackScenario.TheStreamVersion(Guid)" />
    public Task<long> TheStreamVersion(Guid id) => Scenario.TheStreamVersion(id);

    /// <inheritdoc cref="CritterStackScenario.TheStreamVersion(string)" />
    public Task<long> TheStreamVersion(string key) => Scenario.TheStreamVersion(key);

    /// <inheritdoc cref="CritterStackScenario.ThenEventsOn{T}(Guid, long, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task ThenEventsOn<T>(Guid id, long afterVersion, params object[] events) where T : class
        => Scenario.ThenEventsOn<T>(id, afterVersion, events);

    /// <inheritdoc cref="CritterStackScenario.ThenEventsOn{T}(string, long, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task ThenEventsOn<T>(string key, long afterVersion, params object[] events) where T : class
        => Scenario.ThenEventsOn<T>(key, afterVersion, events);

    /// <inheritdoc cref="CritterStackScenario.ThenNoEventsOn{T}(Guid, long)" />
    public Task ThenNoEventsOn<T>(Guid id, long afterVersion) where T : class => Scenario.ThenNoEventsOn<T>(id, afterVersion);

    /// <inheritdoc cref="CritterStackScenario.ThenNoEventsOn{T}(string, long)" />
    public Task ThenNoEventsOn<T>(string key, long afterVersion) where T : class => Scenario.ThenNoEventsOn<T>(key, afterVersion);

    /// <inheritdoc cref="CritterStackScenario.ThenStreamIsStarted{TAggregate}(Guid)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task ThenStreamIsStarted<TAggregate>(Guid id) where TAggregate : class => Scenario.ThenStreamIsStarted<TAggregate>(id);

    /// <inheritdoc cref="CritterStackScenario.ThenStreamIsStarted{TAggregate}(string)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task ThenStreamIsStarted<TAggregate>(string key) where TAggregate : class => Scenario.ThenStreamIsStarted<TAggregate>(key);

    /// <inheritdoc cref="CritterStackScenario.ThenStreamIsStarted{TAggregate}(object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task ThenStreamIsStarted<TAggregate>(object id) where TAggregate : class => Scenario.ThenStreamIsStarted<TAggregate>(id);

    /// <inheritdoc cref="CritterStackScenario.ThenStreamIsStartedWithEvents{TAggregate}(object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task ThenStreamIsStartedWithEvents<TAggregate>(params object[] events) where TAggregate : class
        => Scenario.ThenStreamIsStartedWithEvents<TAggregate>(events);

    /// <inheritdoc cref="CritterStackScenario.TheStartedStream{TAggregate}()" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Guid TheStartedStream<TAggregate>() where TAggregate : class => Scenario.TheStartedStream<TAggregate>();

    /// <inheritdoc cref="CritterStackScenario.TheStartedStreamKey{TAggregate}()" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public string TheStartedStreamKey<TAggregate>() where TAggregate : class => Scenario.TheStartedStreamKey<TAggregate>();

    // ---- assert: refusals ------------------------------------------------------------------

    /// <inheritdoc cref="CritterStackScenario.ThenRefusedWith(string)" />
    public void ThenRefusedWith(string reason) => Scenario.ThenRefusedWith(reason);

    /// <inheritdoc cref="CritterStackScenario.ThenRefusedWith(string, object[])" />
    public void ThenRefusedWith(string reason, params object[] mentioning) => Scenario.ThenRefusedWith(reason, mentioning);

    /// <inheritdoc cref="CritterStackScenario.ThenRefusedWith{TException}(object?)" />
    public TException? ThenRefusedWith<TException>(object? expected = null) where TException : Exception
        => Scenario.ThenRefusedWith<TException>(expected);

    /// <inheritdoc cref="CritterStackScenario.ThenValidationFails" />
    public void ThenValidationFails(string reason) => Scenario.ThenValidationFails(reason);

    /// <inheritdoc cref="CritterStackScenario.ThenCommandRefused" />
    public void ThenCommandRefused() => Scenario.ThenCommandRefused();

    /// <inheritdoc cref="CritterStackScenario.ThenTheActSucceeded" />
    public void ThenTheActSucceeded() => Scenario.ThenTheActSucceeded();

    /// <inheritdoc cref="CritterStackScenario.ThenMessagesSent{T}" />
    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessagesSent<T>(int count = 1) => Scenario.ThenMessagesSent<T>(count);

    // ---- assert: aggregates, documents and read models ------------------------------------

    /// <inheritdoc cref="CritterStackScenario.TheAggregate{T}(Guid)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T?> TheAggregate<T>(Guid id) where T : class => Scenario.TheAggregate<T>(id);

    /// <inheritdoc cref="CritterStackScenario.TheAggregate{T}(string)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T?> TheAggregate<T>(string key) where T : class => Scenario.TheAggregate<T>(key);

    /// <inheritdoc cref="CritterStackScenario.TheAggregate{T}(object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T?> TheAggregate<T>(object id) where T : class => Scenario.TheAggregate<T>(id);

    /// <inheritdoc cref="CritterStackScenario.ThenAggregate{T}(Guid)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(Guid id) where T : class => Scenario.ThenAggregate<T>(id);

    /// <inheritdoc cref="CritterStackScenario.ThenAggregate{T}(string)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(string key) where T : class => Scenario.ThenAggregate<T>(key);

    /// <inheritdoc cref="CritterStackScenario.ThenAggregate{T}(object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(object id) where T : class => Scenario.ThenAggregate<T>(id);

    /// <inheritdoc cref="CritterStackScenario.ThenAggregate{T}(Guid, object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(Guid id, object expected) where T : class => Scenario.ThenAggregate<T>(id, expected);

    /// <inheritdoc cref="CritterStackScenario.ThenAggregate{T}(string, object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(string key, object expected) where T : class => Scenario.ThenAggregate<T>(key, expected);

    /// <inheritdoc cref="CritterStackScenario.ThenAggregate{T}(object, object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(object id, object expected) where T : class => Scenario.ThenAggregate<T>(id, expected);

    /// <inheritdoc cref="CritterStackScenario.ThenDocument{T}(object)" />
    [EventModelRole(SliceRole.Document, nameof(T))]
    public Task<T> ThenDocument<T>(object id) where T : class => Scenario.ThenDocument<T>(id);

    /// <inheritdoc cref="CritterStackScenario.ThenDocument{T}(object, object)" />
    [EventModelRole(SliceRole.Document, nameof(T))]
    public Task<T> ThenDocument<T>(object id, object expected) where T : class => Scenario.ThenDocument<T>(id, expected);

    /// <inheritdoc cref="CritterStackScenario.ThenReadModel{T}(object)" />
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    public Task<T> ThenReadModel<T>(object id) where T : class => Scenario.ThenReadModel<T>(id);

    /// <inheritdoc cref="CritterStackScenario.ThenReadModel{T}(object, object)" />
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    public Task<T> ThenReadModel<T>(object id, object expected) where T : class => Scenario.ThenReadModel<T>(id, expected);

    /// <inheritdoc cref="CritterStackScenario.ThenNoReadModel{T}(object)" />
    public Task ThenNoReadModel<T>(object id) where T : class => Scenario.ThenNoReadModel<T>(id);

    /// <inheritdoc cref="CritterStackScenario.ThenNoReadModel{T}()" />
    public Task ThenNoReadModel<T>() where T : class => Scenario.ThenNoReadModel<T>();

    /// <inheritdoc cref="CritterStackScenario.ThenSingleReadModel{T}(object?)" />
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    public Task<T> ThenSingleReadModel<T>(object? expected = null) where T : class => Scenario.ThenSingleReadModel<T>(expected);

    /// <inheritdoc cref="CritterStackScenario.ThenSingleDocument{T}(object?)" />
    [EventModelRole(SliceRole.Document, nameof(T))]
    public Task<T> ThenSingleDocument<T>(object? expected = null) where T : class => Scenario.ThenSingleDocument<T>(expected);

    /// <inheritdoc cref="CritterStackScenario.TheDocuments{T}" />
    public Task<IReadOnlyList<T>> TheDocuments<T>() where T : class => Scenario.TheDocuments<T>();

    /// <inheritdoc cref="CritterStackScenario.ThenDocuments{T}" />
    [EventModelRole(SliceRole.Document, nameof(T))]
    public Task<IReadOnlyList<T>> ThenDocuments<T>(params object[] expected) where T : class => Scenario.ThenDocuments<T>(expected);

    /// <inheritdoc cref="CritterStackScenario.ThenProjectionsAreCaughtUp" />
    [EventModelRole(SliceRole.ReadModel, nameof(readModel))]
    public Task ThenProjectionsAreCaughtUp(Type readModel) => Scenario.ThenProjectionsAreCaughtUp(readModel);

    /// <inheritdoc cref="CritterStackScenario.AfterAsyncDaemonsCatchUpAsync()" />
    public Task AfterAsyncDaemonsCatchUpAsync() => Scenario.AfterAsyncDaemonsCatchUpAsync();

    /// <inheritdoc cref="CritterStackScenario.AfterAsyncDaemonsCatchUpAsync(string)" />
    public Task AfterAsyncDaemonsCatchUpAsync(string tenantId) => Scenario.AfterAsyncDaemonsCatchUpAsync(tenantId);

    /// <inheritdoc cref="CritterStackScenario.TheDcbModel{T}(EventTagQuery)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T?> TheDcbModel<T>(EventTagQuery query) where T : class => Scenario.TheDcbModel<T>(query);

    /// <inheritdoc cref="CritterStackScenario.ThenDcbModel{T}(EventTagQuery, object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task ThenDcbModel<T>(EventTagQuery query, object expected) where T : class => Scenario.ThenDcbModel<T>(query, expected);

    // ---- assert: any object ----------------------------------------------------------------

    /// <inheritdoc cref="CritterStackScenario.Verify" />
    public void Verify(object subject, StepTable expected) => Scenario.Verify(subject, expected);

    /// <inheritdoc cref="CritterStackScenario.ThenMatches" />
    public void ThenMatches(object? subject, object expected) => Scenario.ThenMatches(subject, expected);
}
