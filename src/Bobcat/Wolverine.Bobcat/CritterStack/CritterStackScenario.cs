using System.Globalization;
using Bobcat.Engine;
using Bobcat.Runtime;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Tags;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bobcat.CritterStack;

/// <summary>
/// The Given/When/Then vocabulary for an event-sourced application on Marten, Polecat or Fisher, over one
/// host and with no message bus (bobcat#471): arrange events and read models, act through the application's
/// own services, and assert what the act appended, what the aggregates and read models became, and how the
/// act was refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Store-only on purpose.</b> Plenty of applications use Marten without Wolverine, so nothing here needs
/// one. Every step reaches the store through the JasperFx.Events abstractions — the same scenario runs
/// against Marten, Polecat, Fisher or the in-memory store. WolverineFx.Bobcat's <c>WolverineScenario</c>
/// derives from this class and adds the acts that go through Wolverine's tracked session and the message
/// assertions; everything a store can answer lives here.
/// </para>
/// <para>
/// <b>Every step renders.</b> Each one records itself through <see cref="ScenarioRecorder" /> — the model a
/// projected Bobcat spec renders from — and reports its verdict as cells. Inside a recording scenario a wrong
/// is gathered on its step and the test carries on; outside one, the wrong is thrown
/// (<see cref="failureOf" />), so a plain test cannot pass by accident.
/// </para>
/// <para>
/// <b>Run evidence.</b> The types a scenario observably touched — the aggregates and events it arranged, the
/// events its acts appended, the read models it loaded — are recorded through
/// <see cref="ScenarioRecorder.RecordTouchedType" /> and travel on <c>scenario_finished.TouchedTypes</c>
/// (bobcat#465). Observed, never asserted: an event a Then step <i>expects</i> is not recorded.
/// </para>
/// </remarks>
public class CritterStackScenario
{
    /// <summary>
    /// How a refusal's reason is compared with the text the act raised. One place, so the case rule is a
    /// one-line change (bobcat#471 left exact-vs-ignore-case open).
    /// </summary>
    private const StringComparison ReasonComparison = StringComparison.Ordinal;

    private readonly string? _storeName;
    private readonly IHost? _host;
    private IEventStore? _store;
    private bool? _hasStore;

    // bobcat#504: what one specification accumulates, shared with every view In() hands out, so a step through a
    // view arranges, acts and asserts on the same specification. A view differs only in the store it is pinned to
    private readonly SpecificationState _state;
    private readonly CritterStackScenario? _root;
    private readonly IEventStore? _pinned;

    // bobcat#536: the tenant a view from ForTenant reads and writes as; null for the store's default session
    private readonly string? _tenant;

    private sealed class SpecificationState
    {
        public object? Stream;
        public CritterStackActOutcome LastAct = CritterStackActOutcome.None;
        public bool Acted;

        // bobcat#503: the streams this specification owns — arranged, addressed, or written by one of its acts — and
        // the tags it arranged events with. A store-only act attributes what it read back by these
        public readonly HashSet<object> OwnStreams = new();
        public readonly HashSet<object> ArrangedTags = new();

        public TimeSpan ProjectionTimeout = TimeSpan.FromSeconds(20);
        public IUnspecifiedValues? UnspecifiedValues;

        // bobcat#504: the host's stores, read once, and the routing between them when there are several
        public IReadOnlyList<IEventStore>? Stores;
        public StoreRouter? Router;

        // The stores this specification has touched: a step went to it, a view was pinned to it, or an act appended
        // the specification's own events to it. With the default store, what AfterAsyncDaemonsCatchUpAsync waits on
        public readonly HashSet<IEventStore> Touched = new(ReferenceEqualityComparer.Instance);
    }

    /// <param name="host">The application under test.</param>
    /// <param name="storeName">
    /// The default event store, when the application has several: what a step uses when no store's registrations
    /// claim its types (bobcat#504). Null takes the only one, or the one main store among several (an
    /// application's <c>AddMarten</c> store beside its ancillary <c>AddMartenStore&lt;T&gt;</c> ones).
    /// </param>
    public CritterStackScenario(IHost host, string? storeName = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Services = host.Services;
        _storeName = storeName;
        _state = new SpecificationState();
    }

    /// <summary>
    /// A scenario over an application's services alone, for a test that has a container but no
    /// <see cref="IHost" /> — a hand-built <c>ServiceProvider</c>, or a host's <c>Services</c> handed on.
    /// <see cref="Host" /> throws on a scenario built this way.
    /// </summary>
    /// <param name="services">The application's services. Its event store is resolved from them.</param>
    /// <param name="storeName">
    /// The default event store, when the application has several: what a step uses when no store's registrations
    /// claim its types (bobcat#504). Null takes the only one, or the one main store among several.
    /// </param>
    public CritterStackScenario(IServiceProvider services, string? storeName = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        _storeName = storeName;
        _state = new SpecificationState();
    }

    /// <summary>
    /// bobcat#504. A view of <paramref name="root" />'s specification pinned to <paramref name="store" />: every
    /// Given and Then reads and writes that store, while the specification — its streams, its last act, its
    /// settings — stays <paramref name="root" />'s, and an act through the view runs through
    /// <paramref name="root" />'s <see cref="runActAsync" />. What <see cref="createView" /> builds; a derived
    /// scenario that overrides it to hand out its own view type calls this.
    /// </summary>
    protected CritterStackScenario(CritterStackScenario root, IEventStore store)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root._root ?? root;
        _pinned = store ?? throw new ArgumentNullException(nameof(store));
        _host = root._host;
        Services = root.Services;
        _storeName = root._storeName;
        _state = root._state;
        _tenant = root._tenant;
    }

    /// <summary>
    /// bobcat#536. A view of <paramref name="root" />'s specification for one tenant: every Given and Then reads and
    /// writes as <paramref name="tenantId" />, on the store <paramref name="root" /> is pinned to (or routed to, as
    /// before), while the specification stays <paramref name="root" />'s. What <see cref="createTenantView" />
    /// builds; a derived scenario that overrides it to hand out its own view type calls this.
    /// </summary>
    protected CritterStackScenario(CritterStackScenario root, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        _root = root._root ?? root;
        _pinned = root._pinned;
        _host = root._host;
        Services = root.Services;
        _storeName = root._storeName;
        _state = root._state;
        _tenant = tenantId;
    }

    /// <summary>
    /// The application under test. Throws when the scenario was built from an <see cref="IServiceProvider" />
    /// rather than a host; every step reads <see cref="Services" /> instead, so only a derived scenario that
    /// needs the host itself reaches for this.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scenario was built from an <see cref="IServiceProvider" />.</exception>
    public IHost Host => _host ?? throw new InvalidOperationException(
        "This scenario was built from an IServiceProvider, so it has no IHost. Read Services instead, or build the scenario from the host.");

    /// <summary>The application's services: the host's, or the provider the scenario was built from.</summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// How long a step that reads a projection waits for the async daemons to catch up. Twenty seconds by
    /// default — what a cold daemon on a loaded CI runner needs.
    /// </summary>
    public TimeSpan ProjectionTimeout
    {
        get => _state.ProjectionTimeout;
        set => _state.ProjectionTimeout = value;
    }

    /// <summary>
    /// What fills the members a partial object (<c>Specify&lt;T&gt;()</c> or a table row) leaves
    /// unspecified when the scenario builds it for a Given or a When. Null takes Bobcat's default
    /// (bobcat#421).
    /// </summary>
    public IUnspecifiedValues? UnspecifiedValues
    {
        get => _state.UnspecifiedValues;
        set => _state.UnspecifiedValues = value;
    }

    /// <summary>What the last act did.</summary>
    public CritterStackActOutcome LastAct
    {
        get => _state.LastAct;
        protected set
        {
            _state.LastAct = value ?? CritterStackActOutcome.None;
            _state.Acted = !ReferenceEquals(_state.LastAct, CritterStackActOutcome.None);
            recordTimedOutAct(_state.LastAct);
        }
    }

    /// <summary>
    /// bobcat#673. An act that timed out fails its own When step, as an error naming the timeout. Every act sets
    /// <see cref="LastAct" /> while its When step is open — this scenario's and a derived one's (a tracked session's
    /// timeout) alike — so the step open now is the act's. Any other failure stays captured on the outcome only, for
    /// a Then step to judge: a refusal that throws is what <see cref="ThenRefusedWith(string)" /> asserts, and a red
    /// When there would fail the specification that expects it. A timeout is nothing a specification expects.
    /// </summary>
    private void recordTimedOutAct(CritterStackActOutcome outcome)
    {
        if (outcome.Error is not { } error) return;
        if (exceptionsIn(error).OfType<TimeoutException>().FirstOrDefault() is not { } timeout) return;
        if (ScenarioRecorder.CurrentStep is not { Failure: null } step) return;

        step.Failure = timeout as ActTimedOutException ?? new ActTimedOutException(timeout, actTimeoutHint(timeout));
    }

    /// <summary>
    /// bobcat#673. A sentence on why an act of this scenario may have run out of time, appended to the
    /// <see cref="ActTimedOutException" /> its When step carries, or null for none. A derived scenario whose act runs
    /// under a timeout of its own says which setting raises it, and what commonly costs the time.
    /// </summary>
    protected virtual string? actTimeoutHint(TimeoutException timeout) => null;

    /// <summary>
    /// The application's event store — the default one, when it has several (the constructor's <c>storeName</c>,
    /// else the one main store among them, see <see cref="CritterStackHostExtensions.MainEventStores" />), or the
    /// one a view from <see cref="In{TStore}" /> / <see cref="In(string)" /> is pinned to. Throws, saying so, when
    /// it has none, or several and no default.
    /// </summary>
    /// <remarks>
    /// Since bobcat#504 the steps do not all use it: on a host with several stores each step goes to the store
    /// whose registrations claim its types, and only falls back to this one.
    /// </remarks>
    public IEventStore EventStore => _pinned ?? (_store ??= Services.EventStore(_storeName));

    /// <summary>Whether the application has an event store at all. An application with none can still act.</summary>
    protected bool hasStore()
    {
        if (_pinned is not null) return true;
        if (_hasStore is { } known) return known;

        try
        {
            _store ??= Services.EventStore(_storeName);
            _hasStore = true;
        }
        catch (Exception)
        {
            _hasStore = false;
        }

        return _hasStore.Value;
    }

    // ---- several stores (bobcat#504) --------------------------------------------------------

    /// <summary>
    /// bobcat#504. The same Given/Then vocabulary against the event store registered under the marker interface
    /// <typeparamref name="TStore" /> — Marten's <c>AddMartenStore&lt;IInvoicingStore&gt;()</c>, Polecat's
    /// <c>AddPolecatStore&lt;T&gt;()</c>, Fisher's <c>AddFisherStore&lt;T&gt;()</c>. The view shares this
    /// specification: what it arranges is this scenario's, an act through it is this scenario's act, and every
    /// step renders in the same specification.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TStore" /> is resolved from the container. Where what it resolves to is not itself the
    /// <see cref="IEventStore" /> (Fisher registers a proxy under the marker), the store whose
    /// <see cref="IEventStore.Identity" /> name is the marker's type name — the name all three stores give an
    /// ancillary store unless told otherwise — is used instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No store is registered under <typeparamref name="TStore" />.</exception>
    public CritterStackScenario In<TStore>() where TStore : class => createView(resolveStore(typeof(TStore)));

    /// <summary>
    /// bobcat#504. The same Given/Then vocabulary against the event store whose <see cref="IEventStore.Identity" />
    /// name is <paramref name="storeName" /> (case-insensitive) — <c>"main"</c> for Marten's primary store, the
    /// marker type's name for an ancillary one. The view shares this specification; see <see cref="In{TStore}" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">No store has that name; the message lists the ones there are.</exception>
    public CritterStackScenario In(string storeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        return createView(Services.EventStore(storeName));
    }

    /// <summary>
    /// bobcat#504. Build the view <see cref="In{TStore}" /> and <see cref="In(string)" /> hand out, pinned to
    /// <paramref name="store" />. A derived scenario whose own steps should be on the view overrides this and
    /// returns its own type, built with the protected view constructor.
    /// </summary>
    protected virtual CritterStackScenario createView(IEventStore store)
    {
        _state.Touched.Add(store);
        return new(this, store);
    }

    private IEventStore resolveStore(Type marker)
    {
        var stores = allStores();
        var resolved = Services.GetService(marker);
        if (resolved is IEventStore direct) return stores.FirstOrDefault(x => ReferenceEquals(x, direct)) ?? direct;

        var named = stores.Where(x => identityName(x) is { } name && string.Equals(name, marker.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 1) return named[0];

        throw new InvalidOperationException(
            $"No event store is registered under {marker.Name}" +
            (resolved is null ? "" : $" ({marker.Name} resolves to a {resolved.GetType().Name}, which is not a JasperFx.Events.IEventStore)") +
            $", and no registered store is named '{marker.Name}'. Known: {string.Join(", ", stores.Select(EventStores.Describe))}. " +
            "Address it by name with In(\"name\") instead.");
    }

    private static string? identityName(IEventStore store)
    {
        try
        {
            return store.Identity.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- one tenant (bobcat#536) -------------------------------------------------------------

    /// <summary>
    /// bobcat#536. The tenant this view reads and writes as (<see cref="ForTenant" />), or null for the store's
    /// default session.
    /// </summary>
    public string? TenantId => _tenant;

    /// <summary>
    /// bobcat#536. The same Given/Then vocabulary for one tenant of a multi-tenanted store: the events a Given
    /// arranges are appended as <paramref name="tenantId" />, and an aggregate, stream, document or read model a Then
    /// reads is read as that tenant — through a session opened for it on the database it lives in
    /// (<see cref="IEventStore.FindDatabaseForTenantAsync" />, else Marten's <c>Storage.FindOrCreateDatabase</c>, else
    /// the store's only database). A projection wait before a read waits on that tenant's database. The view shares
    /// this specification exactly as <see cref="In{TStore}" />'s does, and the two compose:
    /// <c>In&lt;IInvoicingStore&gt;().ForTenant("red")</c>.
    /// </summary>
    /// <remarks>
    /// Only the store's reads and writes are the tenant's. An act through the view is the specification's act and
    /// runs as it always did: send it for the tenant the way the application takes one (WolverineFx.Bobcat's
    /// <c>DeliveryOptions { TenantId = … }</c>). A store-only act's events are read back from the default session, so
    /// on a conjoined store judge them with a tenant's <c>ThenEventsOn</c>/<c>ThenAggregate</c> rather than
    /// <c>ThenEvents</c>.
    /// </remarks>
    public CritterStackScenario ForTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return createTenantView(tenantId);
    }

    /// <summary>
    /// bobcat#536. Build the view <see cref="ForTenant" /> hands out. A derived scenario whose own steps should be on
    /// the view overrides this and returns its own type, built with the protected tenant view constructor.
    /// </summary>
    protected virtual CritterStackScenario createTenantView(string tenantId) => new(this, tenantId);

    // A step's text, saying whose data it is when a tenant view takes it
    private string asTenant(string text) => _tenant is null ? text : $"{text} for tenant \"{_tenant}\"";

    // Every store the host registers, read once per specification
    private IReadOnlyList<IEventStore> allStores() => _state.Stores ??= Services.EventStores();

    /// <summary>
    /// bobcat#504. The store a step about <paramref name="model" /> — an aggregate, projected or document type, or
    /// null — whose values are <paramref name="values" /> should read or write. The pinned store on a view; the only
    /// store on a host with one, exactly as before; otherwise the one store whose registrations claim the model,
    /// else its events' or tags' types, else the default store (<see cref="EventStore" />) when it can take them.
    /// Throws a <see cref="SpecCriticalException" /> naming the candidates and <see cref="In{TStore}" /> when
    /// nothing decides. Cached for the specification.
    /// </summary>
    /// <param name="step">The step, as the failure names it.</param>
    /// <param name="model">The aggregate, projected or document type the step is about, when it has one.</param>
    /// <param name="values">Events (whole, partial or tagged), or the types of events, whose types and tags help decide.</param>
    /// <param name="tagTypes">DCB tag types the step names, such as a tag query's.</param>
    protected Task<IEventStore> storeForAsync(string step, Type? model, IEnumerable<object>? values = null,
        IEnumerable<Type>? tagTypes = null)
    {
        if (_pinned is not null) return Task.FromResult(_pinned);
        if (allStores().Count <= 1) return Task.FromResult(EventStore);

        return routeAsync(step, model, values, tagTypes);
    }

    private async Task<IEventStore> routeAsync(string step, Type? model, IEnumerable<object>? values, IEnumerable<Type>? tagTypes)
    {

        var events = new List<Type>();
        var tags = new List<Type>(tagTypes ?? []);
        foreach (var value in values ?? [])
        {
            if (value is Type type)
            {
                events.Add(type);
                continue;
            }

            events.Add(PartialMatching.ExpectedType(value));
            tags.AddRange(Tagging.ExpectedTagsOf(value).Select(x => x.GetType()));
        }

        var store = await router().RouteAsync(step, model, events, tags);
        _state.Touched.Add(store);
        return store;
    }

    private StoreRouter router() => _state.Router ??= new StoreRouter(allStores(), defaultStore());

    // The constructor's storeName, else the one main store among several, else none
    private IEventStore? defaultStore()
        => _storeName is not null
            ? Services.EventStore(_storeName)
            : CritterStackHostExtensions.DefaultEventStore(Services, allStores());

    /// <summary>
    /// bobcat#504. The stores an act is observed on: every store the host registers, or — on a host with one or
    /// none — <see cref="EventStore" /> when there is one, exactly as before.
    /// </summary>
    protected IReadOnlyList<IEventStore> storesToObserve()
    {
        var root = _root ?? this;
        var stores = root.allStores();
        if (stores.Count > 1) return stores;
        return root.hasStore() ? [root.EventStore] : [];
    }

    /// <summary>
    /// bobcat#504. A high-water mark on every store <see cref="storesToObserve" /> returns, taken before an act so
    /// <see cref="readBackSinceAsync" /> can read what each one gained.
    /// </summary>
    protected async Task<IReadOnlyList<StoreHighWaterMark>> highWaterMarksAsync()
    {
        var marks = new List<StoreHighWaterMark>();
        foreach (var store in storesToObserve())
            marks.Add(new StoreHighWaterMark(store, await EventStores.HighWaterSequenceAsync(store)));

        return marks;
    }

    /// <summary>Clear the event store and forget the last act, for a suite that shares one host across scenarios.</summary>
    public virtual async Task ResetAsync(CancellationToken token = default)
    {
        if (Services.EventStores().Count > 0) await Services.ResetEventStoresAsync(token);
        _state.Stream = null;
        _state.OwnStreams.Clear();
        _state.ArrangedTags.Clear();
        // bobcat#504: what each store registers is read afresh for the next specification
        _state.Router = null;
        _state.Touched.Clear();
        LastAct = CritterStackActOutcome.None;
    }

    /// <summary>
    /// The exception a wrong is thrown as when no scenario is recording to gather it. A
    /// <see cref="SpecAssertionException" /> here; a derived scenario names its own.
    /// </summary>
    protected virtual Exception failureOf(string message)
        => _root is not null ? _root.failureOf(message) : new SpecAssertionException(message);

    // ---- arrange ---------------------------------------------------------------------------

    /// <summary>
    /// <see cref="GivenEvents{TAggregate}(Guid, object[])" /> for a stream identified by a strong-typed id around a
    /// Guid or a string (wolverine#4865), which reads by its own name.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents<TAggregate>(object id, params object[] events) where TAggregate : class
        => GivenEvents(typeof(TAggregate), id, events);

    /// <summary>The stream the scenario acts against already holds exactly these events.</summary>
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => GivenEvents(typeof(TAggregate), id, events);

    /// <inheritdoc cref="GivenEvents{TAggregate}(Guid, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents<TAggregate>(string key, params object[] events) where TAggregate : class
        => GivenEvents(typeof(TAggregate), key, events);

    /// <summary>
    /// The stream the scenario acts against already holds exactly these events, and has no aggregate
    /// type. Marten, Polecat and Fisher all allow a stream without one; this is the arrange for an event
    /// model that names none (GH-4892). Starts the stream when it does not exist, appends when it does.
    /// </summary>
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents(Guid id, params object[] events) => GivenEvents((Type?)null, id, events);

    /// <inheritdoc cref="GivenEvents(Guid, object[])" />
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents(string key, params object[] events) => GivenEvents((Type?)null, key, events);

    /// <inheritdoc cref="GivenEvents{TAggregate}(Guid, object[])" />
    /// <remarks>A null <paramref name="aggregate" /> arranges a stream with no aggregate type (GH-4892).</remarks>
    [EventModelRole(SliceRole.Aggregate, nameof(aggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEvents(Type? aggregate, object id, params object[] events)
        => arrange(aggregate, id, events, isContext: false);

    // GivenEvents and GivenContext: the same step and the same append. Only an arranged stream the act decides
    // against becomes "the stream the scenario is about"; a lookup arranged after it must not take its place
    private async Task arrange(Type? aggregate, object id, object[] events, bool isContext)
    {
        var stream = streamName(aggregate, id);
        events = flatten(events);
        var (described, inline) = describe(events);
        var text = asTenant(events.Length == 0 ? $"{stream} has no events yet" : $"{stream} has already recorded {described}");
        await inStep(ScenarioRecorder.Step("Given", text), async () =>
        {
            // wolverine#4865: a strong-typed id is the Guid or string it wraps, to the store
            var identity = StrongTypedIds.StreamIdentity(id);
            if (!isContext || _state.Stream is null) _state.Stream = identity;
            claimStream(identity);
            claimTags(events);
            if (aggregate is not null) recordTouched(aggregate);
            recordTouchedExpectations(events);
            if (!inline) recordValues("event", events);
            // Every store's StartStream(Type, ...) takes a null aggregate type as an untyped stream
            if (events.Length > 0)
                await EventStoreAuthoring.AppendAsync(await storeForAsync(text, aggregate, events), aggregate!, identity, build(events, text), _tenant);
        });
    }

    /// <summary>
    /// bobcat#502. The stream the act <em>looks up</em> already holds exactly these events — the Client an
    /// <c>OpenAccount</c> handler loads to check it exists, as opposed to the Account stream the command decides
    /// against. At run time it is <see cref="GivenEvents{TAggregate}(Guid, object[])" />: the same step, the same
    /// append. The difference is the Event Model's: <typeparamref name="TContext" /> is stamped into the slice's
    /// <c>ReadsFrom</c>, never as one of its aggregates.
    /// </summary>
    /// <remarks>
    /// The arranged events are arranged events as for <see cref="GivenEvents{TAggregate}(Guid, object[])" />:
    /// nothing on a Command slice, consumed on a View one. A context stream never replaces the stream the scenario
    /// is about (the one <see cref="ThenSingleReadModel{T}" /> reads) once a <c>GivenEvents</c> has named one.
    /// </remarks>
    [EventModelRole(SliceRole.Context, nameof(TContext))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenContext<TContext>(Guid id, params object[] events) where TContext : class
        => GivenContext(typeof(TContext), id, events);

    /// <inheritdoc cref="GivenContext{TContext}(Guid, object[])" />
    [EventModelRole(SliceRole.Context, nameof(TContext))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenContext<TContext>(string key, params object[] events) where TContext : class
        => GivenContext(typeof(TContext), key, events);

    /// <summary>
    /// <see cref="GivenContext{TContext}(Guid, object[])" /> for a stream identified by a strong-typed id around a
    /// Guid or a string (wolverine#4865).
    /// </summary>
    [EventModelRole(SliceRole.Context, nameof(TContext))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenContext<TContext>(object id, params object[] events) where TContext : class
        => GivenContext(typeof(TContext), id, events);

    /// <inheritdoc cref="GivenContext{TContext}(Guid, object[])" />
    [EventModelRole(SliceRole.Context, nameof(context))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenContext(Type context, object id, params object[] events)
    {
        ArgumentNullException.ThrowIfNull(context);
        return arrange(context, id, events, isContext: true);
    }

    /// <summary>
    /// The stream the scenario acts against has no events yet. Appends nothing — it says so in the
    /// specification, and names the stream the act's events are read from.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task GivenNoEventsFor<TAggregate>(Guid id) where TAggregate : class => GivenEvents(typeof(TAggregate), id);

    /// <inheritdoc cref="GivenNoEventsFor{TAggregate}(Guid)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task GivenNoEventsFor<TAggregate>(object id) where TAggregate : class => GivenEvents(typeof(TAggregate), id);

    /// <inheritdoc cref="GivenNoEventsFor{TAggregate}(Guid)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task GivenNoEventsFor<TAggregate>(string key) where TAggregate : class => GivenEvents(typeof(TAggregate), key);

    /// <summary>
    /// Arrange events on a <em>different</em> stream from the one the act runs against — a second
    /// aggregate for a rule that spans two, or history a read model fans in from.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEventsOn<TAggregate>(object id, params object[] events) where TAggregate : class
        => GivenEventsOn(typeof(TAggregate), id, events);

    /// <inheritdoc cref="GivenEventsOn{TAggregate}(object, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEventsOn<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => GivenEventsOn(typeof(TAggregate), id, events);

    /// <inheritdoc cref="GivenEventsOn{TAggregate}(object, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenEventsOn<TAggregate>(string key, params object[] events) where TAggregate : class
        => GivenEventsOn(typeof(TAggregate), key, events);

    /// <inheritdoc cref="GivenEventsOn{TAggregate}(object, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(aggregate))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public async Task GivenEventsOn(Type aggregate, object id, params object[] events)
    {
        events = flatten(events);
        var (described, inline) = describe(events);
        var text = asTenant($"{streamName(aggregate, id)} has already recorded {described}");
        await inStep(ScenarioRecorder.Step("Given", text), async () =>
        {
            recordTouched(aggregate);
            recordTouchedExpectations(events);
            if (!inline) recordValues("event", events);
            claimStream(id);
            claimTags(events);
            if (events.Length > 0)
                await EventStoreAuthoring.AppendAsync(await storeForAsync(text, aggregate, events), aggregate,
                    StrongTypedIds.StreamIdentity(id), build(events, text), _tenant);
        });
    }

    /// <summary>
    /// A read model already exists: <paramref name="document" /> is stored directly. The same step as
    /// <see cref="GivenReadModel{T}(object)" />, with <typeparamref name="T" /> inferred from the
    /// document, so <c>GivenReadModel(new Flight { … })</c> needs no type argument (bobcat#619).
    /// </summary>
    [EventModelRole(SliceRole.ArrangedReadModel, nameof(T))]
    public Task GivenReadModel<T>(T document) where T : class => GivenReadModel<T>((object)document);

    /// <summary>
    /// A read model already exists, specified by only some of its members:
    /// <c>GivenReadModel(Specify&lt;Flight&gt;().With(x =&gt; x.Number, "BA117"))</c>, with
    /// <typeparamref name="T" /> inferred as the specified type rather than as <see cref="Specified{T}" />.
    /// </summary>
    [EventModelRole(SliceRole.ArrangedReadModel, nameof(T))]
    public Task GivenReadModel<T>(Specified<T> document) where T : class => GivenReadModel<T>((object)document);

    /// <summary>
    /// A read model already exists: <paramref name="document" /> is stored directly, as when an event
    /// model arranges a view rather than the events that build it.
    /// </summary>
    /// <remarks>
    /// <paramref name="document" /> may be a partial object, built with its unspecified members
    /// filled by <see cref="UnspecifiedValues" />. Storing it bypasses the projection, so a spec that
    /// is about how the view is built should arrange the events instead.
    /// <para>
    /// On the Event Model the document is the slice's read model only when the slice is a View; before a
    /// command it is a lookup the command decides against, so it is stamped into <c>ReadsFrom</c>
    /// (<see cref="SliceRole.ArrangedReadModel" />, bobcat#614).
    /// </para>
    /// </remarks>
    [EventModelRole(SliceRole.ArrangedReadModel, nameof(T))]
    public async Task GivenReadModel<T>(object document) where T : class
    {
        // bobcat#682: "there is a TodoList with Title: ..." -- not "the TodoList read model is TodoList(...)", which
        // named the type twice and called a plain document (an [Entity] lookup) a read model
        var values = document is IPartialObject partial ? PartialObjects.DescribeValues(partial) : ScenarioValues.DescribeDocument(document);
        var inline = values.Length <= ScenarioValues.InlineLimit;
        var text = asTenant(inline && values.Length > 0
            ? $"there is a {nameOf(typeof(T))} with {values}"
            : $"there is a {nameOf(typeof(T))}");
        await inStep(ScenarioRecorder.Step("Given", text), async () =>
        {
            // bobcat#618: a document too long for the line keeps its values, as a table under the step
            if (!inline) recordValues("document", [document]);

            var built = Build(document, text);
            if (built is not T)
                throw new SpecCriticalException(
                    $"'{text}': a {nameOf(PartialMatching.ExpectedType(document))} cannot be stored as the {nameOf(typeof(T))} read model.");

            recordTouched(typeof(T));
            await DocumentStores.StoreAllAsync(await storeForAsync(text, typeof(T)), typeof(T), [built], _tenant);
        });
    }

    /// <summary>
    /// How a stream reads in a step: its id is named after the aggregate (GH-4835), so a stream
    /// arranged once reads "the Order stream" — and a second one of the same type "Order #Order2".
    /// </summary>
    private static string streamName(Type? aggregate, object id)
    {
        // A stream with no aggregate type (GH-4892) reads as "the stream", or "stream 2" for a second
        var label = aggregate?.Name ?? "stream";
        if (id is string key) return $"{(aggregate is null ? "stream" : nameOf(aggregate))} \"{key}\"";

        ScenarioValues.Learn(id, label);
        var name = ScenarioValues.Format(id);
        return name == ScenarioValues.Alias(label) ? $"the {label}{(aggregate is null ? "" : " stream")}" : $"{label} {name}";
    }

    /// <summary>
    /// Values as a step's text shows them: each one in full — <c>Type(Property: value, …)</c> — when
    /// they fit on the line, otherwise only their type names, and the caller shows the values as a
    /// table under the step instead.
    /// </summary>
    protected static (string Text, bool Inline) describe(IReadOnlyList<object> values)
    {
        // A partial object reads as only the members it specifies: ShipmentConfirmed(TrackingNumber: 1Z999)
        var full = values.Any(x => x is IPartialObject or TaggedValue)
            ? string.Join(", ", values.Select(describeOne))
            : ScenarioValues.DescribeAll(values);
        return full.Length <= ScenarioValues.InlineLimit ? (full, true) : (names(values), false);
    }

    // One value as a step reads it; an event with tags says them after it (wolverine#4865)
    private static string describeOne(object value)
    {
        var described = value switch
        {
            IPartialObject specified => PartialObjects.Describe(specified),
            TaggedValue tagged => ScenarioValues.Describe(tagged.Value),
            _ => ScenarioValues.Describe(value)
        };

        var tags = Tagging.ExpectedTagsOf(value);
        return tags.Count == 0 ? described : $"{described} tagged {Tagging.Describe(tags)}";
    }

    /// <summary>
    /// The values a step was given, with each <em>list</em> of partial objects — the rows of a table
    /// through <c>PartialObjects.FromTable</c> — spread into its items, so a table and single values
    /// mix in one call.
    /// </summary>
    protected static object[] flatten(object[] values)
        => values.Any(x => x is IEnumerable<IPartialObject>)
            ? values.SelectMany(x => x is IEnumerable<IPartialObject> rows ? rows.Cast<object>() : [x]).ToArray()
            : values;

    /// <summary>A partial object built, its unspecified members filled by <see cref="UnspecifiedValues" />; anything else as it is.</summary>
    protected internal object Build(object value, string? step = null) => value switch
    {
        // wolverine#4865: an arranged event keeps the tags it names, so it is appended tagged
        IPartialObject specified when specified is ITaggedExpectation { ExpectedTags.Count: > 0 } tagged
            => Specifications.Tagged(PartialObjects.Build(specified, UnspecifiedValues, step), tagged.ExpectedTags.ToArray()),
        IPartialObject specified => PartialObjects.Build(specified, UnspecifiedValues, step),
        _ => value
    };

    private object[] build(object[] values, string step) => values.Select(x => Build(x, step)).ToArray();

    /// <summary>A type alone as an expectation: any one of them, whatever its values.</summary>
    protected static object anyOf(Type type) => new TablePartialObject(type, []);

    /// <summary>Values too long for the step's text, as a table under it: one row each, unjudged.</summary>
    protected static void recordValues(string noun, IReadOnlyList<object> values)
    {
        if (!recording || values.Count == 0) return;

        var run = new TableRun([noun, ObjectSetVerification.ValuesColumn]);
        for (var i = 0; i < values.Count; i++)
        {
            run.Cells.Add(new CellResult(noun, ResultStatus.ok, nameOf(PartialMatching.ExpectedType(values[i]))) { RowIndex = i });
            run.Cells.Add(new CellResult(ObjectSetVerification.ValuesColumn, ResultStatus.ok,
                PartialMatching.DescribeExpected(values[i])) { RowIndex = i });
        }

        run.Report();
    }

    // ---- run evidence (bobcat#465) ---------------------------------------------------------

    /// <summary>Record that the scenario observably touched <paramref name="type" />. No-op outside a recording.</summary>
    protected static void recordTouched(Type type) => ScenarioRecorder.RecordTouchedType(type);

    /// <summary>Record the runtime type of each of <paramref name="values" /> as touched.</summary>
    protected static void recordTouched(IEnumerable<object?> values)
    {
        foreach (var value in values)
        {
            if (value is not null) recordTouched(value.GetType());
        }
    }

    // An arranged value may be a partial object or a tagged one: the type it stands for is what was touched
    private static void recordTouchedExpectations(IEnumerable<object> values)
    {
        foreach (var value in values) recordTouched(PartialMatching.ExpectedType(value));
    }

    // ---- act -------------------------------------------------------------------------------

    /// <summary>
    /// Any act an application performs — a domain service, an endpoint called in-process, a background job
    /// run by hand — handed a fresh service scope from the host. What it appended is read from the store
    /// afterwards; an exception it throws is captured, not rethrown, so a refusal can be asserted with
    /// <see cref="ThenValidationFails" /> or <see cref="ThenRefusedWith(string)" />.
    /// </summary>
    /// <param name="description">How the step reads: <c>"the nightly sweep runs"</c>.</param>
    /// <param name="act">The act, given the scope's services.</param>
    [EventModelAct]
    public async Task WhenActing(string description, Func<IServiceProvider, Task> act)
    {
        ArgumentNullException.ThrowIfNull(act);
        // The act's own exception is captured, never thrown; the store reads around it can still throw
        await inStep(ScenarioRecorder.Step("When", description), () => recordActAsync(act));
    }

    /// <summary>
    /// Run <paramref name="act" /> and capture what it did. The seam every store-only act goes through:
    /// this one takes the store's high-water mark, runs the act in a new service scope, reads back every
    /// event appended since, and attributes each one to this specification or not
    /// (<see cref="attributeToSpecification" />, bobcat#503) — so <see cref="CritterStackActOutcome.NewEvents" />
    /// and <see cref="CritterStackActOutcome.AllNewEvents" /> are the same list, nothing is told apart as a
    /// cascade, and what concurrent work appended elsewhere is <see cref="CritterStackActOutcome.ForeignEvents" />.
    /// A derived scenario that can see more of the act, such as one that runs it inside a message bus's tracked
    /// session, overrides this. Never throws what the act threw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read by sequence, attributed by stream.</b> An event counts as the act's when it is on a stream this
    /// specification arranged (any Given, <c>GivenContext</c> included), addressed (<c>WhenActingOn</c>), or that one
    /// of its acts wrote to — this one's started streams included. An event on any other stream that already
    /// existed is foreign: kept aside on <see cref="CritterStackActOutcome.ForeignEvents" />, never judged.
    /// </para>
    /// <para>
    /// <b>The residual.</b> A stream another test <em>starts</em> while this act runs looks exactly like one the
    /// act started, and still counts. Whole-store assertions on a store-only act therefore still want serial
    /// execution on a shared store, or a store or tenant per test; a tracked session (WolverineFx.Bobcat's
    /// <c>WolverineScenario</c>) is what makes real parallelism safe. An event appended to another tenant's
    /// database is not seen at all.
    /// </para>
    /// <para>
    /// <b>Tags.</b> No store reads an event's tags back, so the outcome says
    /// <see cref="CritterStackActOutcome.EventTagsKnown" /> is false, a step that judges tags refuses with that
    /// reason, and a tagged (DCB) append is attributed by its stream like any other. Where a store does return
    /// tags, an event carrying a tag value this specification arranged events with counts as well.
    /// </para>
    /// </remarks>
    protected virtual async Task<CritterStackActOutcome> runActAsync(Func<IServiceProvider, Task> act)
    {
        // bobcat#504: a view's act is its specification's act, run by the scenario the view came from
        if (_root is not null) return await _root.runActAsync(act);

        var marks = await highWaterMarksAsync();

        Exception? error = null;
        try
        {
            await using var scope = Services.CreateAsyncScope();
            await act(scope.ServiceProvider);
        }
        catch (Exception e)
        {
            error = e;
        }

        // Read even after a failure: an act can commit and then throw, and what it committed is a fact
        return await readBackSinceAsync(marks, error);
    }

    /// <summary>
    /// bobcat#504. What a store-only act did, read back from every store <paramref name="marks" /> were taken on:
    /// each store's events since its mark, in sequence order, attributed to this specification or not
    /// (<see cref="attributeToSpecification" />) store by store, with the streams each one started. Events from
    /// several stores are listed store by store, in the order the stores were observed — sequences are a store's
    /// own, so there is no order across stores to keep — and <see cref="CritterStackActOutcome.StoreByEvent" />
    /// says which store each came from. A derived scenario's fallback read uses this too.
    /// </summary>
    /// <param name="marks">The high-water marks taken before the act, from <see cref="highWaterMarksAsync" />.</param>
    /// <param name="error">The exception the act raised, if any; carried on the outcome, never thrown.</param>
    protected async Task<CritterStackActOutcome> readBackSinceAsync(IReadOnlyList<StoreHighWaterMark> marks, Exception? error)
    {
        var own = new List<IEvent>();
        var foreign = new List<IEvent>();
        var started = new List<StreamAction>();

        // Only where there is a choice of store to report: a single-store outcome is exactly what it was
        var byEvent = marks.Count > 1 ? new Dictionary<IEvent, IEventStore>(ReferenceEqualityComparer.Instance) : null;

        foreach (var mark in marks)
        {
            var appended = (await EventStores.QueryEventsSinceAsync(mark.Store, mark.Sequence + 1)).OrderBy(x => x.Sequence).ToList();
            var startedHere = await startedStreamsOf(mark.Store, appended);
            var (ownHere, foreignHere) = attributeToSpecification(appended, startedHere);

            own.AddRange(ownHere);
            foreign.AddRange(foreignHere);
            if (ownHere.Count > 0) _state.Touched.Add(mark.Store);
            started.AddRange(startedHere);
            if (byEvent is not null)
            {
                foreach (var e in appended) byEvent[e] = mark.Store;
            }
        }

        return new CritterStackActOutcome(own, error)
        {
            AllNewEvents = own,
            ForeignEvents = foreign,
            StartedStreams = started,
            // Read back from the store, which returns no tags
            EventTagsKnown = false,
            StoreByEvent = byEvent ?? CritterStackActOutcome.NoStores
        };
    }

    /// <summary>
    /// bobcat#503. Mark a stream — a Guid, a string key or a strong-typed id around one — as this specification's,
    /// so a store-only act attributes the events appended to it. Every Given, <c>WhenActingOn</c> and act already
    /// does; a derived scenario's own arrange calls this for a stream it lays down some other way.
    /// </summary>
    protected void claimStream(object id) => _state.OwnStreams.Add(StrongTypedIds.StreamIdentity(id));

    // The tag values arranged events were appended with, so a store that returns tags can attribute by them
    private void claimTags(IEnumerable<object> events)
    {
        foreach (var tag in events.SelectMany(Tagging.ExpectedTagsOf)) _state.ArrangedTags.Add(tag);
    }

    /// <summary>
    /// bobcat#503. Split what a store-only act read back into this specification's events and foreign ones: an
    /// event is the specification's when its stream was arranged, addressed or written by one of its acts, when
    /// the act started that stream (<paramref name="started" />), or when it carries a tag value the
    /// specification arranged events with. The specification's streams then include every stream this act wrote
    /// to, so a second act on a stream the first one started is still attributed.
    /// </summary>
    protected (IReadOnlyList<IEvent> Own, IReadOnlyList<IEvent> Foreign) attributeToSpecification(
        IReadOnlyList<IEvent> appended, IReadOnlyList<StreamAction> started)
    {
        foreach (var stream in started) _state.OwnStreams.Add(stream.Key ?? (object)stream.Id);

        var own = new List<IEvent>();
        var foreign = new List<IEvent>();
        foreach (var e in appended)
        {
            var isOwn = _state.OwnStreams.Contains(streamOf(e))
                        || (e.Tags is { Count: > 0 } tags && tags.Any(tag => _state.ArrangedTags.Contains(tag.Value)));
            (isOwn ? own : foreign).Add(e);
        }

        foreach (var e in own) _state.OwnStreams.Add(streamOf(e));
        return (own, foreign);
    }

    private static object streamOf(IEvent e) => e.StreamKey ?? (object)e.StreamId;

    /// <summary>
    /// bobcat#503. The line a failing <em>whole-store</em> assertion adds when the last act saw events on streams
    /// this specification never arranged or started: the likeliest reason the assertion saw something it did
    /// not expect, or missed something, is another test writing to the same store. Nothing when there were none,
    /// and a passing assertion never shows it.
    /// </summary>
    protected string crossTalkSuffix()
        => LastAct.ForeignEvents.Count == 0
            ? ""
            : Environment.NewLine
              + $"cross-talk suspected: {LastAct.ForeignEvents.Count} event(s) on streams this specification never arranged "
              + "or started were appended during the act. Is another test writing to this store concurrently?";

    /// <summary>
    /// bobcat#504. The line a failing whole-store assertion adds when the last act was observed on several event
    /// stores: which store each of its events came from, store by store. Nothing on a host with one store, and a
    /// passing assertion never shows it.
    /// </summary>
    protected string storesSuffix()
    {
        if (LastAct.StoreByEvent.Count == 0) return "";

        var all = LastAct.AllNewEvents.Concat(LastAct.ForeignEvents).ToList();
        if (all.Count == 0) return Environment.NewLine + "The act was observed on every event store and appended to none.";

        var byStore = all.GroupBy(x => LastAct.StoreOf(x))
            .Select(x => $"{(x.Key is { } store ? storeLabel(store) : "an unknown store")}: {ScenarioValues.DescribeAll(x.Select(e => e.Data))}");
        return Environment.NewLine + $"Appended by store — {string.Join("; ", byStore)}";
    }

    // How a store reads in a message: its IEventStore.Identity name
    private static string storeLabel(IEventStore store) => identityName(store) is { } name ? $"'{name}'" : store.GetType().Name;

    // A stream whose first event is among those appended was started by the act. Its aggregate type comes from
    // the store's stream state: an event read back carries none (IEvent.AggregateTypeName is only filled inside
    // the async daemon), and without it every started stream would answer to every aggregate type
    private static async Task<IReadOnlyList<StreamAction>> startedStreamsOf(IEventStore store, IReadOnlyList<IEvent> appended)
    {
        var started = new List<StreamAction>();
        foreach (var e in appended.Where(x => x.Version == 1))
        {
            var action = e.StreamKey is { } key
                ? new StreamAction(key, StreamActionType.Start)
                : new StreamAction(e.StreamId, StreamActionType.Start);
            action.AggregateType = await aggregateTypeOf(store, e);
            started.Add(action);
        }

        return started;
    }

    // The aggregate type the store recorded for an event's stream, or null for a stream started without one or
    // an alias the store cannot map back to a type (Marten throws for one it has never seen)
    private static async Task<Type?> aggregateTypeOf(IEventStore store, IEvent @event)
    {
        try
        {
            var state = @event.StreamKey is { } key
                ? await EventStores.FetchStreamStateAsync(store, key)
                : await EventStores.FetchStreamStateAsync(store, @event.StreamId);
            return state?.AggregateType;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Run the act through the seam and remember it
    private async Task<CritterStackActOutcome> recordActAsync(Func<IServiceProvider, Task> act)
    {
        var outcome = await runActAsync(act);
        LastAct = outcome;
        recordTouched(outcome.NewEvents.Select(x => x.Data));
        return outcome;
    }

    // ---- a named stream, from a named version (wolverine#4938, bobcat#461) ---------------------
    // For an application that does not use Wolverine, or any act whose events nothing else can attribute:
    // the spec says WHICH stream and FROM WHICH version, and only what the stream gained past that version
    // counts. Store work only.

    /// <summary>
    /// The version the stream stands at now: its last event's version, or 0 when it has no events.
    /// Capture it before an act to name it in <see cref="ThenEventsOn{T}(Guid, long, object[])" />.
    /// Not a step: it reads, and the assertion that follows is the step.
    /// </summary>
    public Task<long> TheStreamVersion(Guid id) => streamVersion(null, id);

    /// <inheritdoc cref="TheStreamVersion(Guid)" />
    public Task<long> TheStreamVersion(string key) => streamVersion(null, key);

    /// <summary>
    /// The <typeparamref name="T" /> stream with this identity gained exactly these events, in this order,
    /// past <paramref name="afterVersion" /> — and nothing else. What it held at or before that version
    /// never counts, whoever appended it. Each expected event may be a whole object, an expected value or a
    /// partial object (<c>Specify&lt;T&gt;().With(...)</c>), judged on only the members it names.
    /// </summary>
    /// <remarks>
    /// Needs no act of this scenario's: the version is the spec's to name, or to capture with
    /// <see cref="TheStreamVersion(Guid)" /> before whatever acts. When the scenario's last act failed, the
    /// failure is reported instead of a missing event. A version past the stream's end is a
    /// <see cref="SpecCriticalException" />, never a vacuous pass.
    /// </remarks>
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task ThenEventsOn<T>(Guid id, long afterVersion, params object[] events) where T : class
        => thenEventsOn(typeof(T), id, afterVersion, flatten(events));

    /// <inheritdoc cref="ThenEventsOn{T}(Guid, long, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task ThenEventsOn<T>(string key, long afterVersion, params object[] events) where T : class
        => thenEventsOn(typeof(T), key, afterVersion, flatten(events));

    /// <summary>The <typeparamref name="T" /> stream with this identity gained nothing past <paramref name="afterVersion" />.</summary>
    public Task ThenNoEventsOn<T>(Guid id, long afterVersion) where T : class
        => thenEventsOn(typeof(T), id, afterVersion, []);

    /// <inheritdoc cref="ThenNoEventsOn{T}(Guid, long)" />
    public Task ThenNoEventsOn<T>(string key, long afterVersion) where T : class
        => thenEventsOn(typeof(T), key, afterVersion, []);

    /// <summary>
    /// One act and its verification, as one paragraph: capture the <typeparamref name="T" /> stream's
    /// version, run <paramref name="act" />, and check the stream gained exactly <paramref name="events" />
    /// past that version — <see cref="ThenEventsOn{T}(Guid, long, object[])" />'s check, with the version
    /// captured for you. No events means the act must append nothing to the stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three sentences, each with its own verdict</b>, through <see cref="Paragraph" />: "Given the
    /// Wallet stream … is at version 2", "When …", "Then WalletCredited is emitted on it". The When reads
    /// <paramref name="act" />'s description from the overload that takes one.
    /// </para>
    /// <para>
    /// The act runs through the same seam as <see cref="WhenActing" />, so it is captured into
    /// <see cref="LastAct" /> — a failing act is reported by the verification, and a later
    /// <see cref="ThenValidationFails" /> can assert on it.
    /// </para>
    /// </remarks>
    [EventModelAct]
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task WhenActingOn<T>(Guid id, Func<IServiceProvider, Task> act, params object[] events) where T : class
        => actingOn(typeof(T), id, null, act, flatten(events));

    /// <inheritdoc cref="WhenActingOn{T}(Guid, Func{IServiceProvider, Task}, object[])" />
    [EventModelAct]
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task WhenActingOn<T>(string key, Func<IServiceProvider, Task> act, params object[] events) where T : class
        => actingOn(typeof(T), key, null, act, flatten(events));

    /// <inheritdoc cref="WhenActingOn{T}(Guid, Func{IServiceProvider, Task}, object[])" />
    /// <param name="description">The act, as the When sentence says it: "the wallet is credited by hand".</param>
    [EventModelAct]
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task WhenActingOn<T>(Guid id, string description, Func<IServiceProvider, Task> act, params object[] events)
        where T : class
        => actingOn(typeof(T), id, description, act, flatten(events));

    /// <inheritdoc cref="WhenActingOn{T}(Guid, string, Func{IServiceProvider, Task}, object[])" />
    [EventModelAct]
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task WhenActingOn<T>(string key, string description, Func<IServiceProvider, Task> act, params object[] events)
        where T : class
        => actingOn(typeof(T), key, description, act, flatten(events));

    private Task thenEventsOn(Type aggregate, object id, long afterVersion, object[] expected)
    {
        var text = asTenant($"{describeEmitted(expected)} on the {nameOf(aggregate)} stream {ScenarioValues.Format(id)} after version {afterVersion}");
        return inStep(ScenarioRecorder.Step("Then", text),
            () => verifyGained(aggregate, id, afterVersion, expected));
    }

    private async Task actingOn(Type aggregate, object id, string? description, Func<IServiceProvider, Task> act,
        object[] expected)
    {
        ArgumentNullException.ThrowIfNull(act);
        recordTouched(aggregate);
        ScenarioValues.Learn(id, nameOf(aggregate));
        // bobcat#503: the stream the act is addressed to is the specification's, whoever arranged it
        claimStream(id);

        var paragraph = new Paragraph("Given");
        var version = await streamVersion(aggregate, id);
        using (paragraph.Sentence($"the {nameOf(aggregate)} stream {ScenarioValues.Format(id)} is at version {version}"))
        {
        }

        await inStep(
            paragraph.Sentence("When", description ?? $"the {nameOf(aggregate)} stream {ScenarioValues.Format(id)} is acted on"),
            () => recordActAsync(act));

        await inStep(paragraph.Sentence("Then", $"{describeEmitted(expected)} on it"),
            () => verifyGained(aggregate, id, version, expected));
    }

    private async Task verifyGained(Type aggregate, object id, long afterVersion, object[] expected)
    {
        var where = $"the {nameOf(aggregate)} stream {ScenarioValues.Format(id)} after version {afterVersion}";
        if (afterVersion < 0)
            throw new SpecCriticalException($"A stream version is never negative, so nothing can be checked on {where}.");

        if (LastAct.Error is { } error)
        {
            fail($"Expected {(expected.Length == 0 ? "no events" : describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray()))} on {where}, "
                 + $"but the act failed: {error.GetType().Name}: {error.Message}");
            return;
        }

        var stream = await fetchAsync(await storeForAsync(where, aggregate, expected), id);
        var current = stream.Count == 0 ? 0 : stream.Max(e => e.Version);

        // A version past the end makes every check vacuous: nothing can follow it yet
        if (afterVersion > current)
            throw new SpecCriticalException(
                $"The {nameOf(aggregate)} stream {ScenarioValues.Format(id)} is at version {current}, so nothing can follow "
                + $"version {afterVersion} — the version the spec names is ahead of the stream.");

        recordTouched(aggregate);
        var gained = stream.Where(e => e.Version > afterVersion).ToList();
        var actual = gained.Select(e => e.Data).ToList();

        if (expected.Length == 0)
        {
            fact(actual.Count == 0, $"Expected no events on {where}, but it gained {ScenarioValues.DescribeAll(actual)}");
            return;
        }

        var run = ObjectSetVerification.Verify(actual, expected, "event", SetMode.Ordered, tagsOf(gained));
        if (recording) run.Report();

        fact(run.Succeeded,
            (actual.Count == 0
                ? $"Expected events on {where}, but it gained none (it is still at version {current}):"
                : $"The events on {where} were not exactly these, in this order:")
            + Environment.NewLine
            + string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, "event")));
    }

    // A stream with no type to route by is read from the default store (bobcat#504); In() names another
    private async Task<long> streamVersion(Type? aggregate, object id)
    {
        var stream = await fetchAsync(await storeForAsync($"the stream {ScenarioValues.Format(id)}", aggregate), id);
        return stream.Count == 0 ? 0 : stream.Max(e => e.Version);
    }

    // A step (or a paragraph's sentence) that throws says so on its own line, then the failure carries on to
    // the runner. Every step whose body can throw while it is open goes through one of these: a bare
    // `using var step` ends as a pass whatever escapes it
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

    private static async Task<T> inStep<T>(IDisposable step, Func<Task<T>> body)
    {
        try
        {
            return await body();
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

    private static void inStep(IDisposable step, Action body)
    {
        try
        {
            body();
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

    private static string describeEmitted(object[] expected)
        => expected.Length == 0
            ? "no events are emitted"
            : $"{describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray())} {(expected.Length == 1 ? "is" : "are")} emitted";

    private Task<IReadOnlyList<IEvent>> fetchAsync(IEventStore store, object id) => id switch
    {
        Guid guid => EventStores.FetchStreamAsync(store, guid, _tenant),
        string key => EventStores.FetchStreamAsync(store, key, _tenant),
        _ => throw new ArgumentOutOfRangeException(nameof(id), $"A stream is identified by a Guid or a string, not a {id.GetType().Name}")
    };

    // ---- assert: events ----------------------------------------------------------------------

    /// <summary>
    /// The tags each event was appended with, by the event's data — strong-typed ids, as the store tagged them.
    /// </summary>
    protected static Func<object, IReadOnlyList<object>?> tagsOf(IReadOnlyList<IEvent> events)
    {
        var tags = new Dictionary<object, IReadOnlyList<object>>(ReferenceEqualityComparer.Instance);
        foreach (var e in events)
        {
            tags[e.Data] = e.Tags?.Select(x => x.Value).ToArray() ?? Array.Empty<object>();
        }

        return data => tags.GetValueOrDefault(data);
    }

    /// <summary>
    /// Whether a step about tags can be judged: false, with the reason recorded, when the act's events do not
    /// carry their tags (<see cref="CritterStackActOutcome.EventTagsKnown" />) and the step names some.
    /// </summary>
    protected bool tagsCanBeJudged(IEnumerable<object> expected, bool queryByTags = false)
    {
        if (LastAct.EventTagsKnown) return true;
        if (!queryByTags && !expected.Any(x => Tagging.ExpectedTagsOf(x).Count > 0)) return true;

        return fail("The act's events were read back from the event store, which does not return the tags they were " +
                    "appended with, so a step about tags cannot judge them. Name the stream instead — " +
                    "ThenEventsOn<TAggregate>(id, ...) — or read the boundary's model with TheDcbModel<T>(query).");
    }

    /// <summary>The act appended exactly these event types, in this order.</summary>
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEvents(params Type[] events)
    {
        using var step = ScenarioRecorder.Step("Then", $"{names(events)} {(events.Length == 1 ? "is" : "are")} emitted");
        if (!actSucceeded()) return;

        var actual = LastAct.NewEvents.Select(x => x.Data.GetType()).ToArray();
        for (var i = 0; i < Math.Max(actual.Length, events.Length); i++)
        {
            check("event", i < actual.Length ? actual[i].Name : null, i < events.Length ? events[i].Name : null, i);
        }

        fact(actual.SequenceEqual(events),
            $"Expected {describeTypes(events)} but the act appended {describeTypes(actual)}{storesSuffix()}{crossTalkSuffix()}");
    }

    /// <summary>
    /// The act appended exactly these events, in order, compared <b>structurally</b> — property by
    /// property, nested properties and collections included, never through <c>Equals</c> (GH-4835).
    /// Wrap one in <see cref="Expect.Value{T}" /> to show but not judge a member nobody controls.
    /// </summary>
    /// <remarks>
    /// An event may also be a partial object — <c>Specify&lt;T&gt;().With(x =&gt; x.Prop, value)</c>, or the
    /// rows of <c>PartialObjects.FromTable</c> — judged and shown on only the members it names (wolverine#4870).
    /// Prefer a deterministic clock to <c>Ignoring(...)</c> for a timestamp.
    /// </remarks>
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEvents(params object[] events)
        => verifyEvents(flatten(events), SetMode.Ordered, "{0} {1} emitted");

    /// <summary>
    /// The act appended exactly these events, in <b>any</b> order — whole objects, <see cref="Expect.Value{T}" />s
    /// or partial objects, as for <see cref="ThenEvents(object[])" />.
    /// </summary>
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsInAnyOrder(params object[] events)
        => verifyEvents(flatten(events), SetMode.AnyOrder, "{0} {1} emitted in any order");

    /// <summary>The act appended exactly these event types, in any order.</summary>
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsInAnyOrder(params Type[] events)
        => verifyEvents(events.Select(anyOf).ToArray(), SetMode.AnyOrder, "{0} {1} emitted in any order");

    /// <summary>
    /// The act appended these events among whatever else it appended — whole objects, <see cref="Expect.Value{T}" />s
    /// or partial objects. Nothing else is judged, or shown.
    /// </summary>
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEmitted(params object[] events)
        => verifyEvents(flatten(events), SetMode.Contains, "{0} {1} emitted");

    /// <summary>The act appended a <typeparamref name="T" />, whatever its values, among whatever else it appended.</summary>
    [EventModelRole(SliceRole.Event, nameof(T))]
    public void ThenEmitted<T>() => verifyEvents([anyOf(typeof(T))], SetMode.Contains, "{0} {1} emitted");

    /// <summary>The act appended no <typeparamref name="T" /> at all.</summary>
    public void ThenNotEmitted<T>() => verifyAbsent([typeof(T)]);

    /// <summary>
    /// The act appended nothing matching any of these — a partial object forbids only the events that
    /// agree on the members it names, and a whole object only an equal one.
    /// </summary>
    public void ThenNotEmitted(params object[] events) => verifyAbsent(flatten(events));

    /// <summary>
    /// bobcat#683. The act <em>or the work it cascaded into</em> appended these events, among whatever else was
    /// appended — read from <see cref="CritterStackActOutcome.AllNewEvents" />, where <c>ThenEmitted</c> reads only
    /// the act's own <see cref="CritterStackActOutcome.NewEvents" />. For an event a cascaded handler appended, such as
    /// one on a durable local queue that a tracked session waited for. Needs no correlation ids, unlike
    /// WolverineFx.Bobcat's <c>ThenEventuallyCaused</c>, but sees only what the act's outcome saw: for a store-only act
    /// the two lists are the same.
    /// </summary>
    /// <remarks>
    /// Stamps no Event Model role: a cascaded event usually belongs to another slice, the one whose handler appended it.
    /// </remarks>
    public void ThenCaused(params object[] events) => verifyCaused(flatten(events));

    /// <summary>bobcat#683. The act or its cascades appended a <typeparamref name="T" />, whatever its values — see <see cref="ThenCaused(object[])" />.</summary>
    public void ThenCaused<T>() => verifyCaused([anyOf(typeof(T))]);

    private void verifyCaused(object[] expected)
    {
        using var step = ScenarioRecorder.Step("Then",
            $"{describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray())} {(expected.Length == 1 ? "is" : "are")} caused");
        if (!actSucceeded() || !tagsCanBeJudged(expected)) return;

        var run = ObjectSetVerification.Verify(LastAct.AllNewEvents.Select(x => x.Data).ToArray(), expected, "event",
            SetMode.Contains, tagsOf(LastAct.AllNewEvents));
        if (recording) run.Report();

        fact(run.Succeeded, string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, "event")) + storesSuffix() + crossTalkSuffix());
    }

    private void verifyEvents(object[] expected, SetMode mode, string format)
    {
        var text = string.Format(CultureInfo.InvariantCulture, format,
            describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray()), expected.Length == 1 ? "is" : "are");
        using var step = ScenarioRecorder.Step("Then", text);
        if (!actSucceeded() || !tagsCanBeJudged(expected)) return;

        var run = ObjectSetVerification.Verify(LastAct.NewEvents.Select(x => x.Data).ToArray(), expected, "event", mode,
            tagsOf(LastAct.NewEvents));
        if (recording) run.Report();

        fact(run.Succeeded, string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, "event")) + storesSuffix() + crossTalkSuffix());
    }

    private void verifyAbsent(object[] forbidden)
    {
        using var step = ScenarioRecorder.Step("Then",
            $"{describeTypes(forbidden.Select(PartialMatching.ExpectedType).ToArray())} {(forbidden.Length == 1 ? "is" : "are")} not emitted");
        if (!actSucceeded() || !tagsCanBeJudged(forbidden)) return;

        var run = ObjectSetVerification.Absent(LastAct.NewEvents.Select(x => x.Data).ToArray(), forbidden, "event",
            tagsOf(LastAct.NewEvents));
        if (recording) run.Report();

        fact(run.Succeeded, string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, "event")) + storesSuffix() + crossTalkSuffix());
    }

    /// <summary>
    /// GH-4920. The act appended exactly these events, in order, to <typeparamref name="TAggregate" />'s stream
    /// with this identity — for a command that decides against several streams, where <c>ThenEvents</c> can
    /// only speak for the one the act addresses. Only what the act added counts, never what was arranged.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsOn<TAggregate>(object id, params object[] events) where TAggregate : class
        => verifyEventsOn(typeof(TAggregate), id, flatten(events));

    /// <inheritdoc cref="ThenNoEventsOn{TAggregate}(Guid)" />
    public void ThenNoEventsOn<TAggregate>(object id) where TAggregate : class => verifyEventsOn(typeof(TAggregate), id, []);

    /// <inheritdoc cref="ThenEventsOn{TAggregate}(object, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsOn<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => verifyEventsOn(typeof(TAggregate), id, flatten(events));

    /// <inheritdoc cref="ThenEventsOn{TAggregate}(object, object[])" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsOn<TAggregate>(string key, params object[] events) where TAggregate : class
        => verifyEventsOn(typeof(TAggregate), key, flatten(events));

    /// <summary>GH-4920. The act appended nothing to <typeparamref name="TAggregate" />'s stream with this identity.</summary>
    public void ThenNoEventsOn<TAggregate>(Guid id) where TAggregate : class => verifyEventsOn(typeof(TAggregate), id, []);

    /// <inheritdoc cref="ThenNoEventsOn{TAggregate}(Guid)" />
    public void ThenNoEventsOn<TAggregate>(string key) where TAggregate : class => verifyEventsOn(typeof(TAggregate), key, []);

    private void verifyEventsOn(Type aggregate, object id, object[] expected)
    {
        var text = expected.Length == 0
            ? $"no events are emitted on the {nameOf(aggregate)} stream {ScenarioValues.Format(id)}"
            : $"{describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray())} {(expected.Length == 1 ? "is" : "are")} emitted on the {nameOf(aggregate)} stream {ScenarioValues.Format(id)}";
        using var step = ScenarioRecorder.Step("Then", text);
        if (!actSucceeded() || !tagsCanBeJudged(expected)) return;

        var stream = StrongTypedIds.StreamIdentity(id);
        // The step names the stream, so whatever the act appended there is in scope, attributed or not (bobcat#503)
        var addressed = addressable();
        var onStream = addressed.Where(x => isOn(x, stream)).Select(x => x.Data).ToArray();
        if (expected.Length == 0)
        {
            fact(onStream.Length == 0,
                $"Expected no events on the {nameOf(aggregate)} stream {id}, but the act appended {ScenarioValues.DescribeAll(onStream)}");
            return;
        }

        if (onStream.Length == 0)
        {
            // Say where the act's events DID go, which is usually the whole answer
            // bobcat#504: with several stores, the store too
            var elsewhere = LastAct.AllNewEvents
                .GroupBy(x => (Store: LastAct.StoreOf(x), Stream: (object?)x.StreamKey ?? x.StreamId))
                .Select(x => $"{(x.Key.Store is { } store ? $"{storeLabel(store)} " : "")}{x.Key.Stream}: {ScenarioValues.DescribeAll(x.Select(e => e.Data))}")
                .ToList();
            fact(false, elsewhere.Count == 0
                ? $"Expected events on the {nameOf(aggregate)} stream {id}, but the act appended no events anywhere"
                : $"Expected events on the {nameOf(aggregate)} stream {id}, but the act appended none there. It appended to: {string.Join("; ", elsewhere)}");
            return;
        }

        verifySet("event", onStream, expected, SetMode.Ordered, tagsOf(addressed));
    }

    // Every event the act appended that a stream- or tag-addressed step may look at: the specification's own and,
    // for a store-only act, the foreign ones too, in commit order. Naming the stream is what scopes these steps
    // Several stores (bobcat#504): store by store, in sequence within each, since sequences are a store's own
    private IReadOnlyList<IEvent> addressable()
    {
        if (LastAct.ForeignEvents.Count == 0) return LastAct.AllNewEvents;

        var all = LastAct.AllNewEvents.Concat(LastAct.ForeignEvents);
        if (LastAct.StoreByEvent.Count == 0) return all.OrderBy(x => x.Sequence).ToList();

        return all.GroupBy(x => LastAct.StoreOf(x)).SelectMany(x => x.OrderBy(e => e.Sequence)).ToList();
    }

    /// <summary>
    /// wolverine#4865. The act appended exactly these events, in order, among those <paramref name="query" />
    /// selects by their tags — the DCB counterpart to <see cref="ThenEventsOn{TAggregate}(Guid, object[])" />,
    /// for a decision whose consistency boundary is a tag query rather than one stream. Only what the act
    /// added counts, its cascades' included.
    /// </summary>
    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEventsOn(EventTagQuery query, params object[] events)
    {
        var expected = flatten(events);
        var scope = EventStores.Describe(query);
        var text = expected.Length == 0
            ? $"no events are emitted on {scope}"
            : $"{describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray())} {(expected.Length == 1 ? "is" : "are")} emitted on {scope}";
        using var step = ScenarioRecorder.Step("Then", text);
        if (!actSucceeded() || !tagsCanBeJudged(expected, queryByTags: true)) return;

        var selected = addressable().Where(x => selects(query, x)).ToList();
        if (expected.Length == 0)
        {
            fact(selected.Count == 0,
                $"Expected no events on {scope}, but the act appended {ScenarioValues.DescribeAll(selected.Select(x => x.Data))}");
            return;
        }

        verifySet("event", selected.Select(x => x.Data).ToArray(), expected, SetMode.Ordered, tagsOf(selected));
    }

    /// <summary>wolverine#4865. The act appended nothing that <paramref name="query" /> selects.</summary>
    public void ThenNoEventsOn(EventTagQuery query) => ThenEventsOn(query);

    // Whether a tag query selects an event: one of its conditions names a tag the event carries, and
    // either no event type or the event's own
    private static bool selects(EventTagQuery query, IEvent @event)
        => @event.Tags is { Count: > 0 } tags && query.Conditions.Any(condition =>
            (condition.EventType is null || condition.EventType == @event.Data.GetType())
            && tags.Any(tag => tag.TagType == condition.TagType && Equals(tag.Value, condition.TagValue)));

    /// <summary>Whether <paramref name="event" /> is on the stream <paramref name="id" /> — a Guid or a string key.</summary>
    protected static bool isOn(IEvent @event, object? id) => id switch
    {
        Guid guid => @event.StreamId == guid,
        string key => @event.StreamKey == key,
        _ => false
    };

    /// <summary>The act appended nothing — the refusal half of a guard.</summary>
    public void ThenNoEvents()
    {
        using var step = ScenarioRecorder.Step("Then", "no events are emitted");

        // Not actSucceeded(): a refused or throwing act that appended nothing is exactly what this step passes
        if (!_state.Acted)
        {
            fail("Expected the act to append no events, but no act has run.");
            return;
        }

        fact(LastAct.NewEvents.Count == 0,
            $"Expected no events but the act appended {ScenarioValues.DescribeAll(LastAct.NewEvents.Select(x => x.Data))}{storesSuffix()}{crossTalkSuffix()}");
    }

    /// <summary>
    /// The one event of type <typeparamref name="T" /> the act appended, to assert on further. Not a
    /// step: it reads the capture, and the assertion that follows is the step. Throws unless exactly
    /// one was appended.
    /// </summary>
    [EventModelRole(SliceRole.Event, nameof(T))]
    public T TheEvent<T>()
    {
        var matching = LastAct.NewEvents.Select(x => x.Data).OfType<T>().ToArray();
        if (matching.Length == 1) return matching[0];

        throw failureOf($"Expected exactly one {nameOf(typeof(T))} but the act appended {matching.Length}{failureSuffix()}{storesSuffix()}{crossTalkSuffix()}");
    }

    /// <summary>Every event the act appended, for a test that wants to look for itself.</summary>
    public IReadOnlyList<object> TheEvents => LastAct.NewEvents.Select(x => x.Data).ToList();

    /// <summary>
    /// The act started a <typeparamref name="TAggregate" /> stream with this identity. The identity is
    /// usually the decision — a redelivered trigger that reuses an id collides instead of starting a
    /// second stream — and <c>ThenEvents</c> cannot say where its events went.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task ThenStreamIsStarted<TAggregate>(object id) where TAggregate : class => ThenStreamIsStarted(typeof(TAggregate), id);

    /// <inheritdoc cref="ThenStreamIsStarted{TAggregate}(object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task ThenStreamIsStarted<TAggregate>(Guid id) where TAggregate : class
        => ThenStreamIsStarted(typeof(TAggregate), id);

    /// <inheritdoc cref="ThenStreamIsStarted{TAggregate}(object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Task ThenStreamIsStarted<TAggregate>(string key) where TAggregate : class
        => ThenStreamIsStarted(typeof(TAggregate), key);

    /// <inheritdoc cref="ThenStreamIsStarted{TAggregate}(object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(aggregate))]
    public async Task ThenStreamIsStarted(Type aggregate, object id)
    {
        await inStep(ScenarioRecorder.Step("Then", $"a {nameOf(aggregate)} stream is started with id \"{id}\""), async () =>
        {
            if (!actSucceeded()) return;

            var events = await fetchAsync(await storeForAsync($"a {nameOf(aggregate)} stream is started", aggregate),
                StrongTypedIds.StreamIdentity(id));
            fact(events.Count > 0,
                $"Expected a {nameOf(aggregate)} stream with id {id}, but no stream exists there. The act appended its events somewhere else, or started no stream at all.");
        });
    }

    /// <summary>
    /// wolverine#4865. One paragraph, two sentences: "a <typeparamref name="TAggregate" /> stream is started" and
    /// "these events are emitted on it", for a command whose handler starts the stream with an id it assigns —
    /// read back from the act (<see cref="TheStartedStream{TAggregate}" />). Each sentence has its own verdict.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    [EventModelRole(SliceRole.Event, nameof(events))]
    public Task ThenStreamIsStartedWithEvents<TAggregate>(params object[] events) where TAggregate : class
        => streamIsStartedWithEvents(typeof(TAggregate), flatten(events));

    // A spec that knows the id already says ThenStreamIsStarted<T>(id) and ThenEventsOn<T>(id, ...); an id
    // overload here would read a lone event as the id

    private async Task streamIsStartedWithEvents(Type aggregate, object[] expected)
    {
        var paragraph = new Paragraph("Then");
        var stream = await inStep<object?>(paragraph.Sentence($"a {nameOf(aggregate)} stream is started"), async () =>
        {
            if (!actSucceeded()) return null;

            var started = startedStreamOrNull(aggregate);
            if (started is null) return null;

            ScenarioValues.Learn(started, nameOf(aggregate));
            var events = await fetchAsync(await startedStreamStoreAsync(aggregate, started), started);
            fact(events.Count > 0,
                $"Expected a {nameOf(aggregate)} stream with id {ScenarioValues.Format(started)}, but no stream exists there.");
            return started;
        });
        if (stream is null) return;

        var text = $"{describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray())} {(expected.Length == 1 ? "is" : "are")} emitted on it";
        using (paragraph.Sentence(text))
        {
            var onStream = LastAct.AllNewEvents.Where(x => isOn(x, stream)).ToList();
            verifySet("event", onStream.Select(x => x.Data).ToArray(), expected, SetMode.Ordered, tagsOf(onStream));
        }
    }

    // bobcat#504: the store the act's events on a started stream came from, else the one its type routes to
    private Task<IEventStore> startedStreamStoreAsync(Type aggregate, object stream)
    {
        var known = LastAct.AllNewEvents.Where(x => isOn(x, stream)).Select(LastAct.StoreOf).FirstOrDefault(x => x is not null);
        return known is not null && _pinned is null ? Task.FromResult(known) : storeForAsync($"a {nameOf(aggregate)} stream is started", aggregate);
    }

    // The one stream of this type the act started, or null with the reason recorded as the step's failure
    private object? startedStreamOrNull(Type aggregate)
    {
        var started = LastAct.StartedStreams.Where(x => x.AggregateType is null || x.AggregateType == aggregate).ToList();
        if (started.Count == 1) return started[0].Key ?? (object)started[0].Id;

        fail((started.Count == 0
            ? $"The act started no {nameOf(aggregate)} stream{(LastAct.StartedStreams.Count == 0 ? "; it started no stream at all" : "")}."
            : $"The act started {started.Count} {nameOf(aggregate)} streams; name the one you mean with ThenStreamIsStartedWithEvents<{nameOf(aggregate)}>(id, ...)."
              + startedByAnotherNote(started.Count)) + crossTalkSuffix());
        return null;
    }

    /// <summary>The write model, folded from its stream.</summary>
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public async Task<T?> TheAggregate<T>(Guid id) where T : class
    {
        recordTouched(typeof(T));
        return await EventStores.AggregateStreamAsync<T>(await storeForAsync($"the {nameOf(typeof(T))} aggregate", typeof(T)), id, _tenant);
    }

    /// <inheritdoc cref="TheAggregate{T}(Guid)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public async Task<T?> TheAggregate<T>(string key) where T : class
    {
        recordTouched(typeof(T));
        return await EventStores.AggregateStreamAsync<T>(await storeForAsync($"the {nameOf(typeof(T))} aggregate", typeof(T)), key, _tenant);
    }

    /// <inheritdoc cref="TheAggregate{T}(Guid)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T?> TheAggregate<T>(object id) where T : class => StrongTypedIds.StreamIdentity(id) switch
    {
        Guid guid => TheAggregate<T>(guid),
        var key => TheAggregate<T>((string)key)
    };

    /// <summary>
    /// GH-4931. The id the last act assigned to the <typeparamref name="TAggregate" /> stream it started — for
    /// a command that starts a stream, whose id a spec cannot know before the act.
    /// </summary>
    /// <remarks>
    /// A started stream's aggregate type is read back from the store's stream state. A stream started with no
    /// aggregate type, or one whose type the store cannot map back to a .NET type, answers to any
    /// <typeparamref name="TAggregate" />.
    /// </remarks>
    /// <exception cref="SpecCriticalException">The act started no such stream, or more than one.</exception>
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public Guid TheStartedStream<TAggregate>() where TAggregate : class => startedStream(typeof(TAggregate)).Id;

    /// <inheritdoc cref="TheStartedStream{TAggregate}()" />
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public string TheStartedStreamKey<TAggregate>() where TAggregate : class
        => startedStream(typeof(TAggregate)).Key
           ?? throw new SpecCriticalException($"The {nameOf(typeof(TAggregate))} stream the act started is identified by a Guid, not a string key. Use TheStartedStream<{nameOf(typeof(TAggregate))}>().");

    private StreamAction startedStream(Type aggregate)
    {
        // A stream started with no aggregate type (Storage.StartStream with events only), or one whose type the
        // store could not map back, can still be the one
        var started = LastAct.StartedStreams.Where(x => x.AggregateType is null || x.AggregateType == aggregate).ToList();
        return started.Count switch
        {
            1 => started[0],
            0 => throw new SpecCriticalException((LastAct.StartedStreams.Count == 0
                ? $"The act started no {nameOf(aggregate)} stream; it started no stream at all."
                : $"The act started no {nameOf(aggregate)} stream. It started: {string.Join(", ", LastAct.StartedStreams.Select(x => x.AggregateType?.Name ?? "(no aggregate type)"))}.")
                + crossTalkSuffix()),
            _ => throw new SpecCriticalException($"The act started {started.Count} {nameOf(aggregate)} streams, so there is no one id to name."
                                                 + startedByAnotherNote(started.Count) + crossTalkSuffix())
        };
    }

    // bobcat#503's residual: a stream another test starts during the act is indistinguishable from the act's own
    private static string startedByAnotherNote(int started)
        => started > 1
            ? " If the specification expected one, another test writing to this store may have started the others while the act ran."
            : "";

    // ---- assert: refusals --------------------------------------------------------------------

    /// <summary>
    /// The act was refused, and the refusal says <paramref name="reason" />: an HTTP endpoint's
    /// ProblemDetails, or the exception the act threw — searched with its inner exceptions, and every one
    /// of an <see cref="AggregateException" />'s, so a refusal wrapped by the act's plumbing is still found.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="reason" /> is empty or whitespace, which every refusal contains.</exception>
    public void ThenRefusedWith(string reason) => assertRefusal($"refused with \"{reason}\"", reason);

    /// <summary>
    /// The act was refused with <paramref name="reason" />, and the refusal names each of
    /// <paramref name="mentioning" />: the values an event model gives a refusal, as in
    /// <c>EmailAlreadyInUse { email: joe@example.com }</c>.
    /// </summary>
    /// <remarks>
    /// Checked against the refusal's text, an HTTP endpoint's ProblemDetails or the exception the act
    /// threw, so it holds for both and asks nothing of how the refusal is built.
    /// </remarks>
    public void ThenRefusedWith(string reason, params object[] mentioning)
    {
        requireReason(reason);
        if (mentioning.Length == 0)
        {
            ThenRefusedWith(reason);
            return;
        }

        var named = string.Join(", ", mentioning.Select(ScenarioValues.Format));
        using var step = ScenarioRecorder.Step("Then", $"refused with \"{reason}\", naming {named}");

        var refusal = refusalText(reason);
        if (refusal is null)
        {
            fail($"Expected the act to be refused with \"{reason}\", but it succeeded.");
            return;
        }

        var hasReason = reasonMatches(refusal, reason);
        cell("reason", hasReason, reason, refusal);
        var missing = new List<string>();
        foreach (var value in mentioning)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            var isNamed = refusal.Contains(text, StringComparison.Ordinal);
            cell(ScenarioValues.Format(value), isNamed, "named", isNamed ? "named" : "not named");
            if (!isNamed) missing.Add(text);
        }

        fact(hasReason && missing.Count == 0,
            $"Expected a refusal containing \"{reason}\" and naming {string.Join(", ", mentioning)}, but it was: {refusal}");
    }

    /// <summary>
    /// The act was refused by throwing a <typeparamref name="TException" />, found anywhere in the
    /// exception the act raised (an inner exception, or one of an <see cref="AggregateException" />'s),
    /// and it matches <paramref name="expected" /> when one is given: a partial object naming the
    /// members a spec is about, as in <c>Specify&lt;EmailAlreadyInUse&gt;().With(x =&gt; x.Email, …)</c>.
    /// </summary>
    public TException? ThenRefusedWith<TException>(object? expected = null) where TException : Exception
    {
        if (expected is not null and not IPartialObject)
            throw new ArgumentException(
                $"Describe the expected {typeof(TException).Name} as a partial object, Specify<{typeof(TException).Name}>().With(...): an exception is matched on the members a spec names, never built whole.",
                nameof(expected));

        var partial = expected as IPartialObject;
        var text = partial is null || partial.Values.Count == 0
            ? $"refused with {typeof(TException).Name}"
            : $"refused with {PartialObjects.Describe(partial)}";
        using var step = ScenarioRecorder.Step("Then", text);

        if (LastAct.Error is null)
        {
            fail(LastAct.Refusal is { } refusal
                ? $"Expected a {typeof(TException).Name} to be thrown, but the act was refused with a response: {refusal}"
                : $"Expected a {typeof(TException).Name} to be thrown, but the act succeeded.");
            return null;
        }

        var thrown = exceptionsIn(LastAct.Error).OfType<TException>().FirstOrDefault();
        // Matched on the search, not the name: an ArgumentNullException is the ArgumentException a spec asked for
        cell("exception", thrown is not null, typeof(TException).Name, thrown?.GetType().Name ?? LastAct.Error.GetType().Name);
        if (thrown is null)
        {
            fact(false,
                $"Expected a {typeof(TException).Name}, but the act threw {LastAct.Error.GetType().Name}: {LastAct.Error.Message}");
            return null;
        }

        if (partial is not null)
        {
            var run = PropertyCells.Verify(thrown, partial);
            if (!run.Succeeded && !recording)
                throw failureOf($"{typeof(TException).Name} did not match: {string.Join(", ", PropertyCells.Disagreeing(run))}");
        }

        return thrown;
    }

    /// <summary>The exception and every exception inside it: inner exceptions, and each of an <see cref="AggregateException" />'s.</summary>
    protected static IEnumerable<Exception> exceptionsIn(Exception exception)
    {
        yield return exception;

        IEnumerable<Exception> inner = exception switch
        {
            AggregateException aggregate => aggregate.InnerExceptions,
            { InnerException: { } one } => [one],
            _ => []
        };
        foreach (var child in inner)
        foreach (var nested in exceptionsIn(child))
            yield return nested;
    }

    /// <summary>
    /// The act failed validation with <paramref name="reason" />. The same check as
    /// <see cref="ThenRefusedWith(string)" />, worded for a command.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="reason" /> is empty or whitespace, which every refusal contains.</exception>
    public void ThenValidationFails(string reason) => assertRefusal($"validation fails with \"{reason}\"", reason);

    /// <summary>
    /// The act was cleanly refused (issue #168): it ran, nothing threw, and it appended nothing — the sad path
    /// of a handler that refuses without throwing, such as a messaging railway's <c>Stop</c>. A refusal that
    /// answers, such as an HTTP endpoint's 4xx, is refused too.
    /// </summary>
    /// <remarks>
    /// Deliberately reason-less: a clean stop carries no reason anywhere an act surfaces, so a reason clause
    /// would assert on nothing. A throwing refusal is <see cref="ThenValidationFails" /> or
    /// <see cref="ThenRefusedWith(string)" />.
    /// </remarks>
    public void ThenCommandRefused()
    {
        using var step = ScenarioRecorder.Step("Then", "the command is refused");

        if (!_state.Acted)
        {
            fail("Expected the command to be refused, but no act has run.");
            return;
        }

        if (LastAct.Error is { } error)
        {
            fail($"Expected the command to be cleanly refused, but it threw: {error.Message}. " +
                 "A throwing refusal is what ThenValidationFails or ThenRefusedWith describes.");
            return;
        }

        if (LastAct.Refusal is not null) return;

        fact(LastAct.NewEvents.Count == 0,
            $"Expected the command to be refused, but it emitted {LastAct.NewEvents.Count} event(s): " +
            ScenarioValues.DescribeAll(LastAct.NewEvents.Select(e => e.Data)) + storesSuffix() + crossTalkSuffix());
    }

    /// <summary>
    /// bobcat#683. The act succeeded: it ran, threw nothing and was not refused. The guard against a false green — a
    /// specification whose only Then steps look for something <em>absent</em> (<c>ThenNoEvents</c>,
    /// <c>ThenNotEmitted</c>) already fails on a failed act, but one with no event step at all says nothing about
    /// whether the act worked unless it says so.
    /// </summary>
    public void ThenTheActSucceeded()
    {
        using var step = ScenarioRecorder.Step("Then", "the act succeeds");
        actSucceeded();
    }

    private void assertRefusal(string text, string reason)
    {
        requireReason(reason);
        using var step = ScenarioRecorder.Step("Then", text);

        var refusal = refusalText(reason);
        if (refusal is null)
        {
            fail($"Expected the act to be refused with \"{reason}\", but it succeeded.");
            return;
        }

        var matched = reasonMatches(refusal, reason);
        cell("reason", matched, reason, refusal);
        fact(matched, $"Expected a refusal containing \"{reason}\" but it was: {refusal}");
    }

    /// <summary>
    /// The text a refusal is judged on: an answer's body, else the first message in the act's exception — or
    /// any of its inner exceptions — that names <paramref name="reason" />, else all of them. Null when the act
    /// was not refused.
    /// </summary>
    private string? refusalText(string reason)
    {
        if (LastAct.Refusal is { } answer) return answer;
        if (LastAct.Error is not { } error) return null;

        var messages = exceptionsIn(error).Select(x => x.Message).Where(x => x.Length > 0).Distinct().ToList();
        return messages.FirstOrDefault(x => reasonMatches(x, reason))
               ?? (messages.Count == 0 ? error.Message : string.Join(" | ", messages));
    }

    private static bool reasonMatches(string refusal, string reason) => refusal.Contains(reason, ReasonComparison);

    // Every refusal's text contains the empty string, so a blank reason would pass on any refusal at all
    private static void requireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A refusal's reason must say something: an empty or whitespace reason matches every refusal.",
                nameof(reason));
    }

    // ---- assert: messages --------------------------------------------------------------------

    /// <summary>
    /// The last act caused at least <paramref name="count" /> messages of type <typeparamref name="T" /> to be
    /// sent, read from <see cref="CritterStackActOutcome.MessagesSent" />. A store-only act tracks no
    /// messages, so this needs a scenario whose act does, such as WolverineFx.Bobcat's.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is less than 1, which any act satisfies.</exception>
    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessagesSent<T>(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        using var step = ScenarioRecorder.Step("Then",
            count == 1 ? $"{nameOf(typeof(T))} is sent" : $"at least {count} {nameOf(typeof(T))} are sent");
        if (!_state.Acted)
        {
            fail($"Expected {count} message(s) of type {nameOf(typeof(T))} to be sent, but no act has run.");
            return;
        }

        if (!actSucceeded()) return;

        var sent = LastAct.MessagesSent.OfType<T>().Count();
        cell("sent", sent >= count, count == 1 ? "at least one" : $"at least {count}", sent);
        fact(sent >= count, $"Expected at least {count} message(s) of type {nameOf(typeof(T))} to be sent, but {sent} were.");
    }

    // ---- assert: read models -----------------------------------------------------------------

    /// <summary>
    /// GH-4921. The aggregate of <typeparamref name="T" />'s stream with this identity, as the store serves it
    /// through <c>FetchLatest&lt;T&gt;</c>: a projected snapshot where there is one, else built from the stream,
    /// whatever the projection lifecycle. For state built from events. A plain or async-projected document
    /// is <see cref="ThenDocument{T}(object)" />. Throws when there is no such stream.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(Guid id) where T : class => thenAggregate<T>(id, null);

    /// <inheritdoc cref="ThenAggregate{T}(Guid)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(object id) where T : class => thenAggregate<T>(id, null);

    /// <inheritdoc cref="ThenAggregate{T}(Guid)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(string key) where T : class => thenAggregate<T>(key, null);

    /// <summary>
    /// GH-4921. The aggregate, as <see cref="ThenAggregate{T}(Guid)" />, judged against
    /// <paramref name="expected" />: a partial object (<c>Specify&lt;T&gt;().With(...)</c>, a table, or
    /// assertions on its members), or a whole object or <see cref="Expect.Value{T}" /> structurally.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(Guid id, object expected) where T : class => thenAggregate<T>(id, expected);

    /// <inheritdoc cref="ThenAggregate{T}(Guid, object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(object id, object expected) where T : class => thenAggregate<T>(id, expected);

    /// <inheritdoc cref="ThenAggregate{T}(Guid, object)" />
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public Task<T> ThenAggregate<T>(string key, object expected) where T : class => thenAggregate<T>(key, expected);

    private Task<T> thenAggregate<T>(object id, object? expected) where T : class
    {
        // The step opens before the load, so a missing aggregate fails a rendered step
        return inStep(ScenarioRecorder.Step("Then", asTenant(expected is null ? existsText<T>(id) : matchesText<T>(id))), async () =>
        {
            var store = await storeForAsync(existsText<T>(id), typeof(T));
            var aggregate = StrongTypedIds.StreamIdentity(id) switch
            {
                Guid guid => await EventStores.FetchLatestAsync<T>(store, guid, _tenant),
                string key => await EventStores.FetchLatestAsync<T>(store, key, _tenant),
                _ => throw new ArgumentOutOfRangeException(nameof(id), "A stream is identified by a Guid or a string")
            } ?? throw failureOf(
                $"No {nameOf(typeof(T))} with id {id}: FetchLatest found no stream there. The act appended to another stream, or none.");

            recordTouched(typeof(T));
            if (expected is not null) judge(aggregate, expected);
            return aggregate;
        });
    }

    private static string existsText<T>(object id) => $"there is a {documentNamed<T>(id)}";

    // bobcat#682: "the Wallet #fromWallet matches", so a from- and a to-wallet checked side by side can be told apart
    private static string matchesText<T>(object id) => $"the {documentNamed<T>(id)} matches";

    // A document by its id: "Product #Product", the alias marked so it cannot read as the type twice (bobcat#640)
    private static string documentNamed<T>(object id) => $"{nameOf(typeof(T))} {ScenarioValues.Format(id)}";

    /// <summary>
    /// GH-4921. The document of type <typeparamref name="T" /> with this identity, loaded with
    /// <c>LoadAsync</c> once the projections have caught up: a plain document, or a projection stored as
    /// one. An aggregate built from events is <see cref="ThenAggregate{T}(Guid)" />. The same as
    /// <see cref="ThenReadModel{T}(object)" />.
    /// </summary>
    [EventModelRole(SliceRole.Document, nameof(T))]
    public Task<T> ThenDocument<T>(object id) where T : class => ThenReadModel<T>(id);

    /// <summary>GH-4921. The document, as <see cref="ThenDocument{T}(object)" />, judged against <paramref name="expected" />.</summary>
    [EventModelRole(SliceRole.Document, nameof(T))]
    public Task<T> ThenDocument<T>(object id, object expected) where T : class => ThenReadModel<T>(id, expected);

    /// <summary>
    /// The read model with this identity, once the projections have caught up — an async projection
    /// read straight after the act reads a stale document. Throws when there is no such document,
    /// because there is nothing to hand back. Loaded with <c>LoadAsync</c>, so it is the same as
    /// <see cref="ThenDocument{T}(object)" />; for an aggregate built from events, see
    /// <see cref="ThenAggregate{T}(Guid)" /> (GH-4921).
    /// </summary>
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    public Task<T> ThenReadModel<T>(object id) where T : class => thenReadModel<T>(id, null);

    /// <summary>
    /// The read model with this identity, once the projections have caught up, judged against
    /// <paramref name="expected" />: a partial object (<c>Specify&lt;T&gt;()</c>) on only the members it
    /// names, or a whole object or <see cref="Expect.Value{T}" /> structurally. Hands the document back.
    /// </summary>
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    public Task<T> ThenReadModel<T>(object id, object expected) where T : class => thenReadModel<T>(id, expected);

    private async Task<T> thenReadModel<T>(object id, object? expected) where T : class
    {
        await projectionsCaughtUpBefore(typeof(T), id);

        // The step opens before the load, so a missing document fails a rendered step
        return await inStep(ScenarioRecorder.Step("Then", asTenant(expected is null ? existsText<T>(id) : matchesText<T>(id))), async () =>
        {
            var document = await EventStoreAuthoring.LoadDocumentAsync<T>(await storeForAsync(existsText<T>(id), typeof(T)), id, _tenant)
                           ?? throw failureOf(
                               $"No {nameOf(typeof(T))} document with id {id}. The projections caught up, so either nothing routed to this id or its identity rule differs.");
            recordTouched(typeof(T));
            if (expected is not null) judge(document, expected);
            return document;
        });
    }

    /// <summary>
    /// No read model with this identity exists once the projections have caught up — a view the
    /// events did not create, or one they deleted.
    /// </summary>
    public async Task ThenNoReadModel<T>(object id) where T : class
    {
        await projectionsCaughtUpBefore(typeof(T), id);

        await inStep(ScenarioRecorder.Step("Then", asTenant($"there is no {documentNamed<T>(id)}")), async () =>
        {
            var document = await EventStoreAuthoring.LoadDocumentAsync<T>(await storeForAsync(existsText<T>(id), typeof(T)), id, _tenant);
            fact(document is null,
                $"Expected no {nameOf(typeof(T))} document with id {id}, but there is one: {ScenarioValues.Describe(document)}");
        });
    }

    /// <summary>
    /// The <typeparamref name="T" /> of the stream this scenario is about — the one the act started, or else the
    /// one the givens arranged — as the store serves it through <c>FetchLatest&lt;T&gt;</c> once the async
    /// daemons have caught up, judged against <paramref name="expected" /> when one is given.
    /// </summary>
    /// <remarks>
    /// Only for a single-stream projection, registered or self-aggregating: a multi-stream projection has no
    /// one stream to be fetched by, so it is refused (wolverine#4865). For a document stored any other way,
    /// <see cref="ThenSingleDocument{T}" />. With no stream at all — the act started none and the givens arranged
    /// none, as in a document app — it is the one <typeparamref name="T" /> in the store (bobcat#616), as
    /// <see cref="ThenSingleDocument{T}" /> reads it.
    /// </remarks>
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    public async Task<T> ThenSingleReadModel<T>(object? expected = null) where T : class
    {
        // A multi-stream projection is the wrong step whether or not there is a stream, so this comes before
        // bobcat#616's no-stream reading (bobcat#672)
        var kind = await EventStores.ProjectionKindAsync(await storeForAsync(singleText<T>(), typeof(T)), typeof(T));
        if (kind is not null && kind != SubscriptionType.SingleStreamProjection)
            throw new SpecCriticalException(
                $"ThenSingleReadModel<{nameOf(typeof(T))}>() reads a single-stream projection, but {nameOf(typeof(T))} is a {kind}. " +
                $"Name the document instead: ThenReadModel<{nameOf(typeof(T))}>(id, ...).");

        if (scenarioStreamOrNull() is null) return await theOnly<T>(expected);

        await daemonsCaughtUpBefore(typeof(T));

        return await inStep(ScenarioRecorder.Step("Then", asTenant(expected is null ? singleText<T>() : matchesText(expected))), async () =>
        {
            var stream = scenarioStream(typeof(T), nameof(ThenSingleReadModel));
            var model = await theLatest<T>(stream)
                        ?? throw failureOf(
                            $"No {nameOf(typeof(T))} for the stream {ScenarioValues.Format(stream)}: FetchLatest found nothing there.");

            recordTouched(typeof(T));
            if (expected is not null) judge(model, expected);
            return model;
        });
    }

    private static string singleText<T>() => $"there is a {nameOf(typeof(T))} for the scenario's stream";

    /// <summary>
    /// The <typeparamref name="T" /> document stored under the id of the stream this scenario is about, loaded with
    /// <c>LoadAsync</c> once the async daemons have caught up — a projected document on Marten or Polecat — and
    /// judged against <paramref name="expected" /> when one is given.
    /// </summary>
    /// <remarks>
    /// With no stream — the act started none and the givens arranged none — it is <em>the one</em>
    /// <typeparamref name="T" /> in the store (bobcat#616): the document a document app's act created under an id
    /// the system chose (a <c>Guid.NewGuid()</c> in the endpoint, a cascaded handler, a saga). Exactly one is the
    /// subject; none or several is a wrong naming the count. Queried through <c>JasperFx.Events.Documents</c>, so
    /// it works on every store.
    /// </remarks>
    [EventModelRole(SliceRole.Document, nameof(T))]
    public async Task<T> ThenSingleDocument<T>(object? expected = null) where T : class
    {
        if (scenarioStreamOrNull() is null) return await theOnly<T>(expected);

        await daemonsCaughtUpBefore(typeof(T));

        return await inStep(ScenarioRecorder.Step("Then", asTenant(expected is null ? singleText<T>() : matchesText(expected))), async () =>
        {
            var stream = scenarioStream(typeof(T), nameof(ThenSingleDocument));
            var document = await EventStoreAuthoring.LoadDocumentAsync<T>(await storeForAsync(singleText<T>(), typeof(T)), stream, _tenant)
                           ?? throw failureOf(
                               $"No {nameOf(typeof(T))} document with id {ScenarioValues.Format(stream)}. The async daemons caught up, so nothing stored one there.");

            recordTouched(typeof(T));
            if (expected is not null) judge(document, expected);
            return document;
        });
    }

    private async Task<T?> theLatest<T>(object stream) where T : class
    {
        var store = await storeForAsync(singleText<T>(), typeof(T));
        return stream switch
        {
            Guid guid => await EventStores.FetchLatestAsync<T>(store, guid, _tenant),
            _ => await EventStores.FetchLatestAsync<T>(store, (string)stream, _tenant)
        };
    }

    // The stream a spec without an id means: the one stream the act started, else the one the givens arranged
    private object scenarioStream(Type model, string step)
        => scenarioStreamOrNull() ?? throw new SpecCriticalException(
            $"{step}<{model.Name}>() reads the stream the scenario is about, but the act started none and the givens arranged none. " +
            $"Name it: ThenReadModel<{model.Name}>(id, ...).");

    private object? scenarioStreamOrNull()
        => LastAct.StartedStreams is [var started] ? started.Key ?? (object)started.Id : _state.Stream;

    private static string onlyText<T>() => $"there is exactly one {nameOf(typeof(T))}";

    // bobcat#616: no stream to read by, so the one T in the store — the document a document app's act created
    // under an id the system chose. Waits first, as every read of a projected document does.
    private async Task<T> theOnly<T>(object? expected) where T : class
    {
        await projectionsCaughtUpBefore(typeof(T), null);

        return await inStep(ScenarioRecorder.Step("Then", asTenant(expected is null ? onlyText<T>() : matchesText(expected))), async () =>
        {
            var documents = await allDocuments<T>(onlyText<T>());
            if (documents.Count != 1)
            {
                var found = documents.Count == 0
                    ? "there is none"
                    : $"there are {documents.Count}: {ScenarioValues.DescribeAll(documents.Cast<object>().ToList())}";

                throw failureOf(
                    $"Expected exactly one {nameOf(typeof(T))} document, but {found}. The scenario has no stream to read by " +
                    $"(the act started none and the givens arranged none), so every {nameOf(typeof(T))} in the store counts.");
            }

            recordTouched(typeof(T));
            if (expected is not null) judge(documents[0], expected);
            return documents[0];
        });
    }

    /// <summary>
    /// bobcat#683. Every <typeparamref name="T" /> document in the store — for the tenant, on a tenant view — once the
    /// async daemons that build it have caught up. A query, not a step: it records nothing and judges nothing. For the
    /// documents an act created under ids the system chose, when there may be more than one; with exactly one,
    /// <see cref="ThenSingleDocument{T}" /> says so as a step. Queried through <c>JasperFx.Events.Documents</c>, so it
    /// works on every store.
    /// </summary>
    public async Task<IReadOnlyList<T>> TheDocuments<T>() where T : class
    {
        await projectionsCaughtUpBefore(typeof(T), null);
        var documents = await allDocuments<T>(documentsText<T>([]));
        recordTouched(typeof(T));
        return documents;
    }

    /// <summary>
    /// bobcat#683. The <typeparamref name="T" /> documents in the store are exactly these, in any order, once the async
    /// daemons have caught up — whole objects, <see cref="Expect.Value{T}" />s or partial objects, judged as a set: one
    /// row per document, MISSING and EXTRA named. No expectations at all means there are none. Every
    /// <typeparamref name="T" /> in the store counts, as for <see cref="ThenSingleDocument{T}" /> without a stream, so on a
    /// store other tests share, give the documents a value only this specification uses and name it in a partial.
    /// </summary>
    [EventModelRole(SliceRole.Document, nameof(T))]
    public async Task<IReadOnlyList<T>> ThenDocuments<T>(params object[] expected) where T : class
    {
        await projectionsCaughtUpBefore(typeof(T), null);
        var items = flatten(expected);

        return await inStep(ScenarioRecorder.Step("Then", asTenant(documentsText<T>(items))), async () =>
        {
            var documents = await allDocuments<T>(documentsText<T>(items));
            recordTouched(typeof(T));
            verifySet(nameOf(typeof(T)), documents.Cast<object>().ToList(), items, SetMode.AnyOrder);
            return documents;
        });
    }

    private static string documentsText<T>(object[] expected)
        => expected.Length == 0
            ? $"there are no {nameOf(typeof(T))} documents"
            : $"the {nameOf(typeof(T))} documents are {ScenarioValues.DescribeAll(expected.Select(unwrap).ToList())}";

    /// <summary>
    /// Wait until every asynchronous projection on every database has caught up, so a read model reflects the
    /// act — what the <c>WaitForNonStaleResults</c>-style helpers need first. Waits up to
    /// <see cref="ProjectionTimeout" />. On a view from <see cref="In{TStore}" />, on the store it is pinned to.
    /// On a host with several stores (bobcat#504), on the default store and every store this specification
    /// touched — a step went to it, a view was pinned to it, or an act appended its events there — so a store the
    /// specification never involves, such as an embedded CritterWatch console's, cannot hold it up. With no default
    /// store, on every store.
    /// </summary>
    public async Task AfterAsyncDaemonsCatchUpAsync()
    {
        // bobcat#536: a tenant view waits on its tenant's database
        if (_tenant is not null)
        {
            await AfterAsyncDaemonsCatchUpAsync(_tenant);
            return;
        }

        const string text = "the async daemons have caught up";
        await inStep(ScenarioRecorder.Step("Given", text), async () =>
        {
            foreach (var store in storesToWaitOn())
                await diagnosingTimeout(store, text, () => EventStores.WaitForNonStaleProjectionsAsync(store, ProjectionTimeout));
        });
    }

    /// <summary><see cref="AfterAsyncDaemonsCatchUpAsync()" /> for one tenant's database, on every store or the view's.</summary>
    public async Task AfterAsyncDaemonsCatchUpAsync(string tenantId)
    {
        var text = $"the async daemons have caught up for tenant \"{tenantId}\"";
        await inStep(ScenarioRecorder.Step("Given", text), async () =>
        {
            foreach (var store in storesToWaitOn())
                await diagnosingTimeout(store, text,
                    () => EventStores.WaitForNonStaleProjectionsAsync(store, tenantId, ProjectionTimeout));
        });
    }

    // bobcat#685: a wait that reads nothing afterwards still says which projections were behind
    private async Task diagnosingTimeout(IEventStore store, string wait, Func<Task> waiting, Type? read = null,
        object? identity = null)
    {
        try
        {
            await waiting();
        }
        catch (TimeoutException e)
        {
            throw await EventStores.DiagnoseCatchUpTimeoutAsync(store, wait, ProjectionTimeout, e, read, identity);
        }
    }

    // bobcat#504: the view's store; or, with several, the default store and every store the specification touched
    // (every store when there is no default); or (one or none) the store as before
    private IReadOnlyList<IEventStore> storesToWaitOn()
    {
        if (_pinned is not null) return [_pinned];

        var stores = allStores();
        if (stores.Count <= 1) return [EventStore];

        var fallback = defaultStore();
        if (fallback is null) return stores;

        return stores.Where(store => ReferenceEquals(store, fallback) || _state.Touched.Contains(store)).ToList();
    }

    /// <summary>There is no <typeparamref name="T" /> at all once the projections have caught up.</summary>
    public async Task ThenNoReadModel<T>() where T : class
    {
        await projectionsCaughtUpBefore(typeof(T), null);

        await inStep(ScenarioRecorder.Step("Then", asTenant($"there is no {nameOf(typeof(T))}")), async () =>
        {
            var documents = await allDocuments<T>($"there is no {nameOf(typeof(T))}");
            fact(documents.Count == 0,
                $"Expected no {nameOf(typeof(T))} document, but there {(documents.Count == 1 ? "is one" : $"are {documents.Count}")}: {ScenarioValues.DescribeAll(documents.Cast<object>().ToList())}");
        });
    }

    private async Task<IReadOnlyList<T>> allDocuments<T>(string step) where T : class
    {
        var store = await storeForAsync(step, typeof(T));
        object session = _tenant is null
            ? DocumentStores.SessionFactoryFor(store).QuerySession()
            : await DocumentStores.TenantSessionAsync(store, _tenant);
        try
        {
            var queryable = ((JasperFx.Events.Documents.IDocumentReadOperations)session).Query<T>();
            return await JasperFx.Events.Documents.DocumentQueryableExtensions.ToListAsync(queryable);
        }
        finally
        {
            if (session is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (session is IDisposable disposable) disposable.Dispose();
        }
    }

    /// <summary>
    /// The projections that build <paramref name="readModel" /> have caught up: every async daemon is waited
    /// on, up to <see cref="ProjectionTimeout" />.
    /// </summary>
    [EventModelRole(SliceRole.ReadModel, nameof(readModel))]
    public Task ThenProjectionsAreCaughtUp(Type readModel)
    {
        var text = catchUpText(readModel);
        return inStep(ScenarioRecorder.Step("Then", text), () => waitBeforeReading(text, readModel, null));
    }

    private static string catchUpText(Type read) => $"the {read.Name} read model has caught up";

    // The catch-up step every read of a projected document opens with: its timeout names what was being read.
    // Not rendered, and not waited on, when the store has no async projections (bobcat#620)
    private async Task projectionsCaughtUpBefore(Type read, object? identity)
    {
        var text = catchUpText(read);
        if (await nothingToCatchUpAsync(text, read)) return;
        await inStep(ScenarioRecorder.Step("Then", text), () => waitBeforeReading(text, read, identity));
    }

    // AfterAsyncDaemonsCatchUpAsync's step, before a read of the scenario's own stream; skipped as above
    private async Task daemonsCaughtUpBefore(Type read)
    {
        const string text = "the async daemons have caught up";
        if (await nothingToCatchUpAsync(text, read)) return;
        var stream = LastAct.StartedStreams is [var started] ? started.Key ?? (object)started.Id : _state.Stream;
        await inStep(ScenarioRecorder.Step("Given", text), () => waitBeforeReading(text, read, stream));
    }

    /// <summary>
    /// bobcat#620. True only when the store being read positively says it configures no async projection shard:
    /// then nothing can be stale, and an implicit catch-up step before every document read is noise. A store that
    /// cannot say, or a store that cannot be chosen, is "not known", so the step renders and waits (or fails on its
    /// own line) as before.
    /// </summary>
    private async Task<bool> nothingToCatchUpAsync(string wait, Type read)
    {
        try
        {
            var store = await storeForAsync(wait, read);
            return EventStoreShards.TryNames(store) is { Count: 0 };
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// bobcat#497. Wait for the async projections before a read, and when the wait times out, rethrow it as a
    /// <see cref="ProjectionCatchUpTimeoutException" /> that names the wait, the projections that were behind, the
    /// timeout and what was being read — still an error, never a specification failure: a daemon that is behind is
    /// the environment, not evidence about the document.
    /// </summary>
    private async Task waitBeforeReading(string wait, Type read, object? identity)
    {
        // bobcat#504: the store being read
        var store = await storeForAsync(wait, read);

        // bobcat#536: a tenant view reads its tenant's database, so that is the one to wait on
        await diagnosingTimeout(store, wait, () => _tenant is null
            ? EventStores.WaitForNonStaleProjectionsAsync(store, ProjectionTimeout)
            : EventStores.WaitForNonStaleProjectionsAsync(store, _tenant, ProjectionTimeout), read, identity);
    }

    /// <summary>
    /// wolverine#4865. <typeparamref name="T" /> projected over the events <paramref name="query" /> selects by
    /// their tags — a Dynamic Consistency Boundary model, or a view built the same way. Null when nothing
    /// matches the query.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public async Task<T?> TheDcbModel<T>(EventTagQuery query) where T : class
    {
        recordTouched(typeof(T));
        var store = await storeForAsync($"{nameOf(typeof(T))} is projected over {EventStores.Describe(query)}", typeof(T),
            query.Conditions.Where(x => x.EventType is not null).Select(x => (object)x.EventType!),
            query.Conditions.Select(x => x.TagType));
        return await EventStores.AggregateByTagsAsync<T>(store, query, _tenant);
    }

    /// <summary>
    /// wolverine#4865. <typeparamref name="T" />, projected over the events <paramref name="query" /> selects by
    /// their tags, is <paramref name="expected" />: a whole object, an <see cref="Expect.Value{T}" /> or a partial
    /// one, judged as <see cref="ThenMatches" /> judges.
    /// </summary>
    [EventModelRole(SliceRole.Aggregate, nameof(T))]
    public async Task ThenDcbModel<T>(EventTagQuery query, object expected) where T : class
    {
        var text = $"the {nameOf(typeof(T))} over {EventStores.Describe(query)} matches";
        var model = await inStep(ScenarioRecorder.Step("Given", $"{nameOf(typeof(T))} is projected over {EventStores.Describe(query)}"),
            () => TheDcbModel<T>(query));

        matches(text, model, expected);
    }

    // ---- assert: any object (GH-4836) --------------------------------------------------------

    /// <summary>
    /// Verify selected properties of <paramref name="subject" /> as a table — Bobcat's
    /// <c>PropertyCells</c>, one cell per column, nested paths (<c>Address.City</c>) included. A
    /// response, a read model, an aggregate, anything.
    /// </summary>
    /// <param name="subject">What is being verified.</param>
    /// <param name="expected">One header row naming the properties, one row of expected values: <c>"| Name | Address.City |\n| Ann | Austin |"</c>.</param>
    public void Verify(object subject, StepTable expected)
    {
        using var step = ScenarioRecorder.Step("Then", $"the {nameOf(subject.GetType())} has");
        var run = PropertyCells.Verify(subject, expected);
        if (!run.Succeeded && !recording)
        {
            throw failureOf($"{nameOf(subject.GetType())} did not match: {string.Join(", ", PropertyCells.Disagreeing(run))}");
        }
    }

    /// <summary>
    /// <paramref name="subject" /> is structurally equal to <paramref name="expected" /> — or to an
    /// <see cref="Expect.Value{T}" /> with members to ignore. The same rules as <see cref="ThenEvents(object[])" />.
    /// </summary>
    public void ThenMatches(object? subject, object expected)
        => matches(null, subject, expected);

    private void matches(string? text, object? subject, object expected)
    {
        // bobcat#618: a scalar is one value, not an object with a type to name -- "the value is 2",
        // judged in one cell, rather than "the Int32 matches ✓ Int32: Int32 ✓ value: 2"
        if (expected is not IPartialObject and not IExpectedValue && ScenarioValues.IsScalar(expected))
        {
            using var scalar = ScenarioRecorder.Step("Then", text ?? $"the value is {ScenarioValues.Format(expected)}");
            if (!check("value", subject, expected))
                fail($"Expected {ScenarioValues.Format(expected)} but was {ScenarioValues.Format(subject)}");
            return;
        }

        using var step = ScenarioRecorder.Step("Then", text ?? matchesText(expected));
        judge(subject, expected);
    }

    // How a step that judges an object against expected reads: a collection by its items when they fit
    // on the line ("the collection matches [\"a\", \"b\"]"), anything else by its type
    private static string matchesText(object expected)
    {
        var value = unwrap(expected);
        if (expected is not IPartialObject && value is System.Collections.IEnumerable and not string)
        {
            var items = ScenarioValues.Describe(value);
            return items.Length <= ScenarioValues.InlineLimit ? $"the collection matches {items}" : $"the {nameOf(value.GetType())} matches";
        }

        return $"the {nameOf(expected is IPartialObject partial ? partial.Type : value.GetType())} matches";
    }

    // Judge subject against expected on the step in progress: a partial object on the members it names, else structurally
    private void judge(object? subject, object expected)
    {
        if (expected is IPartialObject partial)
        {
            if (subject is null)
            {
                fail($"Expected a {nameOf(partial.Type)} but there was nothing to verify");
                return;
            }

            var run = PropertyCells.Verify(subject, partial);
            if (!run.Succeeded && !recording)
            {
                throw failureOf($"{nameOf(partial.Type)} did not match: {string.Join(", ", PropertyCells.Disagreeing(run))}");
            }

            return;
        }

        compareSequence(nameOf(unwrap(expected).GetType()), subject is null ? Array.Empty<object>() : new[] { subject }, new[] { expected });
    }

    // ---- the clock (ClockGrammars, as recorded steps) ------------------------------------------
    // Each freezes or moves the ambient BobcatClock, which is what relative TODAY/NOW cells resolve against
    // and what an application reads when its TimeProvider is BobcatClock.Current. Synchronous on purpose: the
    // clock is AsyncLocal, and a value set inside an async method never flows back to its caller.

    /// <summary>The ambient clock a step's relative values resolve against: <see cref="BobcatClock.Current" />.</summary>
    public TimeProvider Clock => BobcatClock.Current;

    /// <summary>Freeze the clock at midnight UTC on <paramref name="date" />.</summary>
    public void GivenTheDateIs(DateOnly date)
    {
        using var step = ScenarioRecorder.Step("Given", $"the date is {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        BobcatClock.Controllable().SetUtcNow(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    /// <summary>Freeze the clock at <paramref name="now" />.</summary>
    public void GivenTheTimeIs(DateTimeOffset now)
    {
        using var step = ScenarioRecorder.Step("Given", $"the current time is {formatInstant(now)}");
        BobcatClock.Controllable().SetUtcNow(now.ToUniversalTime());
    }

    /// <summary>Move the clock forward by <paramref name="duration" />.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration" /> is negative: the clock only advances.</exception>
    public void WhenTheClockAdvancesBy(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        using var step = ScenarioRecorder.Step("When", $"the clock advances by {formatDuration(duration)}");
        BobcatClock.Controllable().Advance(duration);
    }

    /// <summary><paramref name="duration" /> passes: the clock moves forward by it.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration" /> is negative: time only passes forward.</exception>
    public void WhenTimePasses(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        using var step = ScenarioRecorder.Step("When", $"{formatDuration(duration)} passes");
        BobcatClock.Controllable().Advance(duration);
    }

    /// <summary>Move the clock to <paramref name="instant" />.</summary>
    public void WhenTheClockAdvancesTo(DateTimeOffset instant)
    {
        using var step = ScenarioRecorder.Step("When", $"the clock advances to {formatInstant(instant)}");
        BobcatClock.Controllable().SetUtcNow(instant.ToUniversalTime());
    }

    // "2026-01-02", "2026-01-02 09:15", "2026-01-02 09:15:30.25": UTC, to the tick, in the form ClockGrammars
    // reads back. No zone suffix, because its parser assumes UTC and refuses one it does not know
    private static string formatInstant(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        var format = utc.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd"
            : utc.Ticks % TimeSpan.TicksPerMinute == 0 ? "yyyy-MM-dd HH:mm"
            : utc.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss"
            : "yyyy-MM-dd HH:mm:ss.FFFFFFF";
        return utc.ToString(format, CultureInfo.InvariantCulture);
    }

    // "3 days", "1 hour 30 minutes", "0.5 milliseconds": the words FriendlyTimeSpanParser reads back, to the
    // tick. Never negative; the clock steps refuse one before they get here
    private static string formatDuration(TimeSpan duration)
    {
        if (duration == TimeSpan.Zero) return "0 seconds";

        var parts = new List<string>();
        void add(decimal amount, string unit)
        {
            if (amount != 0)
                parts.Add($"{amount.ToString("0.####", CultureInfo.InvariantCulture)} {unit}{(amount == 1 ? "" : "s")}");
        }

        add(duration.Days, "day");
        add(duration.Hours, "hour");
        add(duration.Minutes, "minute");
        add(duration.Seconds, "second");
        // The milliseconds and whatever is under them: a tick is 0.0001 of a millisecond
        add((decimal)(duration.Ticks % TimeSpan.TicksPerSecond) / TimeSpan.TicksPerMillisecond, "millisecond");
        return string.Join(" ", parts);
    }

    // ---- shared ------------------------------------------------------------------------------

    private static bool recording => ScenarioRecorder.Current is not null;

    /// <summary>Report a wrong: gathered on the step in progress, or thrown when nothing records. Returns false.</summary>
    protected bool fail(string message)
    {
        if (!recording) throw failureOf(message);

        SpecAssert.Fail(message);
        return false;
    }

    /// <summary>Report <paramref name="because" /> unless <paramref name="condition" /> holds.</summary>
    protected bool fact(bool condition, string because) => condition || fail(because);

    // A judged cell: expected against actual, recorded on the step in progress
    private static bool check(string name, object? actual, object? expected, int rowIndex = -1)
    {
        var matched = ObjectComparison.LeafEquals(actual, expected);
        cell(name, matched, expected, actual, rowIndex);
        return matched;
    }

    private static void cell(string name, bool matched, object? expected, object? actual, int rowIndex = -1,
        string? note = null)
    {
        if (!recording) return;

        ScenarioRecorder.RecordCell(new CellResult(name, matched ? ResultStatus.success : ResultStatus.failed)
        {
            Expected = ObjectComparison.Format(expected),
            Actual = ObjectComparison.Format(actual),
            RowIndex = rowIndex,
            Note = note
        });
    }

    private static void valueCell(string name, object? actual, int rowIndex = -1)
    {
        if (!recording) return;
        ScenarioRecorder.RecordCell(new CellResult(name, ResultStatus.ok, ObjectComparison.Format(actual))
        {
            RowIndex = rowIndex
        });
    }

    private static bool row(IReadOnlyList<ObjectComparison.Leaf> leaves, int rowIndex)
    {
        foreach (var leaf in leaves)
        {
            if (leaf.Ignored) valueCell(leaf.Path, leaf.Actual, rowIndex);
            else cell(leaf.Path, leaf.Matched, leaf.Expected, leaf.Actual, rowIndex, leaf.Note);
        }

        return leaves.All(x => x.Matched);
    }

    private static string describeLeaves(IEnumerable<ObjectComparison.Leaf> leaves)
        => string.Join("; ", ValueDifferences.Sentences(leaves
            .Where(x => !x.Matched)
            .Select(x => new ValueDifference(x.Path, x.Expected, x.Actual, x.Note))
            .ToList()));

    private void compareSequence(string noun, IReadOnlyList<object> actual, IReadOnlyList<object> expected)
    {
        var problems = new List<string>();

        for (var i = 0; i < Math.Max(actual.Count, expected.Count); i++)
        {
            var expectedValue = i < expected.Count ? unwrap(expected[i]) : null;
            var actualValue = i < actual.Count ? actual[i] : null;

            check(noun, actualValue is null ? null : nameOf(actualValue.GetType()), expectedValue is null ? null : nameOf(expectedValue.GetType()), i);

            if (expectedValue is null)
            {
                problems.Add($"[{i}] an extra {nameOf(actualValue!.GetType())}");
                continue;
            }

            if (actualValue is null)
            {
                problems.Add($"[{i}] {nameOf(expectedValue.GetType())} is missing");
                continue;
            }

            if (actualValue.GetType() != expectedValue.GetType())
            {
                problems.Add($"[{i}] expected {nameOf(expectedValue.GetType())} but was {nameOf(actualValue.GetType())}");
                continue;
            }

            var leaves = ObjectComparison.Compare(actualValue, expectedValue,
                (expected[i] as IExpectedValue)?.Rules ?? ComparisonRules.None);
            if (!row(leaves, i)) problems.Add($"[{i}] {nameOf(expectedValue.GetType())}: {describeLeaves(leaves)}");
        }

        fact(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>An expected value's value; anything else as it is.</summary>
    protected static object unwrap(object value) => value is IExpectedValue expected ? expected.Value : value;

    /// <summary>
    /// <paramref name="actual" /> against <paramref name="expected" /> as a set verification (GH-4835):
    /// one grid with a row per item — OK, a FAIL naming the values that disagree, MISSING, EXTRA, or
    /// ORDER — rather than a positional comparison that reads one missing event as every later one wrong.
    /// </summary>
    protected void verifySet(string noun, IReadOnlyList<object> actual, IReadOnlyList<object> expected, SetMode mode,
        Func<object, IReadOnlyList<object>?>? tags = null)
    {
        var run = tags is null
            ? ObjectSetVerification.Verify(actual, expected, noun, mode)
            : ObjectSetVerification.Verify(actual, expected, noun, mode, tags);

        if (recording) run.Report();

        fact(run.Succeeded, string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, noun)));
    }

    /// <summary>
    /// A failure the step did not ask about is reported as the act failing, rather than as "expected 1
    /// event, got 0" — which would send the reader to the handler's logic when the answer is in the
    /// exception the act captured. No act at all is a failure too, so a forgotten When cannot pass a step
    /// that looks for nothing.
    /// </summary>
    protected bool actSucceeded()
    {
        // Without an act every "what the act did" step would pass on the empty outcome
        if (!_state.Acted)
        {
            return fail("Expected an act to judge, but no act has run. A step about what the act did needs a When first.");
        }

        if (LastAct.Error is { } error)
        {
            return fail($"The act failed: {error.GetType().Name}: {error.Message}");
        }

        if (LastAct.Refusal is { } refusal)
        {
            return fail($"The act was refused: {refusal}");
        }

        return true;
    }

    /// <summary>What the act's failure or refusal was, as a sentence's tail, or nothing when it succeeded.</summary>
    protected string failureSuffix()
        => LastAct.Error is { } e ? $". The act failed: {e.GetType().Name}: {e.Message}"
            : LastAct.Refusal is { } r ? $". The act was refused: {r}" : "";

    private static string names(IReadOnlyList<object> values) => describeTypes(values.Select(PartialMatching.ExpectedType).ToArray());

    private static string names(Type[] types) => describeTypes(types);

    /// <summary>A type as a step reads it: <c>List&lt;Account&gt;</c>, never <c>List`1</c> (bobcat#618).</summary>
    protected static string nameOf(Type type) => ScenarioValues.TypeName(type);

    /// <summary>Type names as a step reads them: <c>A, B</c>, or <c>nothing</c>.</summary>
    protected static string describeTypes(IReadOnlyList<Type> types)
        => types.Count == 0 ? "nothing" : string.Join(", ", types.Select(nameOf));
}
