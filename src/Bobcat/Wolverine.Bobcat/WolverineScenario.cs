using Bobcat;
using Bobcat.CritterStack;
using Bobcat.Engine;
using Bobcat.Runtime;
using System.Globalization;
using JasperFx.Events;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace Wolverine.Bobcat;

/// <summary>
/// What one act did: the tracked session, the events it appended, and how it failed or was refused.
/// </summary>
/// <param name="Session">The tracked session, when the act got as far as completing one.</param>
/// <param name="NewEvents">The events the act appended — the arranged stream's delta, or, with no stream arranged, everything the store issued after the act began.</param>
/// <param name="Error">The exception the act raised or a handler threw, when there was one.</param>
/// <param name="Refusal">A refusal that is an answer rather than a failure — an HTTP endpoint's 4xx body. A bus-dispatched command refuses by throwing, which is <paramref name="Error" />.</param>
public sealed record ActOutcome(
    ITrackedSession? Session,
    IReadOnlyList<IEvent> NewEvents,
    Exception? Error,
    string? Refusal = null)
{
    /// <summary>Nothing has acted yet.</summary>
    public static readonly ActOutcome None = new(null, Array.Empty<IEvent>(), null);
}

/// <summary>
/// The Given/When/Then vocabulary for a Wolverine application, over one host (GH-4833): arrange
/// events, act through Wolverine's tracked session, and assert what the act appended and cascaded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composable on purpose.</b> <see cref="WolverineSpec" /> is the base class an xUnit or TUnit
/// suite derives from, and it delegates every step here. A Bobcat Gherkin fixture already has a base
/// class of its own, so it holds one of these instead — the same steps, the same act capture.
/// </para>
/// <para>
/// <b>Every step renders.</b> Each one records itself through <see cref="ScenarioRecorder" /> — the
/// marker-step model a projected Bobcat spec renders from — and reports its verdict as cells. Nothing
/// takes an <c>IStepContext</c>, which a projected test does not have. Outside a recording scenario
/// the steps still run and a wrong is thrown; see <see cref="SpecificationFailedException" />.
/// </para>
/// <para>
/// <b>Store-agnostic.</b> Events are arranged and read through the JasperFx.Events abstractions, so
/// the same scenario runs against Marten, Polecat, Fisher or the in-memory prototyping store. An
/// application with no event store can still use every act and the message assertions.
/// </para>
/// <para>
/// <b>Works against field-less stubs.</b> Nothing here needs an event or a command to carry data;
/// only a read model needs an identity to be loaded by.
/// </para>
/// </remarks>
public class WolverineScenario
{
    private readonly string? _storeName;
    private IEventStore? _store;
    private object? _stream;

    /// <param name="host">The application under test.</param>
    /// <param name="storeName">Which event store to use, when the application has several. Null takes the only one.</param>
    public WolverineScenario(IHost host, string? storeName = null)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _storeName = storeName;
    }

    /// <summary>The application under test.</summary>
    public IHost Host { get; }

    /// <summary>How long an act's tracked session waits for everything it caused to settle. Five seconds by default.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether an act's tracked session also waits on external transports — a message cascaded to a
    /// broker and handled back in this application. On by default.
    /// </summary>
    public bool IncludeExternalTransports { get; set; } = true;

    /// <summary>What the last act did.</summary>
    public ActOutcome LastAct { get; protected set; } = ActOutcome.None;

    /// <summary>The application's event store. Throws, saying so, when it has none.</summary>
    public IEventStore Store => _store ??= Host.Services.EventStore(_storeName);

    private bool? _hasStore;

    // An application with no event store still gets every act and the message assertions
    private bool hasStore()
    {
        if (_hasStore is { } known) return known;

        try
        {
            _store ??= Host.Services.EventStore(_storeName);
            _hasStore = true;
        }
        catch (Exception)
        {
            _hasStore = false;
        }

        return _hasStore.Value;
    }

    /// <summary>Clear the event store and forget the last act, for a suite that shares one host across scenarios.</summary>
    public async Task ResetAsync(CancellationToken token = default)
    {
        if (Host.Services.EventStores().Count > 0) await Host.Services.ResetEventStoresAsync(token);
        _stream = null;
        LastAct = ActOutcome.None;
    }

    // ---- arrange ---------------------------------------------------------------------------

    /// <summary>The stream the scenario acts against already holds exactly these events.</summary>
    public Task GivenEvents<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => GivenEvents(typeof(TAggregate), id, events);

    /// <inheritdoc cref="GivenEvents{TAggregate}(Guid, object[])" />
    public Task GivenEvents<TAggregate>(string key, params object[] events) where TAggregate : class
        => GivenEvents(typeof(TAggregate), key, events);

    /// <inheritdoc cref="GivenEvents{TAggregate}(Guid, object[])" />
    public async Task GivenEvents(Type aggregate, object id, params object[] events)
    {
        var stream = streamName(aggregate, id);
        var (described, inline) = describe(events);
        using var step = ScenarioRecorder.Step("Given",
            events.Length == 0
                ? $"{stream} has no events yet"
                : $"{stream} has already recorded {described}");

        _stream = id;
        if (events.Length > 0) await EventStoreAuthoring.AppendAsync(Store, aggregate, id, events);
        if (!inline) recordValues("event", events);
    }

    /// <summary>
    /// The stream the scenario acts against has no events yet. Appends nothing — it says so in the
    /// specification, and names the stream the act's events are read from.
    /// </summary>
    public Task GivenNoEventsFor<TAggregate>(Guid id) where TAggregate : class => GivenEvents(typeof(TAggregate), id);

    /// <inheritdoc cref="GivenNoEventsFor{TAggregate}(Guid)" />
    public Task GivenNoEventsFor<TAggregate>(string key) where TAggregate : class => GivenEvents(typeof(TAggregate), key);

    /// <summary>
    /// Arrange events on a <em>different</em> stream from the one the act runs against — a second
    /// aggregate for a rule that spans two, or history a read model fans in from.
    /// </summary>
    public Task GivenEventsOn<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => GivenEventsOn(typeof(TAggregate), id, events);

    /// <inheritdoc cref="GivenEventsOn{TAggregate}(Guid, object[])" />
    public Task GivenEventsOn<TAggregate>(string key, params object[] events) where TAggregate : class
        => GivenEventsOn(typeof(TAggregate), key, events);

    /// <inheritdoc cref="GivenEventsOn{TAggregate}(Guid, object[])" />
    public async Task GivenEventsOn(Type aggregate, object id, params object[] events)
    {
        var (described, inline) = describe(events);
        using var step = ScenarioRecorder.Step("Given", $"{streamName(aggregate, id)} has already recorded {described}");
        if (events.Length > 0) await EventStoreAuthoring.AppendAsync(Store, aggregate, id, events);
        if (!inline) recordValues("event", events);
    }

    /// <summary>
    /// How a stream reads in a step: its id is named after the aggregate (GH-4835), so a stream
    /// arranged once reads "the Order stream" — and a second one of the same type "Order Order2".
    /// </summary>
    private static string streamName(Type aggregate, object id)
    {
        if (id is string key) return $"{aggregate.Name} \"{key}\"";

        ScenarioValues.Learn(id, aggregate.Name);
        var name = ScenarioValues.Format(id);
        return name == aggregate.Name ? $"the {aggregate.Name} stream" : $"{aggregate.Name} {name}";
    }

    /// <summary>
    /// Values as a step's text shows them: each one in full — <c>Type(Property: value, …)</c> — when
    /// they fit on the line, otherwise only their type names, and the caller shows the values as a
    /// table under the step instead.
    /// </summary>
    private static (string Text, bool Inline) describe(IReadOnlyList<object> values)
    {
        var full = ScenarioValues.DescribeAll(values);
        return full.Length <= ScenarioValues.InlineLimit ? (full, true) : (names(values), false);
    }

    /// <summary>Values too long for the step's text, as a table under it: one row each, unjudged.</summary>
    private static void recordValues(string noun, IReadOnlyList<object> values)
    {
        if (!Verdicts.Recording || values.Count == 0) return;

        var run = new TableRun([noun, ObjectSetVerification.ValuesColumn]);
        for (var i = 0; i < values.Count; i++)
        {
            run.Cells.Add(new CellResult(noun, ResultStatus.ok, values[i].GetType().Name) { RowIndex = i });
            run.Cells.Add(new CellResult(ObjectSetVerification.ValuesColumn, ResultStatus.ok,
                ScenarioValues.DescribeProperties(values[i])) { RowIndex = i });
        }

        run.Report(null);
    }

    // ---- act -------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="message" /> arrives at the application: sent through Wolverine, and the scenario
    /// waits for everything it caused to settle. A handler that throws is captured, not rethrown, so a
    /// refusal can be asserted with <see cref="ThenValidationFails" />.
    /// </summary>
    public Task WhenReceived(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
        => ActAsync("{0} is received", message,
            tracking => (configureTracking?.Invoke(tracking) ?? tracking).SendMessageAndWaitAsync(message));

    /// <summary><paramref name="message" /> is published through Wolverine to every subscriber, and the scenario waits for what it caused.</summary>
    public Task WhenPublished(object message,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
        => ActAsync("{0} is published", message,
            tracking => (configureTracking?.Invoke(tracking) ?? tracking).PublishMessageAndWaitAsync(message));

    /// <summary>
    /// Any act that reaches the application from outside — an HTTP call, a client SDK, a hosted
    /// service poke — run inside the tracked session, waiting for everything it caused.
    /// </summary>
    /// <param name="description">How the step reads: <c>"the nightly sweep runs"</c>.</param>
    public Task WhenTracked(string description, Func<Task> act,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
        => ActAsync(description,
            tracking => (configureTracking?.Invoke(tracking) ?? tracking)
                .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => act())));

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
        => actAsync(stepText, null, dispatch, complete);

    /// <summary>
    /// <see cref="ActAsync(string, Func{TrackedSessionConfiguration, Task{ITrackedSession}}, Func{ActOutcome, Task{ActOutcome}}?)" />
    /// for an act that carries a command or message: <paramref name="stepFormat" />'s <c>{0}</c> is
    /// <paramref name="subject" /> in full when it fits on the line, or its type name with its values
    /// in a table under the step when it does not.
    /// </summary>
    protected internal Task ActAsync(string stepFormat, object subject,
        Func<TrackedSessionConfiguration, Task<ITrackedSession>> dispatch,
        Func<ActOutcome, Task<ActOutcome>>? complete = null)
        => actAsync(stepFormat, subject, dispatch, complete);

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

        using var step = ScenarioRecorder.Step("When", stepText);
        if (!inline) recordValues("message", [subject!]);

        HandlerWarmUp.WarmBeforeTracking(Host);

        var store = hasStore() ? Store : null;
        long? floor = store is not null && _stream is null ? await EventStores.HighWaterSequenceAsync(store) : null;
        var before = store is not null && _stream is not null ? await fetchAsync(store, _stream) : Array.Empty<IEvent>();

        var tracking = Host.TrackActivity().Timeout(Timeout).DoNotAssertOnExceptionsDetected();
        if (IncludeExternalTransports) tracking = tracking.IncludeExternalTransports();

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

        IReadOnlyList<IEvent> appended = Array.Empty<IEvent>();
        if (store is not null)
        {
            appended = _stream is not null
                ? (await fetchAsync(store, _stream)).Skip(before.Count).ToList()
                : await EventStores.QueryEventsSinceAsync(store, floor!.Value + 1);
        }

        var outcome = new ActOutcome(session, appended, error);
        if (complete is not null) outcome = await complete(outcome);
        LastAct = outcome;

        if (SpecReport.IsRecording)
        {
            if (session is not null) SpecReport.For<MessageActivityReport>().Add(session);
            if (appended.Count > 0) SpecReport.For<AppendedEventsReport>().Add(appended);
        }
    }

    private static Task<IReadOnlyList<IEvent>> fetchAsync(IEventStore store, object id) => id switch
    {
        Guid guid => EventStores.FetchStreamAsync(store, guid),
        string key => EventStores.FetchStreamAsync(store, key),
        _ => throw new ArgumentOutOfRangeException(nameof(id), $"A stream is identified by a Guid or a string, not a {id.GetType().Name}")
    };

    // ---- assert: events ----------------------------------------------------------------------

    /// <summary>The act appended exactly these event types, in this order.</summary>
    public void ThenEvents(params Type[] events)
    {
        using var step = ScenarioRecorder.Step("Then", $"{names(events)} {(events.Length == 1 ? "is" : "are")} emitted");
        if (!actSucceeded()) return;

        var actual = LastAct.NewEvents.Select(x => x.Data.GetType()).ToArray();
        for (var i = 0; i < Math.Max(actual.Length, events.Length); i++)
        {
            Verdicts.Check("event", i < actual.Length ? actual[i].Name : null, i < events.Length ? events[i].Name : null, i);
        }

        Verdicts.Fact(actual.SequenceEqual(events),
            $"Expected {describeTypes(events)} but the act appended {describeTypes(actual)}");
    }

    /// <summary>
    /// The act appended exactly these events, in order, compared <b>structurally</b> — property by
    /// property, nested properties and collections included, never through <c>Equals</c> (GH-4835).
    /// Wrap one in <see cref="Expect.Value{T}" /> to show but not judge a member nobody controls.
    /// </summary>
    /// <remarks>
    /// Prefer a deterministic clock to <c>Ignoring(...)</c> for a timestamp: give the host a
    /// <see cref="TimeProvider" /> the test controls and the value becomes something to assert.
    /// </remarks>
    public void ThenEvents(params object[] events)
    {
        using var step = ScenarioRecorder.Step("Then",
            $"{names(events.Select(unwrap).ToArray())} {(events.Length == 1 ? "is" : "are")} emitted");
        if (!actSucceeded()) return;

        var actual = LastAct.NewEvents.Select(x => x.Data).ToArray();
        verifySet("event", actual, events);
    }

    /// <summary>The act appended nothing — the refusal half of a guard.</summary>
    public void ThenNoEvents()
    {
        using var step = ScenarioRecorder.Step("Then", "no events are emitted");
        Verdicts.Fact(LastAct.NewEvents.Count == 0,
            $"Expected no events but the act appended {ScenarioValues.DescribeAll(LastAct.NewEvents.Select(x => x.Data))}");
    }

    /// <summary>
    /// The one event of type <typeparamref name="T" /> the act appended, to assert on further. Not a
    /// step: it reads the capture, and the assertion that follows is the step. Throws unless exactly
    /// one was appended.
    /// </summary>
    public T TheEvent<T>()
    {
        var matching = LastAct.NewEvents.Select(x => x.Data).OfType<T>().ToArray();
        if (matching.Length == 1) return matching[0];

        throw new SpecificationFailedException(
            $"Expected exactly one {typeof(T).Name} but the act appended {matching.Length}{failureSuffix()}");
    }

    /// <summary>Every event the act appended, for a test that wants to look for itself.</summary>
    public IReadOnlyList<object> TheEvents => LastAct.NewEvents.Select(x => x.Data).ToList();

    /// <summary>
    /// The act started a <typeparamref name="TAggregate" /> stream with this identity. The identity is
    /// usually the decision — a redelivered trigger that reuses an id collides instead of starting a
    /// second stream — and <c>ThenEvents</c> cannot say where its events went.
    /// </summary>
    public Task ThenStreamIsStarted<TAggregate>(Guid id) where TAggregate : class
        => ThenStreamIsStarted(typeof(TAggregate), id);

    /// <inheritdoc cref="ThenStreamIsStarted{TAggregate}(Guid)" />
    public Task ThenStreamIsStarted<TAggregate>(string key) where TAggregate : class
        => ThenStreamIsStarted(typeof(TAggregate), key);

    /// <inheritdoc cref="ThenStreamIsStarted{TAggregate}(Guid)" />
    public async Task ThenStreamIsStarted(Type aggregate, object id)
    {
        using var step = ScenarioRecorder.Step("Then", $"a {aggregate.Name} stream is started with id \"{id}\"");
        if (!actSucceeded()) return;

        var events = await fetchAsync(Store, id);
        Verdicts.Fact(events.Count > 0,
            $"Expected a {aggregate.Name} stream with id {id}, but no stream exists there. The act appended its events somewhere else, or started no stream at all.");
    }

    /// <summary>The write model, folded from its stream.</summary>
    public Task<T?> TheAggregate<T>(Guid id) where T : class => EventStores.AggregateStreamAsync<T>(Store, id);

    /// <inheritdoc cref="TheAggregate{T}(Guid)" />
    public Task<T?> TheAggregate<T>(string key) where T : class => EventStores.AggregateStreamAsync<T>(Store, key);

    // ---- assert: refusals --------------------------------------------------------------------

    /// <summary>
    /// The act was refused, and the refusal says <paramref name="reason" />: an HTTP endpoint's
    /// ProblemDetails, or the exception a bus-dispatched command's handler threw.
    /// </summary>
    public void ThenRefusedWith(string reason) => assertRefusal($"refused with \"{reason}\"", reason);

    /// <summary>A bus-dispatched command failed validation with <paramref name="reason" />. The same check as <see cref="ThenRefusedWith" />, worded for a message.</summary>
    public void ThenValidationFails(string reason) => assertRefusal($"validation fails with \"{reason}\"", reason);

    private void assertRefusal(string text, string reason)
    {
        using var step = ScenarioRecorder.Step("Then", text);

        var refusal = LastAct.Refusal ?? LastAct.Error?.Message;
        if (refusal is null)
        {
            Verdicts.Fail($"Expected the act to be refused with \"{reason}\", but it succeeded.");
            return;
        }

        Verdicts.Cell("reason", refusal.Contains(reason, StringComparison.Ordinal), reason, refusal);
        Verdicts.Fact(refusal.Contains(reason, StringComparison.Ordinal),
            $"Expected a refusal containing \"{reason}\" but it was: {refusal}");
    }

    // ---- assert: read models -----------------------------------------------------------------

    /// <summary>
    /// The read model with this identity, once the projections have caught up — an async projection
    /// read straight after the act reads a stale document. Throws when there is no such document,
    /// because there is nothing to hand back.
    /// </summary>
    public async Task<T> ThenReadModel<T>(object id) where T : class
    {
        await ThenProjectionsAreCaughtUp(typeof(T));

        return await EventStoreAuthoring.LoadDocumentAsync<T>(Store, id)
               ?? throw new SpecificationFailedException(
                   $"No {typeof(T).Name} document with id {id}. The projections caught up, so either nothing routed to this id or its identity rule differs.");
    }

    /// <summary>The async projections have caught up, so a read model reflects the act.</summary>
    public async Task ThenProjectionsAreCaughtUp(Type readModel)
    {
        using var step = ScenarioRecorder.Step("Then", $"the {readModel.Name} read model has caught up");
        await EventStores.WaitForNonStaleProjectionsAsync(Store, EventStores.DefaultProjectionTimeout);
    }

    // ---- assert: cascaded messages (GH-4837) -------------------------------------------------

    /// <summary>
    /// The act sent or published a <typeparamref name="T" /> — to <paramref name="destination" /> when
    /// one is given, matched as a prefix of the destination URI (<c>"rabbitmq://queue/billing"</c>,
    /// <c>"local://"</c>).
    /// </summary>
    public void ThenMessageSent<T>(string? destination = null)
    {
        using var step = ScenarioRecorder.Step("Then",
            destination is null ? $"{typeof(T).Name} is sent" : $"{typeof(T).Name} is sent to \"{destination}\"");
        if (!actSucceeded()) return;

        var sent = sentOf(typeof(T)).ToArray();
        var matching = sent.Where(x => destinationMatches(x, destination)).ToArray();

        Verdicts.Check("sent", matching.Length > 0 ? "yes" : "no", "yes");
        Verdicts.Fact(matching.Length > 0, sent.Length == 0
            ? $"Expected a {typeof(T).Name} to be sent, but none was{(scheduledOf(typeof(T)).Any() ? " (one was SCHEDULED for later: use ThenMessageScheduled)" : "")}. Sent: {describeSent()}"
            : $"Expected a {typeof(T).Name} to be sent to {destination}, but it went to {string.Join(", ", sent.Select(x => x.Destination?.ToString() ?? "(no destination)"))}");
    }

    /// <summary>The act sent a <typeparamref name="T" /> to a local queue in this application.</summary>
    public void ThenMessageSentLocally<T>() => ThenMessageSent<T>("local://");

    /// <summary>The act sent a <typeparamref name="T" /> to an external transport — anything but a local queue.</summary>
    public void ThenMessageSentExternally<T>()
    {
        using var step = ScenarioRecorder.Step("Then", $"{typeof(T).Name} is sent to an external transport");
        if (!actSucceeded()) return;

        var external = sentOf(typeof(T)).Where(x => x.Destination?.Scheme is { } scheme && scheme != "local").ToArray();
        Verdicts.Fact(external.Length > 0,
            $"Expected a {typeof(T).Name} to be sent to an external transport. Sent: {describeSent()}");
    }

    /// <summary>
    /// The act sent or published a message structurally equal to <paramref name="expected" /> — or an
    /// <see cref="Expect.Value{T}" /> with members to ignore.
    /// </summary>
    public void ThenMessageSent(object expected)
    {
        var value = unwrap(expected);
        using var step = ScenarioRecorder.Step("Then", $"{value.GetType().Name} is sent");
        if (!actSucceeded()) return;

        var candidates = sentOf(value.GetType()).Select(x => x.Message).ToArray();
        if (candidates.Length == 0)
        {
            Verdicts.Fail($"Expected a {value.GetType().Name} to be sent, but none was. Sent: {describeSent()}");
            return;
        }

        var ignored = (expected as IExpectedValue)?.IgnoredPaths;
        var comparisons = candidates.Select(x => ObjectComparison.Compare(x, value, ignored)).ToArray();
        var best = comparisons.FirstOrDefault(x => x.All(leaf => leaf.Matched)) ?? comparisons[0];

        Verdicts.Row(best, 0);
        Verdicts.Fact(best.All(x => x.Matched),
            $"No {value.GetType().Name} sent matched the expectation: {Verdicts.Describe(best)}");
    }

    /// <summary>
    /// The act scheduled a <typeparamref name="T" /> for later — <c>DelayedFor</c>, <c>ScheduledAt</c>,
    /// <c>OutgoingMessages.Delay</c>. A scheduled message is not sent within the act, so
    /// <see cref="ThenMessageSent{T}" /> does not see it. With <paramref name="delay" />, the message
    /// is due no sooner than that long after the act.
    /// </summary>
    public void ThenMessageScheduled<T>(TimeSpan? delay = null)
    {
        using var step = ScenarioRecorder.Step("Then",
            delay is null ? $"{typeof(T).Name} is scheduled" : $"{typeof(T).Name} is scheduled for {delay}");
        if (!actSucceeded()) return;

        var scheduled = scheduledOf(typeof(T)).ToArray();
        Verdicts.Check("scheduled", scheduled.Length > 0 ? "yes" : "no", "yes");
        if (!Verdicts.Fact(scheduled.Length > 0,
                $"Expected a {typeof(T).Name} to be scheduled, but none was. Sent: {describeSent()}")) return;

        if (delay is { } minimum && scheduled[0].ScheduledTime is { } due)
        {
            // measured from now, after the act: the message was due at least `minimum` after it was scheduled
            var remaining = due - DateTimeOffset.UtcNow;
            Verdicts.Value("due", due);
            Verdicts.Fact(remaining > minimum - TimeSpan.FromMinutes(1),
                $"Expected {typeof(T).Name} to be due in about {minimum}, but it is due at {due:O}");
        }
    }

    /// <summary>The act scheduled no <typeparamref name="T" />.</summary>
    public void ThenNoMessageScheduled<T>()
    {
        using var step = ScenarioRecorder.Step("Then", $"no {typeof(T).Name} is scheduled");
        var scheduled = scheduledOf(typeof(T)).ToArray();
        Verdicts.Fact(scheduled.Length == 0, $"Expected no {typeof(T).Name} to be scheduled but {scheduled.Length} were");
    }

    private IEnumerable<Envelope> scheduledOf(Type messageType)
        => LastAct.Session?.Scheduled.Envelopes().Where(x => x.Message is not null && messageType.IsInstanceOfType(x.Message))
           ?? Enumerable.Empty<Envelope>();

    /// <summary>The act sent no <typeparamref name="T" /> anywhere.</summary>
    public void ThenNoMessageSent<T>()
    {
        using var step = ScenarioRecorder.Step("Then", $"no {typeof(T).Name} is sent");
        var sent = sentOf(typeof(T)).ToArray();
        Verdicts.Fact(sent.Length == 0,
            $"Expected no {typeof(T).Name} but {sent.Length} were sent, to {string.Join(", ", sent.Select(x => x.Destination?.ToString() ?? "(no destination)"))}");
    }

    /// <summary>Every message the act sent or published, for a test that wants to look for itself.</summary>
    public IReadOnlyList<object> TheMessagesSent
        => LastAct.Session?.Sent.AllMessages().ToList() ?? (IReadOnlyList<object>)Array.Empty<object>();

    private IEnumerable<Envelope> sentOf(Type messageType)
        => LastAct.Session?.Sent.Envelopes().Where(x => x.Message is not null && messageType.IsInstanceOfType(x.Message))
           ?? Enumerable.Empty<Envelope>();

    private static bool destinationMatches(Envelope envelope, string? destination)
        => destination is null ||
           (envelope.Destination?.ToString().StartsWith(destination, StringComparison.OrdinalIgnoreCase) ?? false);

    private string describeSent()
    {
        var sent = LastAct.Session?.Sent.Envelopes().ToArray() ?? Array.Empty<Envelope>();
        return sent.Length == 0
            ? "nothing"
            : string.Join(", ", sent.Select(x => $"{(x.Message is null ? x.MessageType : ScenarioValues.Describe(x.Message))} to {x.Destination}"));
    }

    // ---- assert: any object (GH-4836) --------------------------------------------------------

    /// <summary>
    /// Verify selected properties of <paramref name="subject" /> as a table — Bobcat's
    /// <c>PropertyCells</c>, one cell per column, nested paths (<c>Address.City</c>) included. Not
    /// HTTP-specific: a response, a read model, an aggregate, anything.
    /// </summary>
    /// <param name="subject">What is being verified.</param>
    /// <param name="expected">One header row naming the properties, one row of expected values: <c>"| Name | Address.City |\n| Ann | Austin |"</c>.</param>
    public void Verify(object subject, StepTable expected)
    {
        using var step = ScenarioRecorder.Step("Then", $"the {subject.GetType().Name} has");
        var run = PropertyCells.Verify(subject, expected);
        if (!run.Succeeded && !Verdicts.Recording)
        {
            throw new SpecificationFailedException(
                $"{subject.GetType().Name} did not match: {string.Join(", ", PropertyCells.Disagreeing(run))}");
        }
    }

    /// <summary>
    /// <paramref name="subject" /> is structurally equal to <paramref name="expected" /> — or to an
    /// <see cref="Expect.Value{T}" /> with members to ignore. The same rules as <see cref="ThenEvents(object[])" />.
    /// </summary>
    public void ThenMatches(object? subject, object expected)
    {
        var value = unwrap(expected);
        using var step = ScenarioRecorder.Step("Then", $"the {value.GetType().Name} matches");
        compareSequence(value.GetType().Name, subject is null ? Array.Empty<object>() : new[] { subject }, new[] { expected });
    }

    // ---- shared ------------------------------------------------------------------------------

    private void compareSequence(string noun, IReadOnlyList<object> actual, IReadOnlyList<object> expected)
    {
        var problems = new List<string>();

        for (var i = 0; i < Math.Max(actual.Count, expected.Count); i++)
        {
            var expectedValue = i < expected.Count ? unwrap(expected[i]) : null;
            var actualValue = i < actual.Count ? actual[i] : null;

            Verdicts.Check(noun, actualValue?.GetType().Name, expectedValue?.GetType().Name, i);

            if (expectedValue is null)
            {
                problems.Add($"[{i}] an extra {actualValue!.GetType().Name}");
                continue;
            }

            if (actualValue is null)
            {
                problems.Add($"[{i}] {expectedValue.GetType().Name} is missing");
                continue;
            }

            if (actualValue.GetType() != expectedValue.GetType())
            {
                problems.Add($"[{i}] expected {expectedValue.GetType().Name} but was {actualValue.GetType().Name}");
                continue;
            }

            var leaves = ObjectComparison.Compare(actualValue, expectedValue, (expected[i] as IExpectedValue)?.IgnoredPaths);
            if (!Verdicts.Row(leaves, i)) problems.Add($"[{i}] {expectedValue.GetType().Name}: {Verdicts.Describe(leaves)}");
        }

        Verdicts.Fact(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static object unwrap(object value) => value is IExpectedValue expected ? expected.Value : value;

    /// <summary>
    /// <paramref name="actual" /> is exactly <paramref name="expected" />, in order, as a set
    /// verification (GH-4835): one grid with a row per item — OK, a FAIL naming the values that
    /// disagree, MISSING, EXTRA, or ORDER — rather than a positional comparison that reads one missing
    /// event as every later one wrong.
    /// </summary>
    private void verifySet(string noun, IReadOnlyList<object> actual, IReadOnlyList<object> expected)
    {
        var values = expected.Select(unwrap).ToArray();
        var run = ObjectSetVerification.Cells(actual, values,
            (item, i) => ObjectComparison.Compare(item, values[i], (expected[i] as IExpectedValue)?.IgnoredPaths)
                .Where(x => !x.Matched)
                .Select(x => new ValueDifference(x.Path, x.Expected, x.Actual))
                .ToList(),
            noun);

        if (Verdicts.Recording) run.Report(null);

        Verdicts.Fact(run.Succeeded, string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, noun)));
    }

    /// <summary>
    /// A failure the step did not ask about is reported as the act failing, rather than as "expected 1
    /// event, got 0" — which would send the reader to the handler's logic when the answer is in the
    /// exception the act captured.
    /// </summary>
    private bool actSucceeded()
    {
        if (LastAct.Error is { } error)
        {
            return Verdicts.Fail($"The act failed: {error.GetType().Name}: {error.Message}");
        }

        if (LastAct.Refusal is { } refusal)
        {
            return Verdicts.Fail($"The act was refused: {refusal}");
        }

        return true;
    }

    private string failureSuffix()
        => LastAct.Error is { } e ? $". The act failed: {e.GetType().Name}: {e.Message}"
            : LastAct.Refusal is { } r ? $". The act was refused: {r}" : "";

    private static string names(IReadOnlyList<object> values) => describeTypes(values.Select(x => x.GetType()).ToArray());

    private static string names(Type[] types) => describeTypes(types);

    private static string describeTypes(IReadOnlyList<Type> types)
        => types.Count == 0 ? "nothing" : string.Join(", ", types.Select(x => x.Name));
}
