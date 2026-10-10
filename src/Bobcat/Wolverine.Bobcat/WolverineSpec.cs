using Bobcat;
using JasperFx.Events;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace Wolverine.Bobcat;

/// <summary>
/// The base class for specifying a Wolverine application in an ordinary xUnit or TUnit test, with the
/// whole Given/When/Then vocabulary of <see cref="WolverineScenario" /> as its own members (GH-4833).
/// </summary>
/// <remarks>
/// <para>
/// Put Bobcat's <c>[BobcatFeature]</c> on the class (Bobcat.Xunit records every test in a feature class)
/// and every step renders as a specification; without a recording the same test runs as an ordinary
/// test and a wrong is thrown.
/// </para>
/// <code>
/// [BobcatFeature("Appointments")]
/// public class confirming_an_appointment(AppFixture app) : WolverineSpec(app.Host)
/// {
///     [Fact]
///     public async Task a_scheduled_appointment_is_confirmed()
///     {
///         var id = Guid.NewGuid();
///         await GivenEvents&lt;Appointment&gt;(id, new AppointmentScheduled(id));
///         await WhenReceived(new ConfirmAppointment(id));
///         ThenEvents(typeof(AppointmentConfirmed));
///     }
/// }
/// </code>
/// <para>
/// Every member is public, not protected, so a test's own helpers in another class can drive it too.
/// </para>
/// </remarks>
public abstract class WolverineSpec
{
    protected WolverineSpec(IHost host, string? storeName = null) : this(new WolverineScenario(host, storeName))
    {
    }

    protected WolverineSpec(WolverineScenario scenario)
    {
        Scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
    }

    /// <summary>The scenario every step delegates to.</summary>
    public WolverineScenario Scenario { get; }

    /// <inheritdoc cref="WolverineScenario.Host" />
    public IHost Host => Scenario.Host;

    /// <inheritdoc cref="WolverineScenario.Store" />
    public IEventStore Store => Scenario.Store;

    /// <inheritdoc cref="WolverineScenario.UnspecifiedValues" />
    public IUnspecifiedValues? UnspecifiedValues
    {
        get => Scenario.UnspecifiedValues;
        set => Scenario.UnspecifiedValues = value;
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

    /// <inheritdoc cref="WolverineScenario.LastAct" />
    public ActOutcome LastAct => Scenario.LastAct;

    /// <inheritdoc cref="WolverineScenario.ResetAsync" />
    public Task ResetAsync(CancellationToken token = default) => Scenario.ResetAsync(token);

    // ---- arrange ---------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.GivenEvents{TAggregate}(Guid, object[])" />
    public Task GivenEvents<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => Scenario.GivenEvents<TAggregate>(id, events);

    /// <inheritdoc cref="WolverineScenario.GivenEvents{TAggregate}(string, object[])" />
    public Task GivenEvents<TAggregate>(string key, params object[] events) where TAggregate : class
        => Scenario.GivenEvents<TAggregate>(key, events);

    /// <inheritdoc cref="WolverineScenario.GivenEvents(Type, object, object[])" />
    public Task GivenEvents(Type? aggregate, object id, params object[] events) => Scenario.GivenEvents(aggregate, id, events);

    /// <inheritdoc cref="WolverineScenario.GivenEvents(Guid, object[])" />
    public Task GivenEvents(Guid id, params object[] events) => Scenario.GivenEvents(id, events);

    /// <inheritdoc cref="WolverineScenario.GivenEvents(string, object[])" />
    public Task GivenEvents(string key, params object[] events) => Scenario.GivenEvents(key, events);

    /// <inheritdoc cref="WolverineScenario.GivenNoEventsFor{TAggregate}(Guid)" />
    public Task GivenNoEventsFor<TAggregate>(Guid id) where TAggregate : class => Scenario.GivenNoEventsFor<TAggregate>(id);

    /// <inheritdoc cref="WolverineScenario.GivenNoEventsFor{TAggregate}(string)" />
    public Task GivenNoEventsFor<TAggregate>(string key) where TAggregate : class => Scenario.GivenNoEventsFor<TAggregate>(key);

    /// <inheritdoc cref="WolverineScenario.GivenReadModel{T}(object)" />
    public Task GivenReadModel<T>(object document) where T : class => Scenario.GivenReadModel<T>(document);

    /// <inheritdoc cref="WolverineScenario.GivenEventsOn{TAggregate}(Guid, object[])" />
    public Task GivenEventsOn<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => Scenario.GivenEventsOn<TAggregate>(id, events);

    /// <inheritdoc cref="WolverineScenario.GivenEventsOn{TAggregate}(string, object[])" />
    public Task GivenEventsOn<TAggregate>(string key, params object[] events) where TAggregate : class
        => Scenario.GivenEventsOn<TAggregate>(key, events);

    // ---- act -------------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.WhenReceived" />
    public Task WhenReceived(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
        => Scenario.WhenReceived(message, configureTracking);

    /// <inheritdoc cref="WolverineScenario.WhenPublished" />
    public Task WhenPublished(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
        => Scenario.WhenPublished(message, configureTracking);

    /// <inheritdoc cref="WolverineScenario.WhenTracked" />
    public Task WhenTracked(string description, Func<Task> act,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
        => Scenario.WhenTracked(description, act, configureTracking);

    // ---- assert ----------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.ThenEvents(Type[])" />
    public void ThenEvents(params Type[] events) => Scenario.ThenEvents(events);

    /// <inheritdoc cref="WolverineScenario.ThenEvents(object[])" />
    public void ThenEvents(params object[] events) => Scenario.ThenEvents(events);

    /// <inheritdoc cref="WolverineScenario.ThenEventsOn{TAggregate}(Guid, object[])" />
    public void ThenEventsOn<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => Scenario.ThenEventsOn<TAggregate>(id, events);

    /// <inheritdoc cref="WolverineScenario.ThenEventsOn{TAggregate}(string, object[])" />
    public void ThenEventsOn<TAggregate>(string key, params object[] events) where TAggregate : class
        => Scenario.ThenEventsOn<TAggregate>(key, events);

    /// <inheritdoc cref="WolverineScenario.ThenNoEventsOn{TAggregate}(Guid)" />
    public void ThenNoEventsOn<TAggregate>(Guid id) where TAggregate : class => Scenario.ThenNoEventsOn<TAggregate>(id);

    /// <inheritdoc cref="WolverineScenario.ThenNoEventsOn{TAggregate}(string)" />
    public void ThenNoEventsOn<TAggregate>(string key) where TAggregate : class => Scenario.ThenNoEventsOn<TAggregate>(key);

    /// <inheritdoc cref="WolverineScenario.ThenEventsInAnyOrder(object[])" />
    public void ThenEventsInAnyOrder(params object[] events) => Scenario.ThenEventsInAnyOrder(events);

    /// <inheritdoc cref="WolverineScenario.ThenEventsInAnyOrder(Type[])" />
    public void ThenEventsInAnyOrder(params Type[] events) => Scenario.ThenEventsInAnyOrder(events);

    /// <inheritdoc cref="WolverineScenario.ThenEmitted(object[])" />
    public void ThenEmitted(params object[] events) => Scenario.ThenEmitted(events);

    /// <inheritdoc cref="WolverineScenario.ThenEmitted{T}" />
    public void ThenEmitted<T>() => Scenario.ThenEmitted<T>();

    /// <inheritdoc cref="WolverineScenario.ThenNotEmitted{T}" />
    public void ThenNotEmitted<T>() => Scenario.ThenNotEmitted<T>();

    /// <inheritdoc cref="WolverineScenario.ThenNotEmitted(object[])" />
    public void ThenNotEmitted(params object[] events) => Scenario.ThenNotEmitted(events);

    /// <inheritdoc cref="WolverineScenario.ThenNoEvents" />
    public void ThenNoEvents() => Scenario.ThenNoEvents();

    /// <inheritdoc cref="WolverineScenario.TheEvent{T}" />
    public T TheEvent<T>() => Scenario.TheEvent<T>();

    /// <inheritdoc cref="WolverineScenario.TheEvents" />
    public IReadOnlyList<object> TheEvents => Scenario.TheEvents;

    /// <inheritdoc cref="WolverineScenario.ThenStreamIsStarted{TAggregate}(Guid)" />
    public Task ThenStreamIsStarted<TAggregate>(Guid id) where TAggregate : class => Scenario.ThenStreamIsStarted<TAggregate>(id);

    /// <inheritdoc cref="WolverineScenario.ThenStreamIsStarted{TAggregate}(string)" />
    public Task ThenStreamIsStarted<TAggregate>(string key) where TAggregate : class => Scenario.ThenStreamIsStarted<TAggregate>(key);

    /// <inheritdoc cref="WolverineScenario.TheAggregate{T}(Guid)" />
    public Task<T?> TheAggregate<T>(Guid id) where T : class => Scenario.TheAggregate<T>(id);

    /// <inheritdoc cref="WolverineScenario.TheAggregate{T}(string)" />
    public Task<T?> TheAggregate<T>(string key) where T : class => Scenario.TheAggregate<T>(key);

    /// <inheritdoc cref="WolverineScenario.ThenEventsOn(JasperFx.Events.Tags.EventTagQuery, object[])" />
    public void ThenEventsOn(JasperFx.Events.Tags.EventTagQuery query, params object[] events) => Scenario.ThenEventsOn(query, events);

    /// <inheritdoc cref="WolverineScenario.ThenNoEventsOn(JasperFx.Events.Tags.EventTagQuery)" />
    public void ThenNoEventsOn(JasperFx.Events.Tags.EventTagQuery query) => Scenario.ThenNoEventsOn(query);

    /// <inheritdoc cref="WolverineScenario.TheDcbModel{T}(JasperFx.Events.Tags.EventTagQuery)" />
    public Task<T?> TheDcbModel<T>(JasperFx.Events.Tags.EventTagQuery query) where T : class => Scenario.TheDcbModel<T>(query);

    /// <inheritdoc cref="WolverineScenario.ThenDcbModel{T}(JasperFx.Events.Tags.EventTagQuery, object)" />
    public Task ThenDcbModel<T>(JasperFx.Events.Tags.EventTagQuery query, object expected) where T : class
        => Scenario.ThenDcbModel<T>(query, expected);

    /// <inheritdoc cref="WolverineScenario.TheStartedStream{TAggregate}()" />
    public Guid TheStartedStream<TAggregate>() where TAggregate : class => Scenario.TheStartedStream<TAggregate>();

    /// <inheritdoc cref="WolverineScenario.TheStartedStreamKey{TAggregate}()" />
    public string TheStartedStreamKey<TAggregate>() where TAggregate : class => Scenario.TheStartedStreamKey<TAggregate>();

    /// <inheritdoc cref="WolverineScenario.ThenRefusedWith(string)" />
    public void ThenRefusedWith(string reason) => Scenario.ThenRefusedWith(reason);

    /// <inheritdoc cref="WolverineScenario.ThenRefusedWith(string, object[])" />
    public void ThenRefusedWith(string reason, params object[] mentioning) => Scenario.ThenRefusedWith(reason, mentioning);

    /// <inheritdoc cref="WolverineScenario.ThenRefusedWith{TException}(object?)" />
    public TException? ThenRefusedWith<TException>(object? expected = null) where TException : Exception
        => Scenario.ThenRefusedWith<TException>(expected);

    /// <inheritdoc cref="WolverineScenario.ThenValidationFails" />
    public void ThenValidationFails(string reason) => Scenario.ThenValidationFails(reason);

    /// <inheritdoc cref="WolverineScenario.ThenAggregate{T}(Guid)" />
    public Task<T> ThenAggregate<T>(Guid id) where T : class => Scenario.ThenAggregate<T>(id);

    /// <inheritdoc cref="WolverineScenario.ThenAggregate{T}(string)" />
    public Task<T> ThenAggregate<T>(string key) where T : class => Scenario.ThenAggregate<T>(key);

    /// <inheritdoc cref="WolverineScenario.ThenAggregate{T}(Guid, object)" />
    public Task<T> ThenAggregate<T>(Guid id, object expected) where T : class => Scenario.ThenAggregate<T>(id, expected);

    /// <inheritdoc cref="WolverineScenario.ThenAggregate{T}(string, object)" />
    public Task<T> ThenAggregate<T>(string key, object expected) where T : class => Scenario.ThenAggregate<T>(key, expected);

    /// <inheritdoc cref="WolverineScenario.ThenDocument{T}(object)" />
    public Task<T> ThenDocument<T>(object id) where T : class => Scenario.ThenDocument<T>(id);

    /// <inheritdoc cref="WolverineScenario.ThenDocument{T}(object, object)" />
    public Task<T> ThenDocument<T>(object id, object expected) where T : class => Scenario.ThenDocument<T>(id, expected);

    /// <inheritdoc cref="WolverineScenario.ThenReadModel{T}(object)" />
    public Task<T> ThenReadModel<T>(object id) where T : class => Scenario.ThenReadModel<T>(id);

    /// <inheritdoc cref="WolverineScenario.ThenReadModel{T}(object, object)" />
    public Task<T> ThenReadModel<T>(object id, object expected) where T : class => Scenario.ThenReadModel<T>(id, expected);

    /// <inheritdoc cref="WolverineScenario.ThenNoReadModel{T}(object)" />
    public Task ThenNoReadModel<T>(object id) where T : class => Scenario.ThenNoReadModel<T>(id);

    /// <inheritdoc cref="WolverineScenario.ThenNoReadModel{T}()" />
    public Task ThenNoReadModel<T>() where T : class => Scenario.ThenNoReadModel<T>();

    /// <inheritdoc cref="WolverineScenario.ThenSingleReadModel{T}(object?)" />
    public Task<T> ThenSingleReadModel<T>(object? expected = null) where T : class => Scenario.ThenSingleReadModel<T>(expected);

    /// <inheritdoc cref="WolverineScenario.ThenProjectionsAreCaughtUp" />
    public Task ThenProjectionsAreCaughtUp(Type readModel) => Scenario.ThenProjectionsAreCaughtUp(readModel);

    /// <inheritdoc cref="WolverineScenario.ThenMessageSent{T}(string?)" />
    public void ThenMessageSent<T>(string? destination = null) => Scenario.ThenMessageSent<T>(destination);

    /// <inheritdoc cref="WolverineScenario.ThenMessageSent(object)" />
    public void ThenMessageSent(object expected) => Scenario.ThenMessageSent(expected);

    /// <inheritdoc cref="WolverineScenario.ThenMessageSentLocally{T}" />
    public void ThenMessageSentLocally<T>() => Scenario.ThenMessageSentLocally<T>();

    /// <inheritdoc cref="WolverineScenario.ThenMessageSentExternally{T}" />
    public void ThenMessageSentExternally<T>() => Scenario.ThenMessageSentExternally<T>();

    /// <inheritdoc cref="WolverineScenario.ThenMessageScheduled{T}" />
    public void ThenMessageScheduled<T>(TimeSpan? delay = null) => Scenario.ThenMessageScheduled<T>(delay);

    /// <inheritdoc cref="WolverineScenario.ThenNoMessageScheduled{T}" />
    public void ThenNoMessageScheduled<T>() => Scenario.ThenNoMessageScheduled<T>();

    /// <inheritdoc cref="WolverineScenario.ThenNoMessageSent{T}" />
    public void ThenNoMessageSent<T>() => Scenario.ThenNoMessageSent<T>();

    /// <inheritdoc cref="WolverineScenario.TheMessagesSent" />
    public IReadOnlyList<object> TheMessagesSent => Scenario.TheMessagesSent;

    /// <inheritdoc cref="WolverineScenario.Verify" />
    public void Verify(object subject, StepTable expected) => Scenario.Verify(subject, expected);

    /// <inheritdoc cref="WolverineScenario.ThenMatches" />
    public void ThenMatches(object? subject, object expected) => Scenario.ThenMatches(subject, expected);
}
