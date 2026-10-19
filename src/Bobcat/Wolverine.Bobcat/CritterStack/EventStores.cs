using System.Diagnostics;
using System.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Tags;

namespace Bobcat.CritterStack;

/// <summary>
/// The store-agnostic binding: every event-store operation Bobcat's Critter Stack helpers need,
/// expressed against the <c>JasperFx.Events</c> abstractions an <see cref="IEventStore"/> exposes
/// rather than against Marten, Polecat or Fisher. Marten, Polecat and Fisher all register their
/// store as <see cref="IEventStore"/> in the host container, which is how
/// <see cref="CritterStackHostExtensions"/> finds one without a compile-time reference to any of
/// them.
/// </summary>
/// <remarks>
/// <para>
/// Two operations have no JasperFx.Events abstraction in the version Bobcat pins (2.53.0), and are
/// reached through the convention the three stores share rather than through an interface:
/// </para>
/// <list type="bullet">
/// <item><b>Rebuilding an aggregate</b> — <see cref="IReadOnlyEventStore"/> can fetch a stream but not
/// aggregate it. Marten and Polecat hand back their session's <see cref="IQueryEventStore"/> from
/// <see cref="IEventStore.OpenReadOnlyEventStore"/>, which can; Fisher does not, so the fallback opens
/// a session through the store's <c>IEventStore&lt;TOperations, TQuerySession&gt;</c> closure and finds
/// the session's <c>Events</c> member. One reflective lookup, cached per store type.</item>
/// <item><b>Wiping data between scenarios</b> — JasperFx's <c>IStatefulResource.ClearState</c> is the
/// abstraction, but Marten 9.22's database does not implement <c>IDatabaseWithRewindableState</c>, so
/// <c>jasperfx resources clear</c> is a no-op for it. The stores do agree on
/// <c>store.Advanced.ResetAllData(ct)</c> (Fisher spells it <c>ResetAllDataAsync</c>) and on
/// <c>store.Advanced.Clean.DeleteAllEventDataAsync()</c> / <c>DeleteAllDocumentsAsync()</c>, so
/// <see cref="ResetAllDataAsync"/> follows that convention and says exactly what it looked for when a
/// store matches neither.</item>
/// </list>
/// <para>
/// Both are the bounded, documented softening of "bind to the abstractions" — the same bargain as
/// <c>GrammarBehaviors.Resolve</c> in core. When JasperFx.Events grows the abstraction, the
/// convention path is the one line to delete. Re-checked against 2.53.0 (issue #103): neither has
/// landed, and both branches are now proved on the real stores — Marten takes the
/// <see cref="IQueryEventStore"/> / <c>ResetAllData</c> branch, Fisher the session-closure /
/// <c>ResetAllDataAsync</c> one.
/// </para>
/// <para>
/// <b>Why the projection waits do not delegate to <c>ProjectionScenario&lt;,&gt;</c></b>
/// (<c>JasperFx.Events.TestSupport</c>, in the pinned version): it is a scripted harness that wipes
/// the store, builds its <i>own</i> daemon and appends through its <i>own</i> session, reached only
/// through each store's <c>Advanced</c> member and closed over that store's session pair — there is
/// no non-generic interface and no <see cref="IEventStore"/> accessor. A spec appends through the
/// application under test and waits on the host's daemon, so the right tool is the wait the
/// scenario itself performs after each batch, which is what <see cref="WaitForNonStaleProjectionsAsync"/>
/// calls directly.
/// </para>
/// </remarks>
public static class EventStores
{
    public static readonly TimeSpan DefaultProjectionTimeout = TimeSpan.FromSeconds(5);

    // --- Reading --------------------------------------------------------------------------

    /// <summary>All events in a <see cref="Guid"/>-identified stream, through the store's read-only view.</summary>
    public static Task<IReadOnlyList<IEvent>> FetchStreamAsync(IEventStore store, Guid streamId, CancellationToken token = default)
        => fetchStreamAsync(store, reader => reader.FetchStreamAsync(streamId, token: token));

    /// <summary>All events in a string-keyed stream, through the store's read-only view.</summary>
    public static Task<IReadOnlyList<IEvent>> FetchStreamAsync(IEventStore store, string streamKey, CancellationToken token = default)
        => fetchStreamAsync(store, reader => reader.FetchStreamAsync(streamKey, token: token));

    /// <summary>
    /// bobcat#536. All events in a <see cref="Guid"/>-identified stream as <paramref name="tenantId"/> sees it, read
    /// through a session opened for that tenant on the database that holds it (<see cref="OpenTenantSessionAsync"/>).
    /// A null tenant is <see cref="FetchStreamAsync(IEventStore, Guid, CancellationToken)"/>.
    /// </summary>
    public static Task<IReadOnlyList<IEvent>> FetchStreamAsync(IEventStore store, Guid streamId, string? tenantId,
        CancellationToken token = default)
        => tenantId is null
            ? FetchStreamAsync(store, streamId, token)
            : inTenantAsync(store, tenantId, events => events.FetchStreamAsync(streamId, token: token));

    /// <inheritdoc cref="FetchStreamAsync(IEventStore, Guid, string?, CancellationToken)"/>
    public static Task<IReadOnlyList<IEvent>> FetchStreamAsync(IEventStore store, string streamKey, string? tenantId,
        CancellationToken token = default)
        => tenantId is null
            ? FetchStreamAsync(store, streamKey, token)
            : inTenantAsync(store, tenantId, events => events.FetchStreamAsync(streamKey, token: token));

    /// <summary>
    /// Every event in the store at or after <paramref name="sequenceFloor"/>, across all streams
    /// (issue #319).
    /// </summary>
    /// <remarks>
    /// The escape for a slice whose act CREATES a stream. Every other assertion reads one stream,
    /// because the scenario named it — but a creating slice's stream id is the handler's to mint,
    /// so there is nothing to name and nothing to read. A sequence floor taken before the act and
    /// queried after is what the store can answer instead.
    ///
    /// Whole-store rather than per-stream, so it is only ever used where the alternative is
    /// nothing at all: <see cref="CritterStackScenario"/> reaches for it exactly when no stream was
    /// bracketed, which today yields an empty list and an assertion that cannot fail.
    /// </remarks>
    public static Task<IReadOnlyList<IEvent>> QueryEventsSinceAsync(
        IEventStore store, long sequenceFloor, CancellationToken token = default)
        => fetchStreamAsync(store, async reader =>
        {
            // PageSize is deliberately large rather than paged: this only ever runs against a
            // scenario's own store, whose contents are what that scenario arranged. A silent
            // truncation here would read as "the slice emitted nothing", which is the failure this
            // whole method exists to stop telling.
            var page = await reader
                .QueryEventsAsync(new EventQuery { SequenceFloor = sequenceFloor, PageSize = int.MaxValue }, token)
                .ConfigureAwait(false);

            return (IReadOnlyList<IEvent>)page.Events.ToList();
        });

    /// <summary>
    /// The highest sequence the store has issued, or 0 for an empty store — the mark
    /// <see cref="QueryEventsSinceAsync"/> counts from.
    /// </summary>
    /// <remarks>
    /// Two small reads through the same <see cref="IReadOnlyEventStore.QueryEventsAsync"/> the act's events are
    /// read back with, rather than loading every event: a one-event page for the count, then the last one-event
    /// page, which the sequence-ascending ordering contract makes the highest. Not
    /// <c>IEventDatabase.FetchHighestEventSequenceNumber</c>, which is per database (a multi-tenant store has
    /// several, and the floor must come from the one the read-back reads) and not implemented by every
    /// Marten database. Concurrent appends between the two reads can only lower the floor, which counts them
    /// as the act's — the whole-store caveat the act already carries.
    /// </remarks>
    public static Task<long> HighWaterSequenceAsync(IEventStore store, CancellationToken token = default)
        => readAsync(store, async reader =>
        {
            var first = await reader.QueryEventsAsync(new EventQuery { PageSize = 1 }, token).ConfigureAwait(false);
            if (first.TotalCount == 0) return 0L;

            var last = await reader.QueryEventsAsync(new EventQuery { PageSize = 1, PageNumber = first.TotalCount }, token)
                .ConfigureAwait(false);
            return last.Events.Count == 0 ? first.Events.Max(x => x.Sequence) : last.Events.Max(x => x.Sequence);
        });

    /// <summary>
    /// A <see cref="Guid"/>-identified stream's state — version and the aggregate type the store recorded for
    /// it — or null when there is no such stream.
    /// </summary>
    public static Task<StreamState?> FetchStreamStateAsync(IEventStore store, Guid streamId, CancellationToken token = default)
        => readAsync(store, reader => reader.FetchStreamStateAsync(streamId, token));

    /// <inheritdoc cref="FetchStreamStateAsync(IEventStore, Guid, CancellationToken)"/>
    public static Task<StreamState?> FetchStreamStateAsync(IEventStore store, string streamKey, CancellationToken token = default)
        => readAsync(store, reader => reader.FetchStreamStateAsync(streamKey, token));

    /// <summary>Rebuild the aggregate of a <see cref="Guid"/>-identified stream from its events.</summary>
    /// <summary>
    /// JasperFx/wolverine#4865. Project <typeparamref name="T"/> over the events <paramref name="query"/> selects
    /// by their tags -- a Dynamic Consistency Boundary model, or a view built the same way -- as
    /// <c>AggregateByTagsAsync&lt;T&gt;</c> does on Marten, Polecat and Fisher. Null when no event matches.
    /// </summary>
    public static Task<T?> AggregateByTagsAsync<T>(IEventStore store, EventTagQuery query, CancellationToken token = default)
        where T : class
        => AggregateByTagsAsync<T>(store, query, null, token);

    /// <summary>
    /// bobcat#536. <see cref="AggregateByTagsAsync{T}(IEventStore, EventTagQuery, CancellationToken)"/> over
    /// <paramref name="tenantId"/>'s events. A null tenant is the store's default session.
    /// </summary>
    public static async Task<T?> AggregateByTagsAsync<T>(IEventStore store, EventTagQuery query, string? tenantId,
        CancellationToken token = default)
        where T : class
    {
        var session = await EventStoreSessions.OpenAsync(store, tenantId, token).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            return await EventStoreSessions.EventStoreOperationsOf(session).AggregateByTagsAsync<T>(query, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <paramref name="query"/> as a specification reads it: <c>events tagged theScreeningId or CustomerId(CUST-104)</c>,
    /// with the event types a condition narrows to, <c>(ScreeningScheduled, SeatReserved)</c>.
    /// </summary>
    public static string Describe(EventTagQuery query)
    {
        var groups = query.Conditions
            .GroupBy(x => x.TagValue)
            .Select(group =>
            {
                var types = group.Where(x => x.EventType is not null).Select(x => x.EventType!.Name).Distinct().ToList();
                var tag = Bobcat.Engine.ScenarioValues.Format(group.Key);
                return types.Count == 0 ? tag : $"{tag} ({string.Join(", ", types)})";
            })
            .ToList();

        return groups.Count == 0 ? "no events" : $"events tagged {string.Join(" or ", groups)}";
    }

    public static Task<T?> AggregateStreamAsync<T>(IEventStore store, Guid streamId, CancellationToken token = default) where T : class
        => aggregateStreamAsync(store, events => events.AggregateStreamAsync<T>(streamId, token: token));

    /// <summary>Rebuild the aggregate of a string-keyed stream from its events.</summary>
    public static Task<T?> AggregateStreamAsync<T>(IEventStore store, string streamKey, CancellationToken token = default) where T : class
        => aggregateStreamAsync(store, events => events.AggregateStreamAsync<T>(streamKey, token: token));

    /// <summary>
    /// bobcat#536. Rebuild the aggregate of a <see cref="Guid"/>-identified stream from <paramref name="tenantId"/>'s
    /// events. A null tenant is <see cref="AggregateStreamAsync{T}(IEventStore, Guid, CancellationToken)"/>.
    /// </summary>
    public static Task<T?> AggregateStreamAsync<T>(IEventStore store, Guid streamId, string? tenantId,
        CancellationToken token = default) where T : class
        => tenantId is null
            ? AggregateStreamAsync<T>(store, streamId, token)
            : inTenantAsync(store, tenantId, events => events.AggregateStreamAsync<T>(streamId, token: token));

    /// <inheritdoc cref="AggregateStreamAsync{T}(IEventStore, Guid, string?, CancellationToken)"/>
    public static Task<T?> AggregateStreamAsync<T>(IEventStore store, string streamKey, string? tenantId,
        CancellationToken token = default) where T : class
        => tenantId is null
            ? AggregateStreamAsync<T>(store, streamKey, token)
            : inTenantAsync(store, tenantId, events => events.AggregateStreamAsync<T>(streamKey, token: token));

    /// <summary>
    /// The aggregate of a <see cref="Guid"/>-identified stream as the store serves it, through
    /// <c>FetchLatest&lt;T&gt;</c> (wolverine#4921): a projected snapshot where there is one, else built from
    /// the stream, whatever the projection lifecycle. Null when there is no such stream.
    /// </summary>
    public static Task<T?> FetchLatestAsync<T>(IEventStore store, Guid streamId, CancellationToken token = default) where T : class
        => fetchLatestAsync(store, null, events => events.FetchLatest<T>(streamId, token), token);

    /// <inheritdoc cref="FetchLatestAsync{T}(IEventStore, Guid, CancellationToken)"/>
    public static Task<T?> FetchLatestAsync<T>(IEventStore store, string streamKey, CancellationToken token = default) where T : class
        => fetchLatestAsync(store, null, events => events.FetchLatest<T>(streamKey, token), token);

    /// <summary>
    /// bobcat#536. <see cref="FetchLatestAsync{T}(IEventStore, Guid, CancellationToken)"/> as
    /// <paramref name="tenantId"/> sees it. A null tenant is the store's default session.
    /// </summary>
    public static Task<T?> FetchLatestAsync<T>(IEventStore store, Guid streamId, string? tenantId,
        CancellationToken token = default) where T : class
        => fetchLatestAsync(store, tenantId, events => events.FetchLatest<T>(streamId, token), token);

    /// <inheritdoc cref="FetchLatestAsync{T}(IEventStore, Guid, string?, CancellationToken)"/>
    public static Task<T?> FetchLatestAsync<T>(IEventStore store, string streamKey, string? tenantId,
        CancellationToken token = default) where T : class
        => fetchLatestAsync(store, tenantId, events => events.FetchLatest<T>(streamKey, token), token);

    /// <summary>
    /// bobcat#536. A session on <paramref name="store"/> scoped to <paramref name="tenantId"/>, opened on the database
    /// that tenant lives in: the store's own answer through <see cref="IEventStore.FindDatabaseForTenantAsync"/>, then
    /// Marten's <c>Storage.FindOrCreateDatabase(tenantId)</c>, then the store's only database. A store whose tenants
    /// are spread over several databases and that can name none of them for this tenant is refused, never guessed at.
    /// Dispose it; the object is the store's own session type (a Marten or Fisher session).
    /// </summary>
    public static Task<IAsyncDisposable> OpenTenantSessionAsync(IEventStore store, string tenantId, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        return EventStoreSessions.OpenAsync(store, tenantId, token);
    }

    private static async Task<T> inTenantAsync<T>(IEventStore store, string tenantId, Func<IQueryEventStore, Task<T>> read)
    {
        var session = await EventStoreSessions.OpenAsync(store, tenantId).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            return await read(EventStoreSessions.QueryEventStoreOf(session)).ConfigureAwait(false);
        }
    }

    private static async Task<T?> fetchLatestAsync<T>(IEventStore store, string? tenantId,
        Func<IEventStoreOperations, ValueTask<T?>> fetch, CancellationToken token)
        where T : class
    {
        var session = await EventStoreSessions.OpenAsync(store, tenantId, token).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            return await fetch(EventStoreSessions.EventStoreOperationsOf(session)).ConfigureAwait(false);
        }
    }

    private static Task<IReadOnlyList<IEvent>> fetchStreamAsync(
        IEventStore store,
        Func<IReadOnlyEventStore, Task<IReadOnlyList<IEvent>>> read)
        => readAsync(store, read);

    private static Task<T> readAsync<T>(IEventStore store, Func<IReadOnlyEventStore, Task<T>> read)
        => readAsync(store, null, read);

    private static async Task<T> readAsync<T>(IEventStore store, string? tenantId, Func<IReadOnlyEventStore, Task<T>> read)
    {
        var reader = tenantId is null ? store.OpenReadOnlyEventStore() : store.OpenReadOnlyEventStore(tenantId);
        try
        {
            return await read(reader).ConfigureAwait(false);
        }
        finally
        {
            await disposeIfOwned(reader).ConfigureAwait(false);
        }
    }

    private static async Task<T?> aggregateStreamAsync<T>(IEventStore store, Func<IQueryEventStore, Task<T?>> aggregate)
        where T : class
    {
        // Marten and Polecat: the read-only view IS the session's IQueryEventStore, so no reflection.
        var reader = store.OpenReadOnlyEventStore();
        try
        {
            if (reader is IQueryEventStore querying)
                return await aggregate(querying).ConfigureAwait(false);
        }
        finally
        {
            await disposeIfOwned(reader).ConfigureAwait(false);
        }

        // Fisher (and any store whose read-only view is a separate object): open a real session
        // through the generic closure and aggregate through its Events member.
        var session = await EventStoreSessions.OpenAsync(store).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            return await aggregate(EventStoreSessions.QueryEventStoreOf(session)).ConfigureAwait(false);
        }
    }

    // --- Projections ----------------------------------------------------------------------

    /// <summary>
    /// Block until every asynchronous projection on every database of the store has caught up with
    /// the store's high-water mark, or <paramref name="timeout"/> elapses. Returns at once when the
    /// store has no async projections. This is the same wait <c>ProjectionScenario</c> performs after
    /// each batch of appends, reached through <see cref="IEventDatabase.WaitForNonStaleProjectionDataAsync"/>
    /// so it does not matter who runs the daemon — Marten's coordinator, Fisher's hosted service, or
    /// Wolverine.
    /// </summary>
    /// <param name="coordinator">
    /// Optional fallback for a store that does not enumerate its databases through
    /// <see cref="IEventStore.AllDatabases"/>: the host's <see cref="IProjectionCoordinator"/>, whose
    /// main-database daemon is asked to <see cref="IProjectionDaemon.WaitForNonStaleData"/> instead.
    /// </param>
    /// <remarks>
    /// <b>A single-database store with no events at all returns at once (bobcat#497)</b>: nothing can be stale.
    /// Marten's own wait does not see it that way — a fresh <c>mt_events_sequence</c> reports a last value of 1
    /// with no events, and its <c>WaitForNonStaleProjectionDataAsync</c> answers that with an immediate
    /// <see cref="TimeoutException"/> ("No event activity was detected within the timeout span"). So the first
    /// read in a fresh schema threw while every later one passed, which made a document read depend on whether an
    /// earlier test had appended anything.
    /// </remarks>
    public static async Task WaitForNonStaleProjectionsAsync(
        IEventStore store,
        TimeSpan? timeout = null,
        IProjectionCoordinator? coordinator = null,
        CancellationToken token = default)
    {
        var wait = timeout ?? DefaultProjectionTimeout;
        var databases = await store.AllDatabases().ConfigureAwait(false);

        // The read-only view reads the one database, so its emptiness is that database's. Several databases
        // are each waited on, as before: the default view would speak for only one of them
        if (databases.Count == 1 && await holdsNoEventsAsync(store, token).ConfigureAwait(false)) return;

        if (databases.Count > 0)
        {
            foreach (var database in databases)
                await database.WaitForNonStaleProjectionDataAsync(wait).WaitAsync(token).ConfigureAwait(false);

            return;
        }

        if (coordinator != null)
        {
            await coordinator.DaemonForMainDatabase().WaitForNonStaleData(wait).WaitAsync(token).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException(
            $"Event store {Describe(store)} enumerates no databases through IEventStore.AllDatabases() and the host " +
            "registers no IProjectionCoordinator, so there is nothing to wait on. Marten, Polecat and Fisher all " +
            "enumerate their databases; if this is another store, register its IProjectionCoordinator.");
    }

    /// <summary>
    /// <see cref="WaitForNonStaleProjectionsAsync(IEventStore, TimeSpan?, IProjectionCoordinator?, CancellationToken)"/>
    /// for one tenant: the database that tenant's events live in. A store that can say which database that is waits
    /// on that one; any other waits on every database, which holds the tenant's too.
    /// </summary>
    /// <remarks>
    /// The store is asked first, store-agnostically, through <see cref="IEventStore.FindDatabaseForTenantAsync"/>
    /// (jasperfx#1005, wolverine#4962): its default answers a single-database store, and Marten, Polecat and Fisher
    /// are expected to override it for database-per-tenant. When it says null, Marten's
    /// <c>Storage.FindOrCreateDatabase(tenantId)</c> is still tried by reflection, so a Marten build that predates the
    /// override keeps waiting on one tenant's database; then every database.
    /// </remarks>
    public static async Task WaitForNonStaleProjectionsAsync(
        IEventStore store,
        string tenantId,
        TimeSpan? timeout = null,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        // #497's empty-store rule, for one tenant: Marten throws at once ("No event activity was detected") on a
        // database holding no events, so a tenant's first read in a fresh schema failed whenever no earlier test had
        // appended anything (GH-5082 found it once these tests ran in another order). A tenant holding no events has
        // no projected data to wait for. Asked of the TENANT's view: with conjoined tenancy the default view reads
        // the default tenant only, and would call a database empty that holds another tenant's events. Kept to a
        // single database, where that view is known to be the tenant's; database-per-tenant waits as before.
        var databases = await store.AllDatabases().ConfigureAwait(false);
        if (databases.Count == 1 && await holdsNoEventsAsync(store, tenantId, token).ConfigureAwait(false)) return;

        if (await TenantDatabaseAsync(store, tenantId, token).ConfigureAwait(false) is { } database)
        {
            await database.WaitForNonStaleProjectionDataAsync(timeout ?? DefaultProjectionTimeout).WaitAsync(token).ConfigureAwait(false);
            return;
        }

        await WaitForNonStaleProjectionsAsync(store, timeout, null, token).ConfigureAwait(false);
    }

    // True only when the store positively says it holds no events. Anything it cannot answer is "not known to be
    // empty", so the wait goes ahead as it always did
    private static Task<bool> holdsNoEventsAsync(IEventStore store, CancellationToken token)
        => holdsNoEventsAsync(store, null, token);

    private static async Task<bool> holdsNoEventsAsync(IEventStore store, string? tenantId, CancellationToken token)
    {
        try
        {
            return await readAsync(store, tenantId, async reader =>
            {
                var page = await reader.QueryEventsAsync(new EventQuery { PageSize = 1 }, token).ConfigureAwait(false);
                return page is { TotalCount: 0 };
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// bobcat#497. The async projection shards that had not reached the store's newest event, best effort: each as
    /// <c>'shard' at N of M</c>, a configured shard with no progress yet at 0. Empty when the store cannot say —
    /// it is a diagnosis for a timeout's message, never a decision. <see cref="DescribeProjectionsBehindAsync" />
    /// also says why the store could not say.
    /// </summary>
    /// <remarks>
    /// N is the shard's progress and M the database's highest event <b>sequence</b> number, not a count of events:
    /// the sequence is never reset by deleting event data (Marten's <c>DeleteAllEventDataAsync</c>, say), so 67
    /// events published after a wipe can read as <c>at 0 of 461</c>.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> ProjectionsBehindAsync(IEventStore store, CancellationToken token = default)
        => (await DescribeProjectionsBehindAsync(store, token).ConfigureAwait(false)).Shards;

    /// <summary>
    /// bobcat#686. <see cref="ProjectionsBehindAsync" />, plus the exception that stopped the store saying — a
    /// schema dropped by another process, a connection refused — instead of discarding it. Never throws.
    /// </summary>
    public static async Task<ProjectionsBehind> DescribeProjectionsBehindAsync(IEventStore store, CancellationToken token = default)
    {
        var behind = new List<string>();
        try
        {
            var configured = EventStoreShards.TryNames(store);
            foreach (var database in await store.AllDatabases().ConfigureAwait(false))
            {
                // The highest event sequence number, not an event count (see ProjectionsBehindAsync)
                var target = await database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);
                var progress = (await database.AllProjectionProgress(token).ConfigureAwait(false))
                    .Where(x => !isHighWaterShard(x))
                    .ToList();

                var shards = configured ?? progress.Select(x => x.ShardName).ToList();
                foreach (var shard in shards)
                {
                    var sequence = progress.FirstOrDefault(x => x.ShardName.Equals(shard, StringComparison.OrdinalIgnoreCase))?.Sequence ?? 0L;
                    if (sequence < target) behind.Add($"'{shard}' at {sequence} of {target}");
                }
            }
        }
        catch (Exception e)
        {
            // Not knowable from here on; the caller says so, and why
            return new ProjectionsBehind(behind, e);
        }

        return new ProjectionsBehind(behind, null);
    }

    /// <summary>
    /// bobcat#685. The diagnosis <see cref="CritterStackScenario" /> gives its own catch-up timeouts, for any wait: a
    /// suite's own <c>WaitForNonStaleProjectionDataAsync</c>, a daemon's <c>WaitForNonStaleData</c>, a shard-state
    /// wait, or <see cref="WaitForNonStaleProjectionsAsync(IEventStore, TimeSpan?, IProjectionCoordinator?, CancellationToken)"/>.
    /// Returns a <see cref="ProjectionCatchUpTimeoutException" /> naming the wait, the shards that were behind
    /// (<see cref="DescribeProjectionsBehindAsync" />, with why the store could not say when it could not), the timeout
    /// and, when given, what was about to be read, with
    /// <paramref name="inner" /> as its <see cref="Exception.InnerException" />. An <paramref name="inner" /> that is
    /// already one is returned as it is.
    /// </summary>
    /// <example>
    /// <code>
    /// catch (TimeoutException e)
    /// {
    ///     throw await EventStores.DiagnoseCatchUpTimeoutAsync(store, "the daemon has caught up", timeout, e);
    /// }
    /// </code>
    /// </example>
    public static async Task<ProjectionCatchUpTimeoutException> DiagnoseCatchUpTimeoutAsync(
        IEventStore store,
        string wait,
        TimeSpan timeout,
        TimeoutException inner,
        Type? readType = null,
        object? identity = null,
        CancellationToken token = default)
    {
        if (inner is ProjectionCatchUpTimeoutException diagnosed) return diagnosed;

        var behind = await DescribeProjectionsBehindAsync(store, token).ConfigureAwait(false);
        return new ProjectionCatchUpTimeoutException(wait, behind, timeout, readType, identity, inner);
    }

    // The database a tenant's events live in: the store's own answer through FindDatabaseForTenantAsync first
    // (jasperfx#1005), then Storage.FindOrCreateDatabase(string) where the store has one; null when neither can say
    internal static async Task<IEventDatabase?> TenantDatabaseAsync(IEventStore store, string tenantId, CancellationToken token)
    {
        if (await store.FindDatabaseForTenantAsync(tenantId, token).ConfigureAwait(false) is { } found) return found;

        var storage = store.GetType().GetProperty("Storage", BindingFlags.Public | BindingFlags.Instance)?.GetValue(store);
        var find = storage?.GetType().GetInterfaces().Prepend(storage.GetType())
            .Select(x => x.GetMethod("FindOrCreateDatabase", [typeof(string)]))
            .FirstOrDefault(x => x is not null);
        if (storage is null || find is null) return null;

        // ValueTask<TDatabase>, awaited through AsTask() since TDatabase is the store's own type
        var pending = find.Invoke(storage, [tenantId])!;
        var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, [])!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")!.GetValue(task) as IEventDatabase;
    }

    /// <summary>
    /// What kind of projection the store registers for <paramref name="projected"/> — a single-stream or a
    /// multi-stream projection, say — or null when none builds that type. Read off the store's own description
    /// of its subscriptions (<see cref="IEventStore.TryCreateUsage"/>).
    /// </summary>
    public static async Task<SubscriptionType?> ProjectionKindAsync(IEventStore store, Type projected, CancellationToken token = default)
    {
        var usage = await store.TryCreateUsage(token).ConfigureAwait(false);
        var subscription = usage?.Subscriptions.FirstOrDefault(x =>
            x.AggregateType is { } aggregate && aggregate.FullName == projected.FullName);
        return subscription?.SubscriptionType;
    }

    /// <summary>
    /// Wait until the async projection shards whose name contains <paramref name="projectionName"/>
    /// (default: <c>typeof(T).Name</c>, which matches both a projection class and the view type a
    /// <c>Snapshot&lt;T&gt;</c> registers) have reached the store's current highest event sequence.
    /// Returns at once when the store has no async shards at all — an inline projection has nothing
    /// to wait for — but a name that matches none of the shards that do exist throws rather than
    /// passing vacuously.
    /// </summary>
    public static async Task WaitForProjectionAsync<T>(
        IEventStore store,
        TimeSpan? timeout = null,
        string? projectionName = null,
        CancellationToken token = default)
    {
        var wait = timeout ?? DefaultProjectionTimeout;
        var name = projectionName ?? typeof(T).Name;

        var shards = EventStoreShards.TryNames(store);
        foreach (var database in await store.AllDatabases().ConfigureAwait(false))
        {
            var target = await database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);
            await waitForShards(database, shards, name, target, wait, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wait until the async projection shards whose name contains <paramref name="projectionName"/>
    /// (default: <c>typeof(T).Name</c>) have processed at least event sequence
    /// <paramref name="minSequence"/>. Same matching and vacuity rules as
    /// <see cref="WaitForProjectionAsync{T}(IEventStore, TimeSpan?, string?, CancellationToken)"/>.
    /// </summary>
    public static async Task WaitForProjectionAsync<T>(
        IEventStore store,
        long minSequence,
        TimeSpan? timeout = null,
        string? projectionName = null,
        CancellationToken token = default)
    {
        var wait = timeout ?? DefaultProjectionTimeout;
        var name = projectionName ?? typeof(T).Name;

        var shards = EventStoreShards.TryNames(store);
        foreach (var database in await store.AllDatabases().ConfigureAwait(false))
            await waitForShards(database, shards, name, minSequence, wait, token).ConfigureAwait(false);
    }

    /// <summary>The current progress of every async projection shard, per database.</summary>
    public static async Task<IReadOnlyList<ShardState>> ProjectionProgressAsync(IEventStore store, CancellationToken token = default)
    {
        var all = new List<ShardState>();
        foreach (var database in await store.AllDatabases().ConfigureAwait(false))
            all.AddRange(await database.AllProjectionProgress(token).ConfigureAwait(false));

        return all;
    }

    /// <param name="configured">
    /// The store's configured async shard identities, when the store's generic closure could be read;
    /// null means "unknown", in which case the progress rows are the only evidence there is.
    /// </param>
    private static async Task waitForShards(
        IEventDatabase database,
        IReadOnlyList<string>? configured,
        string name,
        long target,
        TimeSpan timeout,
        CancellationToken token)
    {
        // Decide what to wait on from the store's CONFIGURATION where we can, not from the progress
        // table: a daemon writes a shard's progress row only after its first batch, so right after
        // the first append an empty table is indistinguishable from "no async projections" and a wait
        // keyed on it would pass vacuously.
        IReadOnlyList<string>? expected = null;
        if (configured != null)
        {
            if (configured.Count == 0)
                return; // no async shards at all — nothing can be stale

            expected = configured.Where(n => n.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (expected.Count == 0)
                throw noShardMatches(database, name, configured);
        }

        var interval = TimeSpan.FromMilliseconds(50);
        var maxInterval = TimeSpan.FromSeconds(1);
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            var progress = await database.AllProjectionProgress(token).ConfigureAwait(false);
            var reported = progress.Where(p => !isHighWaterShard(p)).ToList();

            IReadOnlyList<(string Shard, long Sequence)> matching;
            if (expected != null)
            {
                // A shard with no row yet has processed nothing.
                matching = expected
                    .Select(shard => (shard, reported.FirstOrDefault(p => p.ShardName.Equals(shard, StringComparison.OrdinalIgnoreCase))?.Sequence ?? 0L))
                    .ToList();
            }
            else
            {
                if (reported.Count == 0)
                    return; // unknown configuration and nothing reported — the most we can say

                matching = reported
                    .Where(p => p.ShardName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (p.ShardName, p.Sequence))
                    .ToList();

                if (matching.Count == 0)
                    throw noShardMatches(database, name, reported.Select(p => p.ShardName).ToList());
            }

            if (matching.All(p => p.Sequence >= target))
                return;

            if (stopwatch.Elapsed >= timeout)
            {
                var state = string.Join(", ", matching.Select(p => $"'{p.Shard}' at {p.Sequence}"));
                throw new TimeoutException(
                    $"Projection '{name}' did not reach sequence {target} within {timeout.TotalMilliseconds}ms ({state}).");
            }

            await Task.Delay(interval, token).ConfigureAwait(false);
            interval = TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds * 2, maxInterval.TotalMilliseconds));
        }
    }

    private static InvalidOperationException noShardMatches(IEventDatabase database, string name, IReadOnlyList<string> known)
        => new(
            $"No async projection shard on database '{database.Identifier}' matches '{name}'. Known shards: " +
            string.Join(", ", known.Select(s => $"'{s}'")) +
            ". Pass projectionName explicitly if the projection is not named after its type.");

    // The high-water mark is itself reported as a shard by Marten; it is the thing being waited FOR,
    // never a thing to wait ON.
    private static bool isHighWaterShard(ShardState state)
        => string.Equals(state.ShardName, ShardState.HighWaterMark, StringComparison.OrdinalIgnoreCase);

    // --- Resetting ------------------------------------------------------------------------

    /// <summary>
    /// Delete every event and every document in the store, keeping its schema — the between-scenario
    /// reset. Follows the convention the Critter Stack stores share (<c>Advanced.ResetAllData</c>,
    /// or <c>Advanced.Clean.DeleteAllEventDataAsync</c> + <c>DeleteAllDocumentsAsync</c>); see the
    /// class remarks for why this is a convention rather than an interface today.
    /// </summary>
    public static Task ResetAllDataAsync(IEventStore store, CancellationToken token = default)
        => StoreResetConvention.ResetAsync(store, token);

    // --- Plumbing ---------------------------------------------------------------------------

    internal static string Describe(IEventStore store)
    {
        try
        {
            return $"'{store.Identity.Name}' ({store.Identity.Type}, {store.GetType().Name})";
        }
        catch
        {
            return $"of type {store.GetType().FullName}";
        }
    }

    private static ValueTask disposeIfOwned(object? reader) => reader switch
    {
        IAsyncDisposable asyncDisposable => asyncDisposable.DisposeAsync(),
        IDisposable disposable => dispose(disposable),
        _ => ValueTask.CompletedTask
    };

    private static ValueTask dispose(IDisposable disposable)
    {
        disposable.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Opens a store session through the <c>IEventStore&lt;TOperations, TQuerySession&gt;</c> closure
/// without knowing the closure at compile time, and finds the session's
/// <see cref="IQueryEventStore"/>. The one reflective seam in the package; see
/// <see cref="EventStores"/> remarks.
/// </summary>
internal static class EventStoreSessions
{
    private static readonly Dictionary<Type, MethodInfo> openSessionByStoreType = new();
    private static readonly Dictionary<Type, MethodInfo> openTenantSessionByStoreType = new();
    private static readonly Dictionary<Type, PropertyInfo?> eventsBySessionType = new();
    private static readonly Lock gate = new();

    public static Task<IAsyncDisposable> OpenAsync(IEventStore store) => OpenAsync(store, null);

    // bobcat#536: a tenant's session is opened on the database that tenant lives in, through the closure's
    // OpenSession(IEventDatabase, string tenantId); no tenant is the first database's default session, as before
    public static async Task<IAsyncDisposable> OpenAsync(IEventStore store, string? tenantId, CancellationToken token = default)
    {
        var open = openSessionMethod(store.GetType(), tenantId is not null);

        var databases = await store.AllDatabases().ConfigureAwait(false);
        if (databases.Count == 0)
        {
            throw new InvalidOperationException(
                $"Event store {EventStores.Describe(store)} enumerates no databases through IEventStore.AllDatabases(), " +
                "so no session can be opened against it to aggregate a stream.");
        }

        var session = tenantId is null
            ? open.Invoke(store, [databases[0]])
            : open.Invoke(store, [await tenantDatabaseAsync(store, tenantId, databases, token).ConfigureAwait(false), tenantId]);
        if (session is null) throw new InvalidOperationException($"OpenSession on {store.GetType().Name} returned null.");

        return session as IAsyncDisposable
               ?? throw new InvalidOperationException(
                   $"The session type {session.GetType().Name} opened by {store.GetType().Name} is not IAsyncDisposable, " +
                   "which every IStorageOperations is expected to be.");
    }

    public static IEventStoreOperations EventStoreOperationsOf(object session)
    {
        if (session is IEventStoreOperations direct) return direct;

        if (eventsProperty(session.GetType())?.GetValue(session) is IEventStoreOperations events) return events;

        throw new InvalidOperationException(
            $"Cannot fetch the latest aggregate through session type {session.GetType().FullName}: its 'Events' member is not a " +
            "JasperFx.Events.IEventStoreOperations. Marten, Polecat and Fisher sessions all expose one.");
    }

    public static IQueryEventStore QueryEventStoreOf(object session)
    {
        if (session is IQueryEventStore direct)
            return direct;

        var property = eventsProperty(session.GetType());
        var value = property?.GetValue(session);
        if (value is IQueryEventStore events)
            return events;

        throw new InvalidOperationException(
            $"Cannot aggregate a stream through session type {session.GetType().FullName}: it is not a " +
            "JasperFx.Events.IQueryEventStore and has no public 'Events' member that is one. Marten, Polecat and Fisher " +
            "sessions all expose one; another store needs IEventStore.OpenReadOnlyEventStore() to return an IQueryEventStore.");
    }

    // The tenant's database, or the store's only one. Several databases and no answer is refused: the first one
    // would be a guess, and a wrong guess reads or writes another tenant's database without a word
    private static async Task<IEventDatabase> tenantDatabaseAsync(IEventStore store, string tenantId,
        IReadOnlyList<IEventDatabase> databases, CancellationToken token)
    {
        if (await EventStores.TenantDatabaseAsync(store, tenantId, token).ConfigureAwait(false) is { } found) return found;
        if (databases.Count == 1) return databases[0];

        throw new InvalidOperationException(
            $"Event store {EventStores.Describe(store)} has {databases.Count} databases and cannot say which one holds tenant " +
            $"'{tenantId}': IEventStore.FindDatabaseForTenantAsync returned null and the store has no " +
            "Storage.FindOrCreateDatabase(string). Is the tenant id right, and is the tenant registered with the store?");
    }

    private static MethodInfo openSessionMethod(Type storeType, bool forTenant)
    {
        lock (gate)
        {
            var cache = forTenant ? openTenantSessionByStoreType : openSessionByStoreType;
            if (cache.TryGetValue(storeType, out var cached))
                return cached;

            var closure = storeType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventStore<,>))
                ?? throw new InvalidOperationException(
                    $"Event store type {storeType.FullName} does not implement IEventStore<TOperations, TQuerySession>, " +
                    "so Bobcat cannot open a session on it to aggregate a stream.");

            Type[] parameters = forTenant ? [typeof(IEventDatabase), typeof(string)] : [typeof(IEventDatabase)];
            var method = closure.GetMethods()
                .First(m => m.Name == nameof(IEventStore<IStorageOperations, object>.OpenSession)
                            && m.GetParameters().Select(x => x.ParameterType).SequenceEqual(parameters));

            cache[storeType] = method;
            return method;
        }
    }

    private static PropertyInfo? eventsProperty(Type sessionType)
    {
        lock (gate)
        {
            if (eventsBySessionType.TryGetValue(sessionType, out var cached))
                return cached;

            // The session's own public surface first, then every interface it implements — Fisher's
            // IQueryEventStore-typed Events is an explicit interface implementation.
            var candidates = new[] { sessionType }.Concat(sessionType.GetInterfaces());
            var property = candidates
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .FirstOrDefault(p => p.Name == "Events" && typeof(IQueryEventStore).IsAssignableFrom(p.PropertyType))
                ?? candidates
                    .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    .FirstOrDefault(p => p.Name == "Events" && p.GetIndexParameters().Length == 0);

            eventsBySessionType[sessionType] = property;
            return property;
        }
    }
}

/// <summary>
/// Reads the store's configured async shard identities through the
/// <c>IEventStore&lt;TOperations, TQuerySession&gt;.AllShards()</c> closure, so a projection wait can
/// tell "no async projections" from "the daemon has not written a progress row yet". Same bounded
/// reflection as <see cref="EventStoreSessions"/>.
/// </summary>
internal static class EventStoreShards
{
    private static readonly Dictionary<Type, MethodInfo?> allShardsByStoreType = new();
    private static readonly Lock gate = new();

    /// <summary>Shard identities, or null when the store does not expose the generic closure.</summary>
    public static IReadOnlyList<string>? TryNames(IEventStore store)
    {
        var allShards = allShardsMethod(store.GetType());
        if (allShards == null)
            return null;

        if (allShards.Invoke(store, []) is not System.Collections.IEnumerable shards)
            return null;

        var names = new List<string>();
        foreach (var shard in shards)
        {
            if (shard == null) continue;
            var name = shard.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(shard);
            if (name is ShardName shardName)
                names.Add(shardName.Identity);
        }

        return names;
    }

    private static MethodInfo? allShardsMethod(Type storeType)
    {
        lock (gate)
        {
            if (allShardsByStoreType.TryGetValue(storeType, out var cached))
                return cached;

            var closure = storeType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventStore<,>));

            var method = closure?.GetMethods()
                .FirstOrDefault(m => m.Name == nameof(IEventStore<IStorageOperations, object>.AllShards) && m.GetParameters().Length == 0);

            allShardsByStoreType[storeType] = method;
            return method;
        }
    }
}

/// <summary>
/// The reset convention shared by Marten, Polecat and Fisher, reached without a reference to any
/// of them. Internal so the lookup is unit-testable against a plain fake; see
/// <see cref="EventStores.ResetAllDataAsync"/>.
/// </summary>
internal static class StoreResetConvention
{
    private static readonly string[] resetMethodNames = ["ResetAllData", "ResetAllDataAsync"];

    public static async Task ResetAsync(object store, CancellationToken token)
    {
        var advanced = store.GetType().GetProperty("Advanced", BindingFlags.Public | BindingFlags.Instance)?.GetValue(store);
        if (advanced != null)
        {
            var reset = resetMethodNames
                .Select(name => advanced.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, [typeof(CancellationToken)]))
                .FirstOrDefault(m => m != null && typeof(Task).IsAssignableFrom(m.ReturnType));

            if (reset != null)
            {
                await ((Task)reset.Invoke(advanced, [token])!).ConfigureAwait(false);
                return;
            }

            var cleaner = advanced.GetType().GetProperty("Clean", BindingFlags.Public | BindingFlags.Instance)?.GetValue(advanced);
            if (cleaner != null && tryFind(cleaner, "DeleteAllEventDataAsync", out var events) && tryFind(cleaner, "DeleteAllDocumentsAsync", out var documents))
            {
                await events(token).ConfigureAwait(false);
                await documents(token).ConfigureAwait(false);
                return;
            }
        }

        throw new InvalidOperationException(
            $"Bobcat does not know how to reset event store type {store.GetType().FullName}. It looked for " +
            "Advanced.ResetAllData(CancellationToken) / Advanced.ResetAllDataAsync(CancellationToken), then for " +
            "Advanced.Clean.DeleteAllEventDataAsync() and DeleteAllDocumentsAsync() — the convention Marten, Polecat " +
            "and Fisher share. Reset this store in the host resource's reset hook instead.");
    }

    private static bool tryFind(object target, string name, out Func<CancellationToken, Task> call)
    {
        var withToken = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, [typeof(CancellationToken)]);
        if (withToken != null && typeof(Task).IsAssignableFrom(withToken.ReturnType))
        {
            call = token => (Task)withToken.Invoke(target, [token])!;
            return true;
        }

        var bare = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (bare != null && typeof(Task).IsAssignableFrom(bare.ReturnType))
        {
            call = _ => (Task)bare.Invoke(target, [])!;
            return true;
        }

        call = null!;
        return false;
    }
}
