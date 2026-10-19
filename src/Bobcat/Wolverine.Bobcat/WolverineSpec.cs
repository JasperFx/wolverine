using Bobcat;
using Bobcat.CritterStack;
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
public abstract class WolverineSpec : CritterStackSpec
{
    protected WolverineSpec(IHost host, string? storeName = null) : this(new WolverineScenario(host, storeName))
    {
    }

    /// <summary>
    /// A spec over an application's services alone, with no <see cref="IHost" />; <c>Host</c> throws. The acts
    /// track the Wolverine runtime resolved from <paramref name="services" />.
    /// </summary>
    protected WolverineSpec(IServiceProvider services, string? storeName = null)
        : this(new WolverineScenario(services, storeName))
    {
    }

    protected WolverineSpec(WolverineScenario scenario) : base(scenario)
    {
    }

    /// <summary>The scenario every step delegates to.</summary>
    public new WolverineScenario Scenario => (WolverineScenario)base.Scenario;

    /// <inheritdoc cref="WolverineScenario.Timeout" />
    public TimeSpan Timeout
    {
        get => Scenario.Timeout;
        set => Scenario.Timeout = value;
    }

    /// <inheritdoc cref="WolverineScenario.EventuallyTimeout" />
    public TimeSpan EventuallyTimeout
    {
        get => Scenario.EventuallyTimeout;
        set => Scenario.EventuallyTimeout = value;
    }

    /// <inheritdoc cref="WolverineScenario.IncludeExternalTransports" />
    public bool IncludeExternalTransports
    {
        get => Scenario.IncludeExternalTransports;
        set => Scenario.IncludeExternalTransports = value;
    }

    /// <inheritdoc cref="WolverineScenario.LastAct" />
    public new ActOutcome LastAct => Scenario.LastAct;

    // ---- several stores (GH-4977, bobcat#504) ------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.In{TStore}" />
    public new WolverineScenario In<TStore>() where TStore : class => Scenario.In<TStore>();

    /// <inheritdoc cref="WolverineScenario.In(string)" />
    public new WolverineScenario In(string storeName) => Scenario.In(storeName);

    // ---- act -------------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.WhenReceived" />
    [EventModelRole(SliceRole.Command, nameof(message))]
    public Task WhenReceived(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null,
        DeliveryOptions? options = null)
        => Scenario.WhenReceived(message, configureTracking, options);

    /// <inheritdoc cref="WolverineScenario.WhenPublished" />
    [EventModelRole(SliceRole.Command, nameof(message))]
    public Task WhenPublished(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null,
        DeliveryOptions? options = null)
        => Scenario.WhenPublished(message, configureTracking, options);

    /// <inheritdoc cref="WolverineScenario.GivenReceived" />
    public Task GivenReceived(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null,
        DeliveryOptions? options = null)
        => Scenario.GivenReceived(message, configureTracking, options);

    /// <inheritdoc cref="WolverineScenario.WhenTracked" />
    public Task WhenTracked(string description, Func<Task> act,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
        => Scenario.WhenTracked(description, act, configureTracking);

    // ---- a concurrent writer (GH-4940) -------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.WhileActing" />
    [EventModelRole(SliceRole.ArrangedEvent, nameof(concurrently))]
    public void WhileActing(params object[] concurrently) => Scenario.WhileActing(concurrently);

    /// <inheritdoc cref="WolverineScenario.ThenRetried" />
    public void ThenRetried() => Scenario.ThenRetried();

    // ---- assert: events past an asynchronous boundary (GH-4939) ---------------------------------

    /// <inheritdoc cref="WolverineScenario.ThenEventuallyCaused(object[])" />
    public Task ThenEventuallyCaused(params object[] events) => Scenario.ThenEventuallyCaused(events);

    /// <inheritdoc cref="WolverineScenario.ThenEventuallyCaused{T}" />
    public Task ThenEventuallyCaused<T>() => Scenario.ThenEventuallyCaused<T>();

    /// <inheritdoc cref="WolverineScenario.ActCorrelationId" />
    public string? ActCorrelationId => Scenario.ActCorrelationId;

    // ---- assert: the consistency boundary (GH-4940) --------------------------------------------

    /// <inheritdoc cref="WolverineScenario.ThenTheConsistencyBoundaryIs(JasperFx.Events.Tags.EventTagQuery)" />
    public void ThenTheConsistencyBoundaryIs(JasperFx.Events.Tags.EventTagQuery expected) => Scenario.ThenTheConsistencyBoundaryIs(expected);

    /// <inheritdoc cref="WolverineScenario.ThenTheConsistencyBoundaryIs{TModel}(JasperFx.Events.Tags.EventTagQuery)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TModel))]
    public void ThenTheConsistencyBoundaryIs<TModel>(JasperFx.Events.Tags.EventTagQuery expected)
        => Scenario.ThenTheConsistencyBoundaryIs<TModel>(expected);

    /// <inheritdoc cref="WolverineScenario.ThenAConcurrentEventWouldConflict(object)" />
    public void ThenAConcurrentEventWouldConflict(object @event) => Scenario.ThenAConcurrentEventWouldConflict(@event);

    /// <inheritdoc cref="WolverineScenario.ThenAConcurrentEventWouldConflict{TEvent}(object[])" />
    public void ThenAConcurrentEventWouldConflict<TEvent>(params object[] tags) => Scenario.ThenAConcurrentEventWouldConflict<TEvent>(tags);

    /// <inheritdoc cref="WolverineScenario.ThenAConcurrentEventWouldNotConflict(object)" />
    public void ThenAConcurrentEventWouldNotConflict(object @event) => Scenario.ThenAConcurrentEventWouldNotConflict(@event);

    /// <inheritdoc cref="WolverineScenario.ThenAConcurrentEventWouldNotConflict{TEvent}(object[])" />
    public void ThenAConcurrentEventWouldNotConflict<TEvent>(params object[] tags) => Scenario.ThenAConcurrentEventWouldNotConflict<TEvent>(tags);

    // ---- assert: cascaded messages -----------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.ThenMessageSent{T}(string?)" />
    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessageSent<T>(string? destination = null) => Scenario.ThenMessageSent<T>(destination);

    /// <inheritdoc cref="WolverineScenario.ThenMessageSent(object)" />
    [EventModelRole(SliceRole.Message, nameof(expected))]
    public void ThenMessageSent(object expected) => Scenario.ThenMessageSent(expected);

    /// <inheritdoc cref="WolverineScenario.ThenMessageSentLocally{T}" />
    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessageSentLocally<T>() => Scenario.ThenMessageSentLocally<T>();

    /// <inheritdoc cref="WolverineScenario.ThenMessageSentExternally{T}" />
    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessageSentExternally<T>() => Scenario.ThenMessageSentExternally<T>();

    /// <inheritdoc cref="WolverineScenario.ThenMessageScheduled{T}" />
    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessageScheduled<T>(TimeSpan? delay = null) => Scenario.ThenMessageScheduled<T>(delay);

    /// <inheritdoc cref="WolverineScenario.ThenMessageScheduled(object)" />
    [EventModelRole(SliceRole.Message, nameof(expected))]
    public void ThenMessageScheduled(object expected) => Scenario.ThenMessageScheduled(expected);

    /// <inheritdoc cref="WolverineScenario.ThenNoMessageScheduled{T}" />
    public void ThenNoMessageScheduled<T>() => Scenario.ThenNoMessageScheduled<T>();

    /// <inheritdoc cref="WolverineScenario.ThenNoMessageSent{T}" />
    public void ThenNoMessageSent<T>() => Scenario.ThenNoMessageSent<T>();

    /// <inheritdoc cref="WolverineScenario.TheMessagesSent" />
    public IReadOnlyList<object> TheMessagesSent => Scenario.TheMessagesSent;

    // ---- assert: handled messages (GH-5076) ----------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.ThenHandled{T}" />
    public void ThenHandled<T>() => Scenario.ThenHandled<T>();

    /// <inheritdoc cref="WolverineScenario.ThenHandled(Type)" />
    public void ThenHandled(Type messageType) => Scenario.ThenHandled(messageType);

    /// <inheritdoc cref="WolverineScenario.ThenNotHandled{T}" />
    public void ThenNotHandled<T>() => Scenario.ThenNotHandled<T>();

    /// <inheritdoc cref="WolverineScenario.ThenNotHandled(Type)" />
    public void ThenNotHandled(Type messageType) => Scenario.ThenNotHandled(messageType);
}
