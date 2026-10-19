using Bobcat;
using Bobcat.CritterStack;
using Bobcat.Engine;
using Bobcat.Runtime;
using System.Globalization;
using JasperFx.Events;
using JasperFx.Events.Tags;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace Wolverine.Bobcat;

/// <summary>
/// What one act did: the tracked session, the events it appended, and how it failed or was refused.
/// </summary>
/// <param name="Session">The tracked session, when the act got as far as completing one.</param>
/// <param name="NewEvents">
/// The events the act itself appended (GH-4931): what the sessions handling the act's own message, or its
/// HTTP request, committed. A cascade's events are in <see cref="CritterStackActOutcome.AllNewEvents" />, not here.
/// </param>
/// <param name="Error">The exception the act raised or a handler threw, when there was one.</param>
/// <param name="Refusal">A refusal that is an answer rather than a failure — an HTTP endpoint's 4xx body. A bus-dispatched command refuses by throwing, which is <paramref name="Error" />.</param>
public sealed record ActOutcome(
    ITrackedSession? Session,
    IReadOnlyList<IEvent> NewEvents,
    Exception? Error,
    string? Refusal = null) : CritterStackActOutcome(NewEvents, Error, Refusal)
{
    /// <summary>Nothing has acted yet.</summary>
    public new static readonly ActOutcome None = new(null, Array.Empty<IEvent>(), null);

    /// <summary>
    /// GH-4931. Each event-store session that committed while the act ran, with the message it was handling,
    /// which says whose events are whose.
    /// </summary>
    public IReadOnlyList<AppendedEvents> Appended { get; init; } = Array.Empty<AppendedEvents>();

    /// <summary>
    /// GH-4940. The Dynamic Consistency Boundaries the act itself loaded, in load order: each tag query its
    /// <c>[DcbModel]</c> was fetched over, which is the query a concurrent append is checked against when the
    /// boundary is saved. A cascade's loads are not here.
    /// </summary>
    public IReadOnlyList<LoadedBoundary> Boundaries { get; init; } = Array.Empty<LoadedBoundary>();

    /// <summary>
    /// GH-4939. The correlation id the act ran under: every message it caused carries it, and an event store
    /// with correlation ids enabled records it on every event those messages append, past an asynchronous
    /// boundary too. What <c>ThenEventuallyCaused</c> queries the store by.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// GH-4940. How many times the act's own message was executed: 1 for a message handled once, more when an error
    /// policy retried it, 0 for an act with no message of its own (an HTTP request, <c>WhenTracked</c>).
    /// </summary>
    public int Attempts { get; init; }

    /// <summary>
    /// GH-4940. Whether the concurrent append armed with <c>WhileActing(concurrently: ...)</c> landed: true when it
    /// did, false when the act saved nothing so it never could, null when none was armed.
    /// </summary>
    public bool? ConcurrentAppendLanded { get; init; }

    /// <summary>
    /// GH-4962. The tenant the act ran for: what its own event-store sessions committed for -- the entry message's
    /// <see cref="Envelope.TenantId" />, or an HTTP endpoint's tenant detection -- else the entry message's envelope,
    /// else the one tenant every message it caused carried. Null for an act with no tenant.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// GH-5077. The act's own messages (<see cref="OwnMessages" />) that Wolverine only scheduled -- a
    /// <see cref="TimeoutMessage" />, a <c>DelayedFor</c> wrapper's message, a <c>ScheduleAsync</c> through the bus
    /// <c>WhenTracked</c> hands its act -- so no handler of them ran within the act. Empty when every one was handled, or
    /// the act sent none of its own: what a handler or an HTTP endpoint the act invoked scheduled is what the act caused,
    /// and never trips this. <c>WhenScheduledMessagesAreExecuted()</c> runs them either way.
    /// </summary>
    public IReadOnlyList<object> OnlyScheduled { get; init; } = Array.Empty<object>();

    /// <summary>
    /// wolverine#5077. The act's own messages: the one <c>WhenReceived</c> or <c>WhenPublished</c> dispatched, or what the
    /// act's own code sent through the bus it was handed. Never "sent" in any step or summary -- sent means what the
    /// handling produced.
    /// </summary>
    public IReadOnlyList<object> OwnMessages { get; init; } = Array.Empty<object>();
}

/// <summary>
/// The Given/When/Then vocabulary for a Wolverine application, over one host (GH-4833): arrange
/// events, act through Wolverine's tracked session, and assert what the act appended and cascaded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built on Bobcat's store-only <see cref="CritterStackScenario" /></b> (bobcat#471), which holds every
/// step a store can answer — the Givens, the event, aggregate, document and read-model assertions, the
/// refusals. This class adds the acts that go through Wolverine's tracked session, the message
/// assertions and the consistency-boundary steps, and fills the shared outcome from what the tracked
/// session saw.
/// </para>
/// <para>
/// <b>Composable on purpose.</b> <see cref="WolverineSpec" /> is the base class an xUnit or TUnit
/// suite derives from, and it delegates every step here.
/// </para>
/// <para>
/// <b>Every step renders.</b> Each one records itself through <see cref="ScenarioRecorder" /> and reports
/// its verdict as cells. Outside a recording scenario the steps still run and a wrong is thrown; see
/// <see cref="SpecificationFailedException" />.
/// </para>
/// </remarks>
public class WolverineScenario : CritterStackScenario
{
    // GH-4931: the message an act dispatches -- built from a partial object, so not always the object the
    // step was given -- which is how its own events are told from its cascades'
    private object? _dispatched;

    // An act whose own work runs with no envelope at all -- an HTTP request -- so only its envelope-less
    // commits are its own, never a cascade's
    private static readonly object NoMessage = new();

    /// <summary>
    /// GH-4931. The next act reaches the application with no message of its own, such as an HTTP request:
    /// only what is committed outside any message handler is its own, and every cascade's events are not.
    /// </summary>
    protected internal void NextActDispatchesNoMessage() => root._dispatched = NoMessage;

    // GH-4977: a view from In<TStore>() / In(name) shares its specification with the scenario it came from, and
    // every act and every Wolverine setting is that scenario's
    private readonly WolverineScenario? _origin;
    private WolverineScenario root => _origin ?? this;

    // GH-5077: what the next act's own code sends through the bus it is handed
    private ActMessages? _actMessages;

    // GH-4940: the concurrent append the next act lands between its read and its write
    private object[]? _concurrently;

    /// <param name="host">The application under test.</param>
    /// <param name="storeName">Which event store to use, when the application has several. Null takes the only one.</param>
    public WolverineScenario(IHost host, string? storeName = null) : base(host, storeName)
    {
        _host = host;
    }

    /// <summary>
    /// A scenario over an application's services alone, for a test that has a container but no
    /// <see cref="IHost" /> -- a host's <c>Services</c> handed on, say. The acts track the Wolverine
    /// runtime resolved from <paramref name="services" />; <c>Host</c> throws on a scenario built this way.
    /// </summary>
    /// <param name="services">The application's services. Its Wolverine runtime and event store are resolved from them.</param>
    /// <param name="storeName">Which event store to use, when the application has several. Null takes the only one.</param>
    public WolverineScenario(IServiceProvider services, string? storeName = null) : base(services, storeName)
    {
    }

    /// <summary>
    /// GH-4977. A view of <paramref name="root" />'s specification pinned to <paramref name="store" /> -- what
    /// <see cref="In{TStore}" /> and <see cref="In(string)" /> hand out. Its Givens and Thens use that store; its
    /// acts, message steps and consistency-boundary steps are <paramref name="root" />'s.
    /// </summary>
    protected WolverineScenario(WolverineScenario root, IEventStore store) : base(root, store)
    {
        _origin = root.root;
        _host = root._host;
    }

    /// <summary>GH-4977. A Wolverine view, so the message and DCB steps work on <c>In&lt;T&gt;()</c> too.</summary>
    protected override CritterStackScenario createView(IEventStore store) => new WolverineScenario(this, store);

    /// <summary>
    /// GH-4977 (bobcat#504). The same vocabulary -- the Wolverine acts and steps included -- against the event
    /// store registered under <typeparamref name="TStore" />, such as Marten's <c>AddMartenStore&lt;IInvoicingStore&gt;()</c>.
    /// The view shares this specification: an act through it is this scenario's act.
    /// </summary>
    public new WolverineScenario In<TStore>() where TStore : class => (WolverineScenario)base.In<TStore>();

    /// <summary>
    /// GH-4977 (bobcat#504). The same vocabulary against the event store named <paramref name="storeName" />. See
    /// <see cref="In{TStore}" />.
    /// </summary>
    public new WolverineScenario In(string storeName) => (WolverineScenario)base.In(storeName);

    // Null when the scenario was built from an IServiceProvider
    private readonly IHost? _host;

    /// <summary>How long an act's tracked session waits for everything it caused to settle. Five seconds by default.</summary>
    public TimeSpan Timeout
    {
        get => root._timeout;
        set => root._timeout = value;
    }

    private TimeSpan _timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether an act's tracked session also waits on external transports — a message cascaded to a
    /// broker and handled back in this application. On by default.
    /// </summary>
    public bool IncludeExternalTransports
    {
        get => root._includeExternalTransports;
        set => root._includeExternalTransports = value;
    }

    private bool _includeExternalTransports = true;

    /// <summary>What the last act did.</summary>
    public new ActOutcome LastAct
    {
        get => base.LastAct as ActOutcome ?? ActOutcome.None;
        // ActOutcome.None is "nothing has acted" too, so it must not count as an act
        protected set => base.LastAct = ReferenceEquals(value, ActOutcome.None) ? CritterStackActOutcome.None : value;
    }

    /// <summary>
    /// GH-4939. The correlation id the act in progress runs under -- or the last act ran under, once it is done.
    /// Every act gets a new one, and the tracked session applies it to the act's own message. An act that
    /// reaches the application some other way, such as an HTTP call made inside <see cref="WhenTracked" />,
    /// sends it itself, as the <c>X-Correlation-ID</c> header for Wolverine.HTTP.
    /// </summary>
    public string? ActCorrelationId
    {
        get => root._actCorrelationId;
        private set => root._actCorrelationId = value;
    }

    private string? _actCorrelationId;

    /// <summary>
    /// GH-4939. How long <see cref="ThenEventuallyCaused(object[])" /> keeps looking for the events it expects
    /// once the async daemons have caught up, for work that lands after them -- a message an event
    /// subscription published, handled on a local queue. Ten seconds by default.
    /// </summary>
    public TimeSpan EventuallyTimeout
    {
        get => root._eventuallyTimeout;
        set => root._eventuallyTimeout = value;
    }

    private TimeSpan _eventuallyTimeout = TimeSpan.FromSeconds(10);

    // Whether any act has run since the scenario began or was reset: a negative step about what the act did
    // would otherwise pass on the empty outcome, as the base's do not
    private bool acted => !ReferenceEquals(base.LastAct, CritterStackActOutcome.None);

    /// <summary>A wrong outside a recording is a <see cref="SpecificationFailedException" />.</summary>
    protected override Exception failureOf(string message) => new SpecificationFailedException(message);

    // ---- a concurrent writer (GH-4940) -------------------------------------------------------

    /// <summary>
    /// GH-4940. While the next act runs, another writer appends <paramref name="concurrently" /> -- between the act's
    /// read and its write. The append lands on the act's <b>first</b> event-store save, before it commits, in a
    /// session of its own, on a new stream. Tag the events (<c>Tagged(new ItemReserved(sku, 1), sku)</c>) to put them
    /// inside or outside a Dynamic Consistency Boundary: inside, the act's save fails with a
    /// <c>DcbConcurrencyException</c> (<see cref="CritterStackScenario.ThenRefusedWith{TException}" />), or an error
    /// policy retries it and the retry succeeds (<see cref="ThenRetried" />); outside, the act is not disturbed.
    /// </summary>
    /// <remarks>
    /// The append fires once, so a retry sees the new events. An act that saves nothing never gives it a moment to
    /// land, and the act then fails saying so, rather than letting a "nothing disturbed it" pass vacuously. Needs the
    /// store integrated with Wolverine (<c>IntegrateWithWolverine()</c>), which registers the hook.
    /// </remarks>
    [EventModelRole(SliceRole.ArrangedEvent, nameof(concurrently))]
    public void WhileActing(params object[] concurrently)
    {
        var events = flatten(concurrently);
        if (events.Length == 0) throw new ArgumentException("Name the events a concurrent writer appends.", nameof(concurrently));

        var (described, inline) = describe(events);
        using var step = ScenarioRecorder.Step("Given", $"while the act runs, a concurrent writer appends {described}");
        if (!inline) recordValues("event", events);
        recordTouched(events.Select(PartialMatching.ExpectedType));

        root._concurrently = events.Select(x => Build(x, "a concurrent writer appends")).ToArray();
    }

    /// <summary>
    /// GH-4940. The act's own message failed at least once and an error policy retried it until it succeeded -- what
    /// a retry policy on <c>DcbConcurrencyException</c> does after <see cref="WhileActing" /> lands inside the boundary.
    /// </summary>
    public void ThenRetried()
    {
        using var step = ScenarioRecorder.Step("Then", "the act is retried and succeeds");
        if (!actSucceeded()) return;

        Verdicts.Check("attempts", LastAct.Attempts > 1 ? "more than one" : LastAct.Attempts.ToString(CultureInfo.InvariantCulture), "more than one");
        Verdicts.Fact(LastAct.Attempts > 1, LastAct.Attempts == 0
            ? "Expected the act to be retried, but it dispatched no message of its own to retry."
            : "Expected the act to be retried, but its message succeeded on the first attempt" +
              (LastAct.ConcurrentAppendLanded == true ? ", although the concurrent append landed: is it outside the boundary?" : "."));
    }

    // ---- act -------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="message" /> arrives at the application: sent through Wolverine, and the scenario
    /// waits for everything it caused to settle. A handler that throws is captured, not rethrown, so a
    /// refusal can be asserted with <see cref="CritterStackScenario.ThenValidationFails" />.
    /// </summary>
    /// <remarks>
    /// <paramref name="message" /> may be a partial object — <c>Specify&lt;T&gt;()</c> or a table row —
    /// built before the act, with its unspecified members filled by <see cref="CritterStackScenario.UnspecifiedValues" />.
    /// The step shows only the members it specifies.
    /// </remarks>
    /// <param name="message">The message, whole or partial.</param>
    /// <param name="configureTracking">Adjusts the tracked session.</param>
    /// <param name="options">
    /// GH-4962. How the message is sent: <c>new DeliveryOptions { TenantId = "red" }</c> sends it for a tenant, which
    /// the act then carries (<see cref="ActOutcome.TenantId" />).
    /// </param>
    /// <remarks>
    /// GH-5077. The message is sent exactly as given, as Wolverine sends it: a <see cref="TimeoutMessage" />, a
    /// <c>DelayedFor</c> / <c>ScheduledAt</c> wrapper or <paramref name="options" /> with a schedule is only
    /// <b>scheduled</b>, and its handler does not run within the act. Add <see cref="WhenScheduledMessagesAreExecuted" />
    /// after the act to run it. Until then a step about what the handler did fails rather than passing on nothing.
    /// </remarks>
    [EventModelRole(SliceRole.Command, nameof(message))]
    public Task WhenReceived(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null,
        DeliveryOptions? options = null)
    {
        // Built before the act, so a partial object the type cannot take fails the spec, not the act
        var built = Build(message, $"{nameOf(PartialMatching.ExpectedType(message))} is received");
        root._dispatched = built;
        return root.ActAsync("{0} is received", message,
            tracking => ownCorrelation(configureTracking, tracking, options).SendMessageAndWaitAsync(built, options));
    }

    /// <summary><paramref name="message" /> is published through Wolverine to every subscriber, and the scenario waits for what it caused.</summary>
    /// <param name="message">The message, whole or partial.</param>
    /// <param name="configureTracking">Adjusts the tracked session.</param>
    /// <param name="options">GH-4962. How the message is published, a tenant included.</param>
    /// <remarks>GH-5077. A delayed message is only scheduled, as for <see cref="WhenReceived" />.</remarks>
    [EventModelRole(SliceRole.Command, nameof(message))]
    public Task WhenPublished(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null,
        DeliveryOptions? options = null)
    {
        var built = Build(message, $"{nameOf(PartialMatching.ExpectedType(message))} is published");
        root._dispatched = built;
        return root.ActAsync("{0} is published", message,
            tracking => ownCorrelation(configureTracking, tracking, options).PublishMessageAndWaitAsync(built, options));
    }

    /// <summary>
    /// GH-5077. The messages the last act scheduled for later are executed now, through Wolverine's
    /// <see cref="ITrackedSession.PlayScheduledMessagesAsync" />, and this is the new act: <see cref="LastAct" /> is
    /// what the replay did, so the Then steps after it judge the handlers of the scheduled messages — a saga's timeout,
    /// a <c>DelayedFor</c> reminder, a <see cref="TimeoutMessage" /> sent with <see cref="WhenReceived" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every message the last act scheduled is executed, a local one inline and any other sent to its destination.
    /// The replay keeps the scheduled messages' correlation id, so <see cref="ThenEventuallyCaused(object[])" /> still
    /// finds what they caused.
    /// </para>
    /// <para>
    /// With no act before it, or an act that scheduled nothing, the step fails and stops the scenario, saying what
    /// the last act did instead. No Event Model role or trigger: which messages are scheduled is known only at run
    /// time, and the slice a spec describes is the one its command (or <c>[BobcatSpec(...)]</c>) names.
    /// </para>
    /// </remarks>
    /// <param name="timeout">How long the replay waits for everything it caused to settle. <see cref="Timeout" /> by default.</param>
    public Task WhenScheduledMessagesAreExecuted(TimeSpan? timeout = null) => root.playScheduledAsync(timeout ?? Timeout);

    private async Task playScheduledAsync(TimeSpan timeout)
    {
        var previous = acted ? LastAct : null;
        var scheduled = previous?.Session is { } session ? scheduledIn(session).ToList() : [];
        var types = scheduled.Select(x => x.Message?.GetType()).OfType<Type>().Distinct().ToArray();
        var text = types.Length == 0
            ? "the scheduled messages are executed"
            : $"the scheduled {describeTypes(types)} {(scheduled.Count == 1 ? "is" : "are")} executed";

        await inStep(ScenarioRecorder.Step("When", text), async () =>
        {
            if (previous?.Session is not { } played)
            {
                throw new SpecCriticalException(
                    "There is no act whose scheduled messages could be executed: WhenScheduledMessagesAreExecuted follows the When step that scheduled them.");
            }

            if (scheduled.Count == 0)
            {
                throw new SpecCriticalException(
                    $"The last act scheduled nothing, so there is nothing to execute. {activitySummary(previous)}");
            }

            foreach (var type in types) recordTouched(type);

            // Everything the replay commits is the act's own: there is no one message to tell its work from a cascade's
            _dispatched = null;
            await executeAsync(_ => replayAsync(previous, played, timeout),
                outcome => Task.FromResult(outcome with { CorrelationId = previous.CorrelationId ?? outcome.CorrelationId }));
        });
    }

    private static async Task<ITrackedSession> replayAsync(ActOutcome previous, ITrackedSession played, TimeSpan timeout)
    {
        try
        {
            return await played.PlayScheduledMessagesAsync(timeout);
        }
        catch (Exception e) when (e.Message.Contains("No scheduled messages recorded", StringComparison.Ordinal))
        {
            // Wolverine replays only what this service scheduled
            throw new SpecCriticalException(
                $"Wolverine found no message this application scheduled to execute. {activitySummary(previous)}", e);
        }
    }

    /// <summary>
    /// GH-5076. Arrange by handling <paramref name="message" />: it is sent through Wolverine and the scenario waits
    /// for everything it caused, exactly as for <see cref="WhenReceived" />, but the step is a <b>Given</b> and it is
    /// not the act. <see cref="LastAct" /> is left alone, so a Then step still judges the When that follows, and
    /// whatever the handlers appended is arranged history: the streams they wrote are this specification's own.
    /// </summary>
    /// <remarks>
    /// A handler that throws fails the step and stops the scenario, since there is nothing to judge an act against, and
    /// so does a message that is only scheduled, which arranges nothing. No Event Model role: a <c>Command</c> role here
    /// would make the arranging message the slice's act.
    /// </remarks>
    /// <param name="message">The message, whole or partial.</param>
    /// <param name="configureTracking">Adjusts the tracked session.</param>
    /// <param name="options">How the message is sent, a tenant included.</param>
    // An arranged message is run evidence, never the act: the Document role puts its type in the specification's resolved
    // types only -- no canvas element -- and, unlike a Command role or a trigger, does not make this call the slice's act
    [EventModelRole(SliceRole.Document, nameof(message))]
    public Task GivenReceived(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null,
        DeliveryOptions? options = null)
        => root.arrangeAsync(message, configureTracking, options);

    private async Task arrangeAsync(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking, DeliveryOptions? options)
    {
        var built = Build(message, $"{nameOf(PartialMatching.ExpectedType(message))} has already been received");
        var (described, inline) = describe([message]);
        var text = $"{described} has already been received";

        await inStep(ScenarioRecorder.Step("Given", text), async () =>
        {
            if (!inline) recordValues("message", [message]);
            recordTouched(built.GetType());

            HandlerWarmUp.WarmBeforeTracking(Services);
            var tracking = trackActivity();
            tracking = configureTracking?.Invoke(tracking) ?? tracking;

            // Thrown, not captured: an arrangement that failed leaves nothing for the act to be judged against
            var session = await tracking.SendMessageAndWaitAsync(built, options);
            if (session.AllExceptions().FirstOrDefault() is { } error)
            {
                throw new SpecCriticalException(
                    $"'{text}': handling it failed, so it arranged nothing: {error.GetType().Name}: {error.Message}", error);
            }

            var own = messageInside(built);
            if (session.Executed.Envelopes().All(x => !ReferenceEquals(x.Message, own)) &&
                scheduledIn(session).Any(x => ReferenceEquals(x.Message, own)))
            {
                throw new SpecCriticalException(
                    $"'{text}': it was only scheduled for later, so its handler never ran and it arranged nothing. Send it without a delay.");
            }

            // What the handlers appended is the specification's arranged history, as GivenEvents' is
            var appended = session.AppendedEvents.SelectMany(x => x.Events).ToList();
            foreach (var e in appended) claimStream(e.StreamKey ?? (object)e.StreamId);
            recordTouched(appended.Select(x => x.Data));
        });
    }

    /// <summary>
    /// Any act that reaches the application from outside — an HTTP call, a client SDK, a hosted
    /// service poke — run inside the tracked session, waiting for everything it caused.
    /// </summary>
    /// <param name="description">How the step reads: <c>"the nightly sweep runs"</c>.</param>
    /// <remarks>
    /// Whatever the act reaches -- an endpoint, a handler -- sends or schedules is what the act caused. To send a message
    /// as the act itself, use the overload that hands the act a bus.
    /// </remarks>
    public Task WhenTracked(string description, Func<Task> act,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
        => root.ActAsync(description,
            tracking => ownCorrelation(configureTracking, tracking, null)
                .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => act())));

    /// <summary>
    /// wolverine#5077. An act of its own, handed the tracked session's bus: what it sends, publishes or schedules through
    /// <c>bus</c> is the act's own message (<see cref="ActOutcome.OwnMessages" />), as <see cref="WhenReceived" />'s is --
    /// never counted as sent, and when it is only scheduled, a step about what its handler did fails saying so until
    /// <see cref="WhenScheduledMessagesAreExecuted" /> runs it.
    /// </summary>
    /// <param name="description">How the step reads: <c>"the escalation is scheduled"</c>.</param>
    /// <param name="act">The act, given the bus to send through. The bus carries the act's correlation id.</param>
    /// <param name="configureTracking">Adjusts the tracked session.</param>
    public Task WhenTracked(string description, Func<IMessageBus, Task> act,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
    {
        ArgumentNullException.ThrowIfNull(act);
        var messages = new ActMessages();
        root._actMessages = messages;
        return root.ActAsync(description,
            tracking => ownCorrelation(configureTracking, tracking, null)
                .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(context => act(messages.Wrap(context)))));
    }

    // GH-4940: every act runs under a correlation id of its own, generated per act, and it wins over one a
    // configureTracking or a DeliveryOptions names -- so no other session shares it, which is what lets a concurrent
    // append be armed by it alone. Nothing in Wolverine or its samples supplies an act's correlation id itself
    private TrackedSessionConfiguration ownCorrelation(
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking,
        TrackedSessionConfiguration tracking, DeliveryOptions? options)
    {
        var configured = configureTracking?.Invoke(tracking) ?? tracking;
        if (root.ActCorrelationId is not { } correlationId) return configured;

        if (options is not null) options.CorrelationId = correlationId;
        return configured.WithCorrelationId(correlationId);
    }

    /// <summary>
    /// The store-only acts of <see cref="CritterStackScenario" /> — <c>WhenActing</c>, <c>WhenActingOn</c> —
    /// run inside the tracked session too, so they wait for what they caused and the outcome carries the
    /// messages they sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the act appended is what the tracked session heard committed, as for every other act. The fallback is
    /// decided <b>per store</b> (GH-4977, bobcat#504): a store the tracked session heard no commit on -- the act
    /// appended through a session Wolverine did not open, such as one taken straight from the store -- is read back
    /// from its high-water mark before the act, with Bobcat's attribution (bobcat#503), so events on streams this
    /// specification never arranged or started are <see cref="CritterStackActOutcome.ForeignEvents" />.
    /// </para>
    /// <para>
    /// Events read back carry no tags and are not told apart from a cascade's, so an outcome that includes any says
    /// <see cref="CritterStackActOutcome.EventTagsKnown" /> is false.
    /// </para>
    /// </remarks>
    protected override async Task<CritterStackActOutcome> runActAsync(Func<IServiceProvider, Task> act)
    {
        // A view's act is its specification's act
        if (_origin is not null) return await _origin.runActAsync(act);

        // Taken before the act, on every store, for the fallback below: none at all without a store
        var marks = await highWaterMarksAsync();

        // GH-5077: what the act's own code sends through the bus it resolves is its own message
        var messages = new ActMessages();
        _actMessages = messages;
        var outcome = await executeAsync(
            tracking => tracking.ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await using var scope = Services.CreateAsyncScope();
                await act(messages.Wrap(scope.ServiceProvider));
            })),
            null);

        if (marks.Count == 0) return outcome;

        var unheard = marks.Where(mark => !outcome.Appended.Any(commit => heardOn(commit, mark.Store, marks))).ToList();
        if (unheard.Count == 0) return outcome;

        // Read even after a failure: an act can commit and then throw, and what it committed is a fact
        return mergeReadBack(outcome, await readBackSinceAsync(unheard, outcome.Error), marks);
    }

    // Did this commit happen on this store? A commit whose integration did not say which store goes to the default
    // store, and on a host with one store every commit is that store's
    private bool heardOn(AppendedEvents commit, IEventStore store, IReadOnlyList<StoreHighWaterMark> marks)
    {
        if (marks.Count == 1) return true;
        if (commit.Store is { } identity) return Equals(identity, store.Identity);
        return hasStore() && ReferenceEquals(EventStore, store);
    }

    private ActOutcome mergeReadBack(ActOutcome outcome, CritterStackActOutcome readBack, IReadOnlyList<StoreHighWaterMark> marks)
    {
        if (readBack.AllNewEvents.Count == 0 && readBack.ForeignEvents.Count == 0) return outcome;

        // Which store every event came from, when there was a choice: what the tracked session heard, by the
        // store each commit names, and what was read back, by the store it was read from
        IReadOnlyDictionary<IEvent, IEventStore> byEvent = CritterStackActOutcome.NoStores;
        if (marks.Count > 1)
        {
            var map = new Dictionary<IEvent, IEventStore>(ReferenceEqualityComparer.Instance);
            foreach (var commit in outcome.Appended)
            {
                var store = marks.FirstOrDefault(x => heardOn(commit, x.Store, marks))?.Store;
                if (store is null) continue;
                foreach (var e in commit.Events) map[e] = store;
            }

            foreach (var (e, store) in readBack.StoreByEvent) map[e] = store;
            byEvent = map;
        }

        var merged = outcome with
        {
            NewEvents = outcome.NewEvents.Concat(readBack.NewEvents).ToList(),
            AllNewEvents = outcome.AllNewEvents.Concat(readBack.AllNewEvents).ToList(),
            ForeignEvents = readBack.ForeignEvents,
            StartedStreams = outcome.StartedStreams.Concat(readBack.StartedStreams).ToList(),
            // Read back from the store, which returns no tags
            EventTagsKnown = outcome.EventTagsKnown && readBack.NewEvents.Count == 0,
            StoreByEvent = byEvent
        };

        recordTouched(readBack.NewEvents.Select(x => x.Data));
        if (SpecReport.IsRecording && readBack.NewEvents.Count > 0) SpecReport.For<AppendedEventsReport>().Add(readBack.NewEvents);

        return merged;
    }

    /// <summary>
    /// The act every <c>When</c> step goes through: warm the handlers, take the stream's "before",
    /// dispatch inside a tracked session, and capture what changed. Nothing it captures is thrown.
    /// </summary>
    /// <param name="stepText">How the step reads.</param>
    /// <param name="dispatch">Runs the act inside the configured tracked session.</param>
    /// <param name="complete">Refines the outcome once the session has settled — the HTTP act reads its response here.</param>
    protected internal Task ActAsync(string stepText,
        Func<TrackedSessionConfiguration, Task<ITrackedSession>> dispatch,
        Func<ActOutcome, Task<ActOutcome>>? complete = null)
        => root.actAsync(stepText, null, dispatch, complete);

    /// <summary>
    /// <see cref="ActAsync(string, Func{TrackedSessionConfiguration, Task{ITrackedSession}}, Func{ActOutcome, Task{ActOutcome}}?)" />
    /// for an act that carries a command or message: <paramref name="stepFormat" />'s <c>{0}</c> is
    /// <paramref name="subject" /> in full when it fits on the line, or its type name with its values
    /// in a table under the step when it does not.
    /// </summary>
    protected internal Task ActAsync(string stepFormat, object subject,
        Func<TrackedSessionConfiguration, Task<ITrackedSession>> dispatch,
        Func<ActOutcome, Task<ActOutcome>>? complete = null)
        => root.actAsync(stepFormat, subject, dispatch, complete);

    private async Task actAsync(string stepText, object? subject,
        Func<TrackedSessionConfiguration, Task<ITrackedSession>> dispatch,
        Func<ActOutcome, Task<ActOutcome>>? complete)
    {
        var inline = true;
        if (subject is not null)
        {
            (var described, inline) = describe([subject]);
            stepText = string.Format(CultureInfo.InvariantCulture, stepText, described);
        }

        // The act's own exception is captured, never thrown; anything else that escapes fails the step's own line
        await inStep(ScenarioRecorder.Step("When", stepText), async () =>
        {
            if (!inline) recordValues("message", [subject!]);

            // The command the act dispatched was touched, whatever it then did (bobcat#465)
            if (_dispatched is { } dispatched && !ReferenceEquals(dispatched, NoMessage)) recordTouched(dispatched.GetType());

            await executeAsync(dispatch, complete);
        });
    }

    /// <summary>
    /// Run a step's body with its step open: an exception that escapes fails the step's own line, then carries
    /// on to the runner. A bare <c>using var step</c> ends as a pass whatever escapes it.
    /// </summary>
    private static async Task inStep(IDisposable step, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception e)
        {
            (step as IStepHandle)?.Fail(e);
            throw;
        }
        finally
        {
            step.Dispose();
        }
    }

    // Run the act in a tracked session and capture what it did into LastAct. Records no step of its own.
    private async Task<ActOutcome> executeAsync(
        Func<TrackedSessionConfiguration, Task<ITrackedSession>> dispatch,
        Func<ActOutcome, Task<ActOutcome>>? complete)
    {
        HandlerWarmUp.WarmBeforeTracking(Services);

        // GH-4931: claimed before the act, so a step that throws before dispatching leaves nothing behind
        var dispatched = _dispatched;
        _dispatched = null;
        var actMessages = _actMessages;
        _actMessages = null;

        // GH-4939: every act runs under a correlation id of its own, so what it caused past an asynchronous
        // boundary can be found in the store by it. A configureTracking that names its own still wins.
        var correlationId = Guid.NewGuid().ToString();
        ActCorrelationId = correlationId;

        var tracking = trackActivity().WithCorrelationId(correlationId);

        // GH-4940: arm the concurrent writer for this act alone, by its flow and its correlation id
        var concurrent = _concurrently;
        _concurrently = null;
        IDisposable? armed = null;
        if (concurrent is not null)
        {
            var store = await storeForAsync("a concurrent writer appends", null, concurrent);
            armed = ConcurrentAppends.Arm(correlationId, token =>
                EventStoreAuthoring.AppendAsync(store, null!, Guid.CreateVersion7(), concurrent, token));
        }

        ITrackedSession? session = null;
        Exception? error = null;
        try
        {
            session = await dispatch(tracking);
            error = session.AllExceptions().FirstOrDefault();
        }
        catch (Exception e)
        {
            error = e;
        }
        finally
        {
            armed?.Dispose();
        }

        // GH-4940: whether the concurrent append landed is the tracked session's to say
        bool? landed = armed is null
            ? null
            : session?.ConcurrentAppends.Any(x => x.CorrelationId == correlationId) ?? false;

        // An act that saved nothing never let the concurrent writer in: say so, instead of a vacuous pass
        if (landed == false && error is null)
        {
            error = new SpecificationFailedException(
                "WhileActing armed a concurrent append, but no session Wolverine opened for the act saved anything, so the " +
                "append never landed between its read and its write. It lands on the first save of a session carrying the " +
                "act's correlation id: a handler's or an HTTP endpoint's session, with the store integrated with Wolverine " +
                "(IntegrateWithWolverine()). A session the act opens straight from the store carries none.");
        }

        // GH-4940: executions of the act's own message, a retry's included
        var attempts = session?.AllRecordsInOrder(MessageEventType.ExecutionStarted)
            .Where(x => x.Envelope is not null && isMessage(dispatched) && ReferenceEquals(x.Envelope.Message, dispatched))
            .Count() ?? 0;

        // GH-5077: one rule for every act -- its own messages only scheduled, never handled: a TimeoutMessage, a DelayedFor
        // wrapper's message, a delay in the options, a ScheduleAsync in WhenTracked. Not an error: Wolverine did what it
        // was asked. But no handler ran, so a step about what one did must not pass on it
        var ownMessages = isMessage(dispatched)
            ? [messageInside(dispatched!)]
            : actMessages?.Sent.Select(messageInside).Distinct(ReferenceEqualityComparer.Instance).ToList() ?? [];
        var onlyScheduled = session is null ? Array.Empty<object>() : onlyScheduledOf(session, ownMessages);

        // GH-4931: what the act appended is what the tracked session heard each event-store session commit --
        // never read back from the store, so concurrent work is never counted and the events keep their tags
        var commits = session?.AppendedEvents ?? Array.Empty<AppendedEvents>();
        var own = commits.Where(x => isTheActsOwn(x, dispatched)).ToList();
        var appended = own.SelectMany(x => x.Events).ToList();
        var all = commits.SelectMany(x => x.Events).ToList();

        var outcome = new ActOutcome(session, appended, error)
        {
            AllNewEvents = all,
            Appended = commits,
            StartedStreams = own.SelectMany(x => x.StartedStreams).ToList(),
            // GH-5077: sent, never scheduled -- a scheduled message is ThenMessageScheduled's
            MessagesSent = session is null ? Array.Empty<object>() : sentIn(session, ownMessages).Select(x => x.Message!).ToList(),
            OnlyScheduled = onlyScheduled,
            OwnMessages = ownMessages,
            // GH-4940: the boundaries the act's own handler loaded, by the same rule as its own commits
            Boundaries = (session?.LoadedBoundaries ?? Array.Empty<LoadedBoundary>())
                .Where(x => isTheActsOwn(x.Envelope, dispatched)).ToList(),
            CorrelationId = session?.CorrelationId ?? correlationId,
            Attempts = attempts,
            ConcurrentAppendLanded = landed,
            TenantId = tenantOf(session, own, dispatched)
        };
        if (complete is not null) outcome = await complete(outcome);
        LastAct = outcome;
        ActCorrelationId = outcome.CorrelationId;

        // bobcat#465: what the act observably touched -- the events it appended, the messages it sent
        recordTouched(appended.Select(x => x.Data));
        recordTouched(outcome.MessagesSent);

        if (SpecReport.IsRecording)
        {
            if (session is not null) SpecReport.For<MessageActivityReport>().Add(session, ownMessages);
            if (appended.Count > 0) SpecReport.For<AppendedEventsReport>().Add(appended);
        }

        return outcome;
    }

    // The tracked session every act and every arranging message runs in
    private TrackedSessionConfiguration trackActivity()
    {
        var tracking = (_host is null ? Services.TrackActivity() : _host.TrackActivity()).Timeout(Timeout).DoNotAssertOnExceptionsDetected();
        return IncludeExternalTransports ? tracking.IncludeExternalTransports() : tracking;
    }

    // GH-5077: the act's own messages that were scheduled and never began executing. Which messages are the act's own
    // is decided where the act dispatches them -- WhenReceived's message, or what the act's own code sent through the
    // bus it was handed -- never inferred from the session, where a handler's or an HTTP endpoint's sends look the same
    private static IReadOnlyList<object> onlyScheduledOf(ITrackedSession session, IReadOnlyList<object> own)
    {
        var scheduled = scheduledIn(session).Select(x => x.Message).ToHashSet(ReferenceEqualityComparer.Instance);
        var started = session.AllRecordsInOrder(MessageEventType.ExecutionStarted).Select(x => x.Envelope?.Message)
            .OfType<object>().ToHashSet(ReferenceEqualityComparer.Instance);

        return own.Where(x => scheduled.Contains(x) && !started.Contains(x)).ToList();
    }

    // The message a DelayedFor / ScheduledAt / WithDeliveryOptions wrapper carries, which is what Wolverine sends; any
    // other message is itself
    private static object messageInside(object message)
    {
        for (var type = message.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DeliveryMessage<>))
                return type.GetProperty(nameof(DeliveryMessage<object>.Message))!.GetValue(message) ?? message;
        }

        return message;
    }

    // Whether an act dispatched a message of its own, which an HTTP request and WhenTracked do not
    private static bool isMessage(object? dispatched) => dispatched is not null && !ReferenceEquals(dispatched, NoMessage);

    // GH-4962: the act's tenant, from the act itself. Its own commits first -- an HTTP endpoint's tenant detection
    // lands there, and so does the entry message's tenant -- then the entry message's envelope, then the one tenant
    // every envelope in the session carried. Several candidates answer nothing rather than guess
    private static string? tenantOf(ITrackedSession? session, IReadOnlyList<AppendedEvents> own, object? dispatched)
    {
        static string? single(IEnumerable<string?> tenants)
        {
            var distinct = tenants.Where(x => !string.IsNullOrEmpty(x) && x != DefaultTenant).Distinct(StringComparer.Ordinal).ToList();
            return distinct.Count == 1 ? distinct[0] : null;
        }

        if (single(own.Select(x => x.TenantId)) is { } committed) return committed;
        if (session is null) return null;

        var records = session.AllRecordsInOrder().Where(x => x.Envelope is not null).ToList();
        if (dispatched is not null && !ReferenceEquals(dispatched, NoMessage) &&
            single(records.Where(x => ReferenceEquals(x.Envelope!.Message, dispatched)).Select(x => x.Envelope!.TenantId)) is { } entry)
        {
            return entry;
        }

        return single(records.Select(x => x.Envelope!.TenantId));
    }

    // The default tenant's name in the stores, which is no tenant at all to an act
    private const string DefaultTenant = "*DEFAULT*";

    // GH-4931: the act's own commits are those of the sessions that handled the act itself. An HTTP endpoint
    // has no envelope; a dispatched message is its envelope's message, and a cascade's envelope carries a
    // message of its own. An act that dispatches nothing Bobcat can see -- WhenTracked -- owns everything.
    private static bool isTheActsOwn(AppendedEvents commit, object? dispatched) => isTheActsOwn(commit.Envelope, dispatched);

    private static bool isTheActsOwn(Envelope? envelope, object? dispatched)
        => dispatched is null || envelope is null || ReferenceEquals(envelope.Message, dispatched);

    // ---- assert: events past an asynchronous boundary (GH-4939) ---------------------------------

    /// <summary>
    /// GH-4939. The act <em>eventually</em> caused these events, among whatever else it caused -- including those
    /// appended past an asynchronous boundary the tracked session cannot see, such as a message an event
    /// subscription published, or an async projection's side effect. Whole objects, <see cref="Expect.Value{T}" />s
    /// or partial objects, judged as <see cref="ThenEmitted(object[])" /> judges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every act runs under a correlation id of its own (<see cref="ActOutcome.CorrelationId" />). Once the async
    /// daemons have caught up, this step queries the event store for every event recorded with that correlation
    /// id, through <c>IEventStore.OpenReadOnlyEventStore().QueryEventsAsync</c>, and looks again until
    /// <see cref="EventuallyTimeout" /> for work that lands after the daemons, such as a handler on a local queue.
    /// </para>
    /// <para>
    /// The store must record correlation ids, which Marten, Polecat and Fisher all leave off by default. When it
    /// does not, the step fails and says how to turn them on. The correlation only reaches an event when every
    /// hop carries it: Wolverine's handlers do, but an event subscription or a projection's side effect that
    /// publishes a message has to pass the event's <c>CorrelationId</c> on itself.
    /// </para>
    /// <para>
    /// The act's tenant -- the entry message's <see cref="Envelope.TenantId" /> or an HTTP endpoint's tenant detection --
    /// scopes the search: within one database on a conjoined store, and to the tenant's own database on a store that
    /// spreads its tenants across several. An act with no tenant on such a store is looked for in every database.
    /// </para>
    /// </remarks>
    public Task ThenEventuallyCaused(params object[] events)
        => verifyEventuallyCaused(flatten(events));

    /// <summary>
    /// GH-4939. The act eventually caused a <typeparamref name="T" />, whatever its values -- see
    /// <see cref="ThenEventuallyCaused(object[])" />.
    /// </summary>
    public Task ThenEventuallyCaused<T>() => verifyEventuallyCaused([anyOf(typeof(T))]);

    private async Task verifyEventuallyCaused(object[] expected)
    {
        var text = $"{describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray())} {(expected.Length == 1 ? "is" : "are")} eventually emitted";
        // The store reads can throw something other than what the step reports itself: that fails the step's line
        await inStep(ScenarioRecorder.Step("Then", text), () => judgeEventuallyCaused(text, expected));
    }

    private async Task judgeEventuallyCaused(string text, object[] expected)
    {
        if (!actSucceeded()) return;

        if (LastAct.CorrelationId is not { } correlationId)
        {
            Verdicts.Fail("There is no act to look for the events of: ThenEventuallyCaused follows a When step.");
            return;
        }

        if (storesToObserve().Count == 0 && !hasStore())
        {
            Verdicts.Fail("ThenEventuallyCaused queries the event store, but the application has none.");
            return;
        }

        // GH-4977 (bobcat#504): the store whose registrations claim the expected events, on a host with several --
        // or the store a view from In<T>() is pinned to
        var store = await storeForAsync(text, null, expected);

        // GH-4962: the act's tenant, from the act -- the entry message's envelope or an HTTP endpoint's tenant
        // detection. On one database (conjoined tenancy) the query is filtered by it (phase 1). On a store that spreads
        // its tenants across databases it picks the database (phase 2): the wait asks the store which database that
        // is and waits on every database when it cannot say, and the read opens the tenant's own database. An act
        // with no tenant on such a store is looked for in every database, which is still exact because a
        // correlation id belongs to one act
        var tenantId = LastAct.TenantId;
        var severalDatabases = store.DatabaseCardinality is DatabaseCardinality.StaticMultiple or DatabaseCardinality.DynamicMultiple;
        var filterByTenant = tenantId is not null && store.HasMultipleTenants && !severalDatabases;

        var deadline = DateTimeOffset.UtcNow + EventuallyTimeout;
        while (true)
        {
            try
            {
                if (severalDatabases && tenantId is not null)
                {
                    await EventStores.WaitForNonStaleProjectionsAsync(store, tenantId, ProjectionTimeout);
                }
                else
                {
                    await EventStores.WaitForNonStaleProjectionsAsync(store, ProjectionTimeout);
                }
            }
            catch (TimeoutException e)
            {
                Verdicts.Fail($"The async daemons did not catch up, so the events the act caused could not be looked for: {e.Message}");
                return;
            }

            IReadOnlyList<IEvent> caused;
            string where;
            try
            {
                (caused, where) = severalDatabases
                    ? tenantId is not null
                        ? (await eventsCorrelatedWith(store, correlationId, tenantId, filterByTenant: false),
                            $" in tenant '{tenantId}''s database")
                        : await eventsCorrelatedInEveryDatabase(store, correlationId)
                    : (await eventsCorrelatedWith(store, correlationId, tenantId, filterByTenant),
                        filterByTenant ? $" for tenant '{tenantId}'" : "");
            }
            catch (NotSupportedException e) when (e.Message.Contains("Correlation", StringComparison.OrdinalIgnoreCase))
            {
                Verdicts.Fail(correlationIdsAreOff(store));
                return;
            }

            var data = caused.Select(x => x.Data).ToArray();
            var tags = tagsOfCaused(caused);
            var run = ObjectSetVerification.Verify(data, expected, "event", SetMode.Contains, tags);
            if (run.Succeeded || DateTimeOffset.UtcNow >= deadline)
            {
                if (Verdicts.Recording) run.Report();

                Verdicts.Fact(run.Succeeded, string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, "event")) +
                                             $"{Environment.NewLine}The act's correlation id {correlationId} found {(data.Length == 0 ? "no events" : ScenarioValues.DescribeAll(data))} in {describeStore(store)}" +
                                             where + "." +
                                             // GH-4977: a whole-store read, so say what else the act saw, store by store
                                             crossTalkSuffix() + storesSuffix());
                return;
            }

            await Task.Delay(100);
        }
    }

    // Every event the store recorded with this correlation id, oldest first, every page of it
    // GH-4962: read through the act's tenant -- a tenant within the database on a conjoined store, where the query is
    // filtered by it too, and the tenant's own database on a store that spreads its tenants across several
    private static async Task<IReadOnlyList<IEvent>> eventsCorrelatedWith(IEventStore eventStore, string correlationId,
        string? tenantId, bool filterByTenant)
    {
        var store = eventStore.OpenReadOnlyEventStore(tenantId);
        var events = new List<IEvent>();
        var query = new EventQuery { CorrelationId = correlationId, TenantId = filterByTenant ? tenantId : null, PageSize = 500 };
        while (true)
        {
            var page = await store.QueryEventsAsync(query);
            events.AddRange(page.Events);
            if (page.Events.Count == 0 || events.Count >= page.TotalCount) return events;
            query.PageNumber++;
        }
    }

    // GH-4962: an act with no tenant on a store that spreads its tenants across databases. Each database is read through
    // one of the tenants the store says lives in it, since a reader is opened by tenant; a database the store names no
    // tenant for cannot be read this way, and the message says how many were
    private static async Task<(IReadOnlyList<IEvent> Events, string Where)> eventsCorrelatedInEveryDatabase(
        IEventStore eventStore, string correlationId)
    {
        var usage = await eventStore.TryCreateUsage(CancellationToken.None);
        var databases = usage?.Database is null
            ? []
            : usage.Database.Databases.Append(usage.Database.MainDatabase).OfType<DatabaseDescriptor>()
                .DistinctBy(x => (x.ServerName, x.DatabaseName)).ToList();

        var events = new List<IEvent>();
        var unreadable = 0;
        foreach (var database in databases)
        {
            if (database.TenantIds.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) is not { } tenant)
            {
                unreadable++;
                continue;
            }

            events.AddRange(await eventsCorrelatedWith(eventStore, correlationId, tenant, filterByTenant: false));
        }

        var where = $" across its {databases.Count - unreadable} tenant database{(databases.Count - unreadable == 1 ? "" : "s")} (the act carried no tenant)" +
                    (unreadable == 0 ? "" : $"; {unreadable} database{(unreadable == 1 ? "" : "s")} with no known tenant could not be read");
        return (events, where);
    }

    // No store reads tags back, so an event the act's own sessions committed keeps the tags it was appended with
    private Func<object, IReadOnlyList<object>?> tagsOfCaused(IReadOnlyList<IEvent> caused)
    {
        var appended = LastAct.AllNewEvents.Where(x => x.Tags is { Count: > 0 }).GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<object>)x.First().Tags!.Select(t => t.Value).ToArray());
        var tags = new Dictionary<object, IReadOnlyList<object>>(ReferenceEqualityComparer.Instance);
        foreach (var e in caused)
        {
            tags[e.Data] = appended.GetValueOrDefault(e.Id) ?? e.Tags?.Select(x => x.Value).ToArray() ?? Array.Empty<object>();
        }

        return data => tags.GetValueOrDefault(data);
    }

    // Which store this is, by the assembly that implements it, for a message that says what to change
    private static string storeKind(IEventStore store) => store.GetType().Assembly.GetName().Name switch
    {
        "Marten" => "Marten",
        "Polecat" => "Polecat",
        "Fisher" => "Fisher",
        var other => other ?? store.GetType().Name
    };

    private static string describeStore(IEventStore store)
    {
        string? name = null;
        try
        {
            name = store.Identity.Name;
        }
        catch (Exception)
        {
            // an identity is a nicety in a message, never a reason for the step to fail
        }

        return name is null ? $"the {storeKind(store)} event store" : $"the {storeKind(store)} event store '{name}'";
    }

    private static string correlationIdsAreOff(IEventStore store)
    {
        var turnOn = storeKind(store) switch
        {
            "Marten" => "Turn them on in AddMarten(): opts.Events.MetadataConfig.CorrelationIdEnabled = true;",
            "Polecat" => "Turn them on in AddPolecat(): opts.Events.EnableCorrelationId = true;",
            "Fisher" => "Turn them on in AddFisher(): opts.Events.EnableCorrelationId = true;",
            _ => "Turn on the store's correlation id metadata."
        };

        return $"ThenEventuallyCaused finds the act's events by its correlation id, but {describeStore(store)} does not record " +
               $"correlation ids, which is the default. {turnOn} Then the store's schema needs the new column, which " +
               "automatic schema creation or a migration adds.";
    }

    // ---- assert: the consistency boundary (GH-4940) --------------------------------------------

    /// <summary>
    /// GH-4940. The act loaded exactly one Dynamic Consistency Boundary, and its tag query is exactly
    /// <paramref name="expected" />: the same conditions — each a tag, and the event type it narrows to or
    /// none — in any order, none missing and none extra. Whatever the query selects is what a concurrent
    /// append is checked against, so this is the step that says what the decision is consistent over.
    /// </summary>
    /// <remarks>
    /// Read from what the act's own handler loaded while the tracked session ran, never from the store, and
    /// judged whether the act succeeded or refused: a refusal decided over the same boundary. An act that
    /// loaded several boundaries names the one it means with <see cref="ThenTheConsistencyBoundaryIs{TModel}" />.
    /// </remarks>
    public void ThenTheConsistencyBoundaryIs(EventTagQuery expected)
        => verifyBoundary(null, expected);

    /// <summary>
    /// GH-4940. The Dynamic Consistency Boundary the act loaded for <typeparamref name="TModel" /> is exactly
    /// <paramref name="expected" />, as <see cref="ThenTheConsistencyBoundaryIs(EventTagQuery)" /> judges it —
    /// for an act that loads more than one boundary.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(TModel))]
    public void ThenTheConsistencyBoundaryIs<TModel>(EventTagQuery expected)
        => verifyBoundary(typeof(TModel), expected);

    /// <summary>
    /// GH-4940. Another writer appending <paramref name="event" />, with the tags it names, between the act's
    /// read and its write would have made the act fail with a concurrency conflict: the event falls inside a
    /// boundary the act loaded. Decided by matching the event's type and tags against the recorded tag
    /// queries, deterministically — nothing is appended and nothing races.
    /// </summary>
    /// <param name="event">
    /// The hypothetical event with its tags: <c>Tagged(new SeatReserved(...), theScreeningId)</c>, or
    /// <c>Specify&lt;SeatReserved&gt;().Tagged(theScreeningId)</c>. Only its type and tags count.
    /// </param>
    public void ThenAConcurrentEventWouldConflict(object @event)
        => verifyConcurrent(PartialMatching.ExpectedType(@event), Tagging.ExpectedTagsOf(@event), true);

    /// <summary>
    /// GH-4940. A concurrent <typeparamref name="TEvent" /> carrying <paramref name="tags" /> would have made
    /// the act fail with a concurrency conflict. See <see cref="ThenAConcurrentEventWouldConflict(object)" />.
    /// </summary>
    public void ThenAConcurrentEventWouldConflict<TEvent>(params object[] tags)
        => verifyConcurrent(typeof(TEvent), tags, true);

    /// <summary>
    /// GH-4940. Another writer appending <paramref name="event" />, with the tags it names, between the act's
    /// read and its write would <b>not</b> conflict with it: the event falls outside every boundary the act
    /// loaded. The other half of <see cref="ThenAConcurrentEventWouldConflict(object)" />, decided the same way.
    /// </summary>
    public void ThenAConcurrentEventWouldNotConflict(object @event)
        => verifyConcurrent(PartialMatching.ExpectedType(@event), Tagging.ExpectedTagsOf(@event), false);

    /// <summary>
    /// GH-4940. A concurrent <typeparamref name="TEvent" /> carrying <paramref name="tags" /> would not
    /// conflict with the act. See <see cref="ThenAConcurrentEventWouldNotConflict(object)" />.
    /// </summary>
    public void ThenAConcurrentEventWouldNotConflict<TEvent>(params object[] tags)
        => verifyConcurrent(typeof(TEvent), tags, false);

    private void verifyBoundary(Type? model, EventTagQuery expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        var subject = model is null ? "the consistency boundary" : $"the {model.Name} consistency boundary";
        using var step = ScenarioRecorder.Step("Then", $"{subject} is {EventStores.Describe(expected)}");

        if (!tryOneBoundary(model, out var boundary)) return;

        var expectedConditions = expected.Conditions.Distinct().ToList();
        var actualConditions = boundary!.Conditions.Distinct().ToList();
        var missing = expectedConditions.Where(x => !actualConditions.Contains(x)).ToList();
        var extra = actualConditions.Where(x => !expectedConditions.Contains(x)).ToList();

        Verdicts.Check("boundary", describeBoundary(actualConditions), EventStores.Describe(expected));

        var problems = new List<string>();
        if (missing.Count > 0) problems.Add($"MISSING {string.Join(", ", missing.Select(describeCondition))}");
        if (extra.Count > 0) problems.Add($"UNEXPECTED {string.Join(", ", extra.Select(describeCondition))}");

        Verdicts.Fact(problems.Count == 0,
            $"Expected {subject} to be {EventStores.Describe(expected)}, but the act loaded {boundary.ModelType.Name} over {describeBoundary(actualConditions)}: {string.Join("; ", problems)}");
    }

    private bool tryOneBoundary(Type? model, out LoadedBoundary? boundary)
    {
        boundary = null;
        var candidates = model is null
            ? LastAct.Boundaries
            : LastAct.Boundaries.Where(x => x.ModelType == model).ToList();

        if (candidates.Count == 1)
        {
            boundary = candidates[0];
            return true;
        }

        if (candidates.Count == 0)
        {
            var loaded = LastAct.Boundaries.Count == 0
                ? "the act loaded no consistency boundary"
                : $"the act loaded only {describeBoundaries(LastAct.Boundaries)}";
            return Verdicts.Fail(model is null
                ? $"Expected a consistency boundary, but {loaded}{failureSuffix()}"
                : $"Expected a {model.Name} consistency boundary, but {loaded}{failureSuffix()}");
        }

        return Verdicts.Fail(model is null
            ? $"The act loaded {candidates.Count} consistency boundaries, {describeBoundaries(candidates)}. Name the one you mean with ThenTheConsistencyBoundaryIs<TModel>(...)"
            : $"The act loaded {candidates.Count} {model.Name} consistency boundaries, {describeBoundaries(candidates)}");
    }

    private void verifyConcurrent(Type eventType, IReadOnlyList<object> tags, bool conflicts)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count == 0)
        {
            // An untagged event is outside every boundary, so either answer would say nothing about the act
            throw new ArgumentException(
                $"Name the tags a concurrent {eventType.Name} would carry: Tagged(new {eventType.Name}(...), tag), Specify<{eventType.Name}>().Tagged(tag) or ThenAConcurrentEventWould...<{eventType.Name}>(tag)",
                nameof(tags));
        }

        var text = $"a concurrent {eventType.Name} tagged {Tagging.Describe(tags)} would {(conflicts ? "" : "not ")}conflict";
        using var step = ScenarioRecorder.Step("Then", text);

        if (LastAct.Boundaries.Count == 0)
        {
            Verdicts.Fail($"The act loaded no consistency boundary, so nothing it read can conflict{failureSuffix()}");
            return;
        }

        var inside = LastAct.Boundaries.Where(x => x.Selects(eventType, tags)).ToList();
        Verdicts.Check("conflicts", inside.Count > 0, conflicts);
        Verdicts.Value("inside", inside.Count == 0 ? "no boundary" : describeBoundaries(inside));

        Verdicts.Fact(conflicts == inside.Count > 0, conflicts
            ? $"Expected a concurrent {eventType.Name} tagged {Tagging.Describe(tags)} to conflict, but it is outside {describeBoundaries(LastAct.Boundaries)}"
            : $"Expected a concurrent {eventType.Name} tagged {Tagging.Describe(tags)} not to conflict, but it is inside {describeBoundaries(inside)}");
    }

    private static string describeBoundaries(IEnumerable<LoadedBoundary> boundaries)
        => string.Join("; ", boundaries.Select(x => $"{x.ModelType.Name} over {describeBoundary(x.Conditions)}"));

    private static string describeBoundary(IEnumerable<EventTagQueryCondition> conditions)
        => EventStores.Describe(EventTagQuery.FromConditions(conditions));

    private static string describeCondition(EventTagQueryCondition condition)
        => condition.EventType is null
            ? $"events tagged {ScenarioValues.Format(condition.TagValue)}"
            : $"{condition.EventType.Name} tagged {ScenarioValues.Format(condition.TagValue)}";

    // ---- assert: cascaded messages (GH-4837) -------------------------------------------------
    //
    // GH-5077: sent and scheduled are two grammars over two disjoint sets. A message scheduled for later -- even one
    // a transport records as sent -- is only ever ThenMessageScheduled's, and a sent one only ThenMessageSent's

    /// <summary>
    /// The act sent or published a <typeparamref name="T" /> — to <paramref name="destination" /> when
    /// one is given, matched as a prefix of the destination URI (<c>"rabbitmq://queue/billing"</c>,
    /// <c>"local://"</c>). A message scheduled for later is not sent: see <see cref="ThenMessageScheduled{T}" />.
    /// </summary>
    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessageSent<T>(string? destination = null)
    {
        using var step = ScenarioRecorder.Step("Then",
            destination is null ? $"{typeof(T).Name} is sent" : $"{typeof(T).Name} is sent to \"{destination}\"");
        if (!actSucceeded()) return;

        var sent = sentOf(typeof(T)).ToArray();
        var matching = sent.Where(x => destinationMatches(x, destination)).ToArray();

        Verdicts.Check("sent", matching.Length > 0 ? "yes" : "no", "yes");
        messageFact(matching.Length > 0, sent.Length == 0
            ? $"Expected a {typeof(T).Name} to be sent, but none was{(scheduledOf(typeof(T)).Any() ? " (one was SCHEDULED for later: use ThenMessageScheduled)" : "")}."
            : $"Expected a {typeof(T).Name} to be sent to {destination}, but it went to {string.Join(", ", sent.Select(x => x.Destination?.ToString() ?? "(no destination)"))}.");
    }

    /// <summary>The act sent a <typeparamref name="T" /> to a local queue in this application.</summary>
    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessageSentLocally<T>() => ThenMessageSent<T>("local://");

    /// <summary>The act sent a <typeparamref name="T" /> to an external transport — anything but a local queue.</summary>
    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessageSentExternally<T>()
    {
        using var step = ScenarioRecorder.Step("Then", $"{typeof(T).Name} is sent to an external transport");
        if (!actSucceeded()) return;

        var external = sentOf(typeof(T)).Where(x => x.Destination?.Scheme is { } scheme && scheme != "local").ToArray();
        messageFact(external.Length > 0, $"Expected a {typeof(T).Name} to be sent to an external transport.");
    }

    /// <summary>
    /// The act sent or published a message structurally equal to <paramref name="expected" /> — or an
    /// <see cref="Expect.Value{T}" /> with members to ignore, or a partial object judged on only the members it names.
    /// </summary>
    [EventModelRole(SliceRole.Message, nameof(expected))]
    public void ThenMessageSent(object expected)
    {
        if (expected is IPartialObject partial)
        {
            using var partialStep = ScenarioRecorder.Step("Then", $"{partial.Type.Name} is sent");
            if (!actSucceeded()) return;
            judgeMessage(partial, sentOf(partial.Type).Select(x => x.Message!).ToArray(), "sent",
                scheduledOf(partial.Type).Any() ? " (one was SCHEDULED for later: use ThenMessageScheduled)" : "");
            return;
        }

        var value = unwrap(expected);
        using var step = ScenarioRecorder.Step("Then", $"{value.GetType().Name} is sent");
        if (!actSucceeded()) return;

        judgeMessage(expected, sentOf(value.GetType()).Select(x => x.Message!).ToArray(), "sent",
            scheduledOf(value.GetType()).Any() ? " (one was SCHEDULED for later: use ThenMessageScheduled)" : "");
    }

    /// <summary>The act sent no <typeparamref name="T" /> anywhere.</summary>
    public void ThenNoMessageSent<T>()
    {
        using var step = ScenarioRecorder.Step("Then", $"no {typeof(T).Name} is sent");
        if (!canJudgeAnAbsence($"Expected no {typeof(T).Name} to be sent")) return;

        var sent = sentOf(typeof(T)).ToArray();
        messageFact(sent.Length == 0,
            $"Expected no {typeof(T).Name} but {sent.Length} were sent, to {string.Join(", ", sent.Select(x => x.Destination?.ToString() ?? "(no destination)"))}.");
    }

    /// <summary>
    /// Every message the act sent or published, for a test that wants to look for itself. A message scheduled for
    /// later is not among them.
    /// </summary>
    public IReadOnlyList<object> TheMessagesSent
        => LastAct.Session is { } session ? sentIn(session, LastAct.OwnMessages).Select(x => x.Message!).ToList() : Array.Empty<object>();

    // ---- assert: scheduled messages (GH-5077) --------------------------------------------------

    /// <summary>
    /// The act scheduled a <typeparamref name="T" /> for later — <c>DelayedFor</c>, <c>ScheduledAt</c>,
    /// <c>OutgoingMessages.Delay</c>, a <see cref="TimeoutMessage" />. A scheduled message is not sent within the act,
    /// so <see cref="ThenMessageSent{T}" /> does not see it. With <paramref name="delay" />, the message is due no
    /// sooner than that long after the act. <see cref="WhenScheduledMessagesAreExecuted" /> runs it.
    /// </summary>
    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessageScheduled<T>(TimeSpan? delay = null)
    {
        using var step = ScenarioRecorder.Step("Then",
            delay is null ? $"{typeof(T).Name} is scheduled" : $"{typeof(T).Name} is scheduled for {delay}");
        if (!actSucceeded()) return;

        var scheduled = scheduledOf(typeof(T)).ToArray();
        Verdicts.Check("scheduled", scheduled.Length > 0 ? "yes" : "no", "yes");
        if (!messageFact(scheduled.Length > 0,
                $"Expected a {typeof(T).Name} to be scheduled, but none was{(sentOf(typeof(T)).Any() ? " (one was SENT within the act: use ThenMessageSent)" : "")}.")) return;

        if (delay is { } minimum && scheduled[0].ScheduledTime is { } due)
        {
            // measured from now, after the act: the message was due at least `minimum` after it was scheduled
            var remaining = due - DateTimeOffset.UtcNow;
            Verdicts.Value("due", due);
            messageFact(remaining > minimum - TimeSpan.FromMinutes(1),
                $"Expected {typeof(T).Name} to be due in about {minimum}, but it is due at {due:O}.");
        }
    }

    /// <summary>
    /// GH-5076. The act scheduled a message structurally equal to <paramref name="expected" /> for later — or an
    /// <see cref="Expect.Value{T}" /> with members to ignore, or a partial object judged on only the members it names.
    /// Its values are judged as <see cref="ThenMessageSent(object)" /> judges a sent message's;
    /// <see cref="ThenMessageScheduled{T}" /> checks when it is due.
    /// </summary>
    [EventModelRole(SliceRole.Message, nameof(expected))]
    public void ThenMessageScheduled(object expected)
    {
        var type = PartialMatching.ExpectedType(expected);
        var (described, inline) = describe([expected is IPartialObject ? expected : unwrap(expected)]);
        using var step = ScenarioRecorder.Step("Then", $"{(inline ? described : nameOf(type))} is scheduled");
        if (!actSucceeded()) return;

        judgeMessage(expected, scheduledOf(type).Select(x => x.Message!).ToArray(), "scheduled",
            sentOf(type).Any() ? " (one was SENT within the act: use ThenMessageSent)" : "");
    }

    /// <summary>
    /// GH-5077. The act scheduled exactly these messages for later, in any order, and nothing else — whole objects,
    /// <see cref="Expect.Value{T}" />s, partial objects, types (any one of them, whatever its values), or the rows of
    /// <c>PartialObjects.FromTable</c>. A message sent within the act is not among them. The scheduled side's
    /// counterpart of <c>ThenEventsInAnyOrder</c>: what a timeout-setting handler scheduled is judged as a whole.
    /// </summary>
    [EventModelRole(SliceRole.Message, nameof(messages))]
    public void ThenMessagesScheduled(params object[] messages)
    {
        var expected = flatten(messages).Select(x => x is Type type ? anyOf(type) : x).ToArray();
        if (expected.Length == 0)
            throw new ArgumentException("Name the messages the act scheduled; ThenNoMessageScheduled<T>() says none was.", nameof(messages));

        var types = expected.Select(PartialMatching.ExpectedType).ToArray();
        using var step = ScenarioRecorder.Step("Then", $"{describeTypes(types)} {(expected.Length == 1 ? "is" : "are")} scheduled");
        if (!actSucceeded()) return;

        var actual = LastAct.Session is { } session ? scheduledIn(session).Select(x => x.Message!).ToArray() : [];
        var run = ObjectSetVerification.Verify(actual, expected, "message", SetMode.AnyOrder);
        if (Verdicts.Recording) run.Report();

        messageFact(run.Succeeded, string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, "message")));
    }

    /// <summary>
    /// The act scheduled no <typeparamref name="T" />. The act's own message, when only scheduled, is a scheduled message
    /// like any other, so this step fails on it plainly; for any other type it fails with the GH-5077 guard, since an act
    /// whose own message never ran could not have scheduled anything.
    /// </summary>
    public void ThenNoMessageScheduled<T>()
    {
        using var step = ScenarioRecorder.Step("Then", $"no {typeof(T).Name} is scheduled");
        if (!acted)
        {
            Verdicts.Fail($"Expected no {typeof(T).Name} to be scheduled, but no act has run.");
            return;
        }

        var scheduled = scheduledOf(typeof(T)).ToArray();
        if (scheduled.Length > 0)
        {
            Verdicts.Fail($"Expected no {typeof(T).Name} to be scheduled but {scheduled.Length} were. {activitySummary(LastAct)}");
            return;
        }

        canJudgeAnAbsence($"Expected no {typeof(T).Name} to be scheduled");
    }

    // ---- assert: handled messages (GH-5076) ----------------------------------------------------

    /// <summary>
    /// GH-5076. A <typeparamref name="T" /> was handled <b>successfully</b> while the act ran — the act's own message, or
    /// one it caused: at least one execution of it completed without an exception, read from the tracked session's
    /// <c>MessageSucceeded</c> records, which is what Wolverine itself means by handled.
    /// </summary>
    /// <remarks>
    /// Judged whether or not the act failed, since an act's failure may be exactly the story: when the step fails, it
    /// says what happened to <typeparamref name="T" /> instead — never received or executed (sent elsewhere, scheduled,
    /// routed nowhere, or never seen at all), or executed with every attempt failing, each attempt's exception named —
    /// and lists every other exception the act's tracked session captured. A message only scheduled was not handled;
    /// <see cref="WhenScheduledMessagesAreExecuted" /> runs it.
    /// </remarks>
    public void ThenHandled<T>() => ThenHandled(typeof(T));

    /// <summary>GH-5076. A message of <paramref name="messageType" /> was handled successfully while the act ran. See <see cref="ThenHandled{T}" />.</summary>
    public void ThenHandled(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        using var step = ScenarioRecorder.Step("Then", $"{nameOf(messageType)} is handled");
        if (!acted)
        {
            Verdicts.Fail($"Expected a {nameOf(messageType)} to be handled, but no act has run.");
            return;
        }

        var session = LastAct.Session;
        var succeeded = successesOf(messageType);
        Verdicts.Check("handled", succeeded > 0 ? "yes" : "no", "yes");
        if (succeeded > 0) return;

        var story = whatHappenedTo(messageType, out var guarded);
        var others = otherExceptions(messageType);
        Verdicts.Fail($"Expected a {nameOf(messageType)} to be handled successfully, but {story}" +
                      (others.Length == 0 ? "" : $" Other exceptions during the act: {others}.") +
                      (guarded ? "" : onlyScheduledSuffix()) + " " + activitySummary(LastAct));
    }

    /// <summary>
    /// GH-5076. No <typeparamref name="T" /> was handled while the act ran: no execution of it even started, so a
    /// failed attempt still counts as handled — its handler ran. See <see cref="ThenHandled{T}" />.
    /// </summary>
    public void ThenNotHandled<T>() => ThenNotHandled(typeof(T));

    /// <summary>GH-5076. No message of <paramref name="messageType" /> was handled while the act ran. See <see cref="ThenNotHandled{T}" />.</summary>
    public void ThenNotHandled(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        using var step = ScenarioRecorder.Step("Then", $"no {nameOf(messageType)} is handled");
        if (!canJudgeAnAbsence($"Expected no {nameOf(messageType)} to be handled")) return;

        var ran = recordsOf(MessageEventType.ExecutionStarted, messageType).Count;
        if (ran == 0) return;

        var succeeded = successesOf(messageType);
        var failures = failuresOf(messageType);
        messageFact(false,
            $"Expected no {nameOf(messageType)} to be handled, but its handler ran {times(ran)}: " +
            $"{succeeded} succeeded, {failures.Count} failed{(failures.Count == 0 ? "" : $" ({describeFailures(failures)})")}.");
    }

    // What became of a message that was never handled successfully, as the tail of a sentence. Says whether it
    // carried the GH-5077 hint itself, so the step does not add it twice
    private string whatHappenedTo(Type messageType, out bool guarded)
    {
        guarded = false;
        var ran = recordsOf(MessageEventType.ExecutionStarted, messageType).Count;
        if (ran > 0)
        {
            var failures = failuresOf(messageType);
            var deadLettered = recordsOf(MessageEventType.MovedToErrorQueue, messageType).Count > 0;
            return $"it ran {times(ran)} and no attempt succeeded" +
                   (failures.Count == 0 ? "." : $": {describeFailures(failures)}.") +
                   (deadLettered ? " It was moved to the error queue." : "");
        }

        if (LastAct.OnlyScheduled.Any(messageType.IsInstanceOfType))
        {
            guarded = true;
            return "it never ran. " + OnlyScheduledMessage(LastAct.OnlyScheduled);
        }

        if (scheduledOf(messageType).Any())
        {
            guarded = true;
            return "it never ran: it was SCHEDULED for later, and a scheduled message is not handled within the act. " +
                   "To run it, add `await WhenScheduledMessagesAreExecuted();` after the act.";
        }

        if (recordsOf(MessageEventType.NoRoutes, messageType).Count > 0 || recordsOf(MessageEventType.NoHandlers, messageType).Count > 0)
            return "it never ran: Wolverine found no route or handler for it.";

        var sent = sentOf(messageType).ToArray();
        if (sent.Length > 0)
        {
            return $"it never ran here: it was sent to {string.Join(", ", sent.Select(x => x.Destination?.ToString() ?? "(no destination)").Distinct())}, " +
                   "and no handler of it executed within the act.";
        }

        return "it never appeared in the act's message activity.";
    }

    // Executions of a message that succeeded: Wolverine's MessageSucceeded, which a listener records, or -- for one
    // invoked inline, as WhenScheduledMessagesAreExecuted replays a local message -- an execution that finished with
    // no exception recorded against that attempt
    private int successesOf(Type messageType)
    {
        var failed = failuresOf(messageType).Select(x => (x.Envelope, x.AttemptNumber)).ToHashSet();
        return recordsOf(MessageEventType.MessageSucceeded, messageType)
            .Concat(recordsOf(MessageEventType.ExecutionFinished, messageType).Where(x => x.Exception is null))
            .Select(x => (x.Envelope, x.AttemptNumber))
            .Where(x => !failed.Contains(x))
            .Distinct()
            .Count();
    }

    private IReadOnlyList<EnvelopeRecord> recordsOf(MessageEventType eventType, Type messageType)
        => LastAct.Session?.AllRecordsInOrder(eventType).Where(x => x.Message is not null && messageType.IsInstanceOfType(x.Message)).ToList()
           ?? (IReadOnlyList<EnvelopeRecord>)Array.Empty<EnvelopeRecord>();

    // Each failed attempt once, however many records -- failed, moved to the error queue -- carry its exception
    private IReadOnlyList<EnvelopeRecord> failuresOf(Type messageType)
        => LastAct.Session?.AllRecordsInOrder()
               .Where(x => x.Exception is not null && x.Message is not null && messageType.IsInstanceOfType(x.Message))
               .DistinctBy(x => (x.Envelope, x.AttemptNumber, x.Exception))
               .ToList()
           ?? (IReadOnlyList<EnvelopeRecord>)Array.Empty<EnvelopeRecord>();

    private static string describeFailures(IEnumerable<EnvelopeRecord> failures)
        => string.Join("; ", failures.Select(x => $"attempt {x.AttemptNumber} threw {x.Exception!.GetType().Name}: {x.Exception.Message}"));

    // Every exception the act's tracked session captured that is not this message type's own
    private string otherExceptions(Type messageType)
    {
        var session = LastAct.Session;
        if (session is null) return "";

        var records = session.AllRecordsInOrder().Where(x => x.Exception is not null).ToList();
        var mine = records.Where(x => x.Message is not null && messageType.IsInstanceOfType(x.Message))
            .Select(x => x.Exception!).ToHashSet(ReferenceEqualityComparer.Instance);

        return string.Join("; ", session.AllExceptions().Distinct(ReferenceEqualityComparer.Instance).Cast<Exception>()
            .Where(x => !mine.Contains(x))
            .Select(e =>
            {
                var owner = records.FirstOrDefault(x => ReferenceEquals(x.Exception, e))?.Message;
                return $"{(owner is null ? "" : nameOf(owner.GetType()) + " threw ")}{e.GetType().Name}: {e.Message}";
            }));
    }

    // Failed attempts, each once, however many records -- failed, moved to the error queue -- carry its exception
    private static int failedAttempts(ITrackedSession session)
        => session.AllRecordsInOrder().Where(x => x.Exception is not null).DistinctBy(x => (x.Envelope, x.AttemptNumber)).Count();

    private static string times(int count) => count == 1 ? "once" : $"{count} times";

    // ---- message activity --------------------------------------------------------------------

    // A message the handling sent: a Sent record whose envelope was not scheduled for later, and not the act's own
    // message, which the act dispatched rather than produced
    private static IEnumerable<Envelope> sentIn(ITrackedSession session, IReadOnlyList<object> own)
        => session.Sent.RecordsInOrder().Where(x => !x.WasScheduled && x.Envelope?.Message is not null)
            .Select(x => x.Envelope!).Where(x => !own.Contains(x.Message!, ReferenceEqualityComparer.Instance));

    // A message the act scheduled: once each, however many records a transport made of scheduling it
    private static IEnumerable<Envelope> scheduledIn(ITrackedSession session)
        => session.Scheduled.RecordsInOrder().Select(x => x.Envelope).OfType<Envelope>().Where(x => x.Message is not null)
            .Distinct(ReferenceEqualityComparer.Instance).Cast<Envelope>();

    private IEnumerable<Envelope> sentOf(Type messageType)
        => LastAct.Session is { } session ? sentIn(session, LastAct.OwnMessages).Where(x => messageType.IsInstanceOfType(x.Message)) : [];

    private IEnumerable<Envelope> scheduledOf(Type messageType)
        => LastAct.Session is { } session ? scheduledIn(session).Where(x => messageType.IsInstanceOfType(x.Message)) : [];

    private static bool destinationMatches(Envelope envelope, string? destination)
        => destination is null ||
           (envelope.Destination?.ToString().StartsWith(destination, StringComparison.OrdinalIgnoreCase) ?? false);

    // One expected message among the candidates: values compared structurally, or a partial on the members it names
    private void judgeMessage(object expected, object[] candidates, string verb, string elsewhere)
    {
        var type = PartialMatching.ExpectedType(expected);
        if (candidates.Length == 0)
        {
            messageFact(false, $"Expected a {nameOf(type)} to be {verb}, but none was{elsewhere}.");
            return;
        }

        if (expected is IPartialObject partial)
        {
            var run = ObjectSetVerification.Verify(candidates, [partial], "message", SetMode.Contains);
            if (Verdicts.Recording) run.Report();
            messageFact(run.Succeeded, string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, "message")));
            return;
        }

        var value = unwrap(expected);
        var ignored = (expected as IExpectedValue)?.IgnoredPaths;
        var comparisons = candidates.Select(x => ObjectComparison.Compare(x, value, ignored)).ToArray();
        var best = comparisons.FirstOrDefault(x => x.All(leaf => leaf.Matched)) ?? comparisons[0];

        Verdicts.Row(best, 0);
        messageFact(best.All(x => x.Matched), $"No {nameOf(type)} {verb} matched the expectation: {Verdicts.Describe(best)}");
    }

    // GH-5077: a message expectation that fails says what the act's messages did, and why when its own message never ran
    private bool messageFact(bool condition, string because)
        => condition || Verdicts.Fail(because + onlyScheduledSuffix() + " " + activitySummary(LastAct));

    // GH-5077: an absence is no evidence when the act's own message was only scheduled -- no handler ran, so nothing it
    // would have done could have happened. Such a step fails instead of passing on nothing
    private bool canJudgeAnAbsence(string expectation)
    {
        if (!acted) return Verdicts.Fail($"{expectation}, but no act has run.");
        return onlyScheduledGuard() is not { } guard || Verdicts.Fail(guard);
    }

    /// <summary>
    /// wolverine#5077. The base's negative event steps (<c>ThenNoEvents</c>, <c>ThenNotEmitted</c>, ...) and
    /// <c>ThenMessagesSent</c> fail with the same sentence when the act's own message was only scheduled.
    /// </summary>
    protected override string? onlyScheduledGuard()
        => LastAct.OnlyScheduled.Count == 0 ? null : OnlyScheduledMessage(LastAct.OnlyScheduled) + " " + activitySummary(LastAct);

    private string onlyScheduledSuffix() => LastAct.OnlyScheduled.Count > 0 ? " " + OnlyScheduledMessage(LastAct.OnlyScheduled) : "";

    /// <summary>GH-5077. What a message step says when the act's own messages were only scheduled, so no handler of them ran.</summary>
    internal static string OnlyScheduledMessage(IReadOnlyList<object> messages)
        => $"The act's {describeTypes(messages.Select(x => x.GetType()).Distinct().ToArray())} " +
           $"{(messages.Count == 1 ? "was" : "were")} only scheduled for later, so no handler ran and nothing this step " +
           "checks could have happened. To run it, add `await WhenScheduledMessagesAreExecuted();` after the act.";

    /// <summary>
    /// GH-5077. The act's message activity in one line, for a failed message step — "Activity: 2 sent, 1 scheduled,
    /// 1 executed, 0 failed — see the Message activity report" — beside the <see cref="MessageActivityReport" /> that
    /// lists every record.
    /// </summary>
    private static string activitySummary(ActOutcome act)
    {
        if (act.Session is not { } session) return "Activity: the act recorded no message activity.";

        // Sent is what the handling produced, never the act's own message; scheduled is everything scheduled, the act's
        // own message included -- an armed timeout is scheduled
        return $"Activity: {sentIn(session, act.OwnMessages).Count()} sent, {scheduledIn(session).Count()} scheduled, " +
               $"{session.Executed.RecordsInOrder().Count()} executed, {failedAttempts(session)} failed " +
               "— see the Message activity report";
    }
}
