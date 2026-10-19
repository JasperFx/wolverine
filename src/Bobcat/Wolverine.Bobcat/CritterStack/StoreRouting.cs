using JasperFx.Events;
using JasperFx.Events.Descriptors;

namespace Bobcat.CritterStack;

/// <summary>
/// bobcat#504. One store's high-water mark, taken before an act: the highest sequence it had issued, which
/// the act's read-back counts from. See <c>CritterStackScenario.highWaterMarksAsync</c>.
/// </summary>
/// <param name="Store">The event store the mark was taken on.</param>
/// <param name="Sequence">The highest sequence it had issued before the act, 0 for an empty store.</param>
public record StoreHighWaterMark(IEventStore Store, long Sequence);

/// <summary>
/// bobcat#504. What one event store says it owns, read off its own registrations through the JasperFx
/// abstractions every Critter Stack store implements: <see cref="IEventStore.TryCreateUsage"/> (its
/// projections' aggregate and document types and applied events, its registered event types, its DCB tag
/// types and their aggregates) and <see cref="IDocumentStoreUsageSource.TryCreateUsage"/> (its document
/// mappings). Type identity is the full name, as the descriptors carry it.
/// </summary>
/// <remarks>
/// A store that cannot describe itself (it returns null or throws) owns nothing, so routing falls back to the
/// default store rather than failing on the description. Marten, Polecat and Fisher register some types
/// lazily — an event type the first time it is appended, a document type the first time it is stored or
/// loaded — so what a store owns can grow while the process runs; the scenario caches what it read.
/// </remarks>
internal sealed class StoreCatalog
{
    public static readonly StoreCatalog Empty = new();

    /// <summary>Aggregate, projected and document types: what a model-typed step is routed by.</summary>
    public HashSet<string> Models { get; } = new(StringComparer.Ordinal);

    /// <summary>Event types the store registers, or one of its projections applies.</summary>
    public HashSet<string> Events { get; } = new(StringComparer.Ordinal);

    /// <summary>DCB tag types the store registers.</summary>
    public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);

    public static async Task<StoreCatalog> ReadAsync(IEventStore store, CancellationToken token = default)
    {
        var catalog = new StoreCatalog();

        try
        {
            if (await store.TryCreateUsage(token).ConfigureAwait(false) is { } usage) catalog.add(usage);
        }
        catch (Exception)
        {
            // Nothing the store can say about its events; it owns none of them
        }

        try
        {
            if (store is IDocumentStoreUsageSource documents
                && await documents.TryCreateUsage(token).ConfigureAwait(false) is { } documentUsage)
            {
                foreach (var mapping in documentUsage.Documents)
                {
                    if (mapping.DocumentType?.FullName is { } name) catalog.Models.Add(name);
                }
            }
        }
        catch (Exception)
        {
            // Likewise for its documents
        }

        return catalog;
    }

    private void add(EventStoreUsage usage)
    {
        foreach (var subscription in usage.Subscriptions)
        {
            if (subscription.AggregateType?.FullName is { } aggregate) Models.Add(aggregate);
            foreach (var applied in subscription.AppliedEvents) Events.Add(applied.FullName);
            foreach (var e in subscription.Events) addEvent(e.Type?.FullName);
        }

        foreach (var e in usage.Events) addEvent(e.Type?.FullName);
        foreach (var e in usage.RegisteredEventTypes) addEvent(e.EventType?.FullName);

        foreach (var tag in usage.TagTypes)
        {
            if (tag.TagType is { Length: > 0 } tagType) Tags.Add(tagType);
            if (tag.AggregateType is { Length: > 0 } aggregate) Models.Add(aggregate);
        }
    }

    private void addEvent(string? name)
    {
        if (name is { Length: > 0 }) Events.Add(name);
    }
}

/// <summary>
/// bobcat#504. Picks the event store a step reads or writes when the host registers several: the one store
/// whose registrations claim the step's types, else the default store (the scenario's <c>storeName</c>, else the
/// host's one main store), else a
/// <see cref="Engine.SpecCriticalException"/> that lists the candidates and says to address one with
/// <c>In&lt;TStore&gt;()</c> or <c>In(name)</c>. Never used for a host with a single store.
/// </summary>
internal sealed class StoreRouter
{
    private readonly IReadOnlyList<IEventStore> _stores;
    private readonly IEventStore? _default;
    private readonly Dictionary<IEventStore, Task<StoreCatalog>> _catalogs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IEventStore> _decisions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public StoreRouter(IReadOnlyList<IEventStore> stores, IEventStore? defaultStore)
    {
        _stores = stores;
        _default = defaultStore;
    }

    public IReadOnlyList<IEventStore> Stores => _stores;

    /// <summary>
    /// The store for a step about <paramref name="model"/> (an aggregate, projected or document type, when the
    /// step has one), whose events are <paramref name="events"/> and whose tags are <paramref name="tags"/>.
    /// The model decides when exactly one store claims it; the events and tags decide when none does, or
    /// narrow several down to one.
    /// </summary>
    public async Task<IEventStore> RouteAsync(string step, Type? model, IReadOnlyCollection<Type> events,
        IReadOnlyCollection<Type> tags)
    {
        var key = $"{model?.FullName}|{string.Join(",", events.Select(x => x.FullName).Distinct().Order())}|"
                  + string.Join(",", tags.Select(x => x.FullName).Distinct().Order());
        lock (_gate)
        {
            if (_decisions.TryGetValue(key, out var known)) return known;
        }

        var catalogs = new List<(IEventStore Store, StoreCatalog Catalog)>();
        foreach (var store in _stores) catalogs.Add((store, await catalogOf(store).ConfigureAwait(false)));

        var decided = decide(step, model, events, tags, catalogs);
        lock (_gate)
        {
            _decisions[key] = decided;
        }

        return decided;
    }

    private IEventStore decide(string step, Type? model, IReadOnlyCollection<Type> events, IReadOnlyCollection<Type> tags,
        IReadOnlyList<(IEventStore Store, StoreCatalog Catalog)> catalogs)
    {
        var byModel = model is null
            ? []
            : catalogs.Where(x => x.Catalog.Models.Contains(model.FullName!)).Select(x => x.Store).ToList();
        if (byModel.Count == 1) return byModel[0];

        // The events and tags: every store that claims any of them
        var bySignature = catalogs
            .Where(x => events.Any(e => x.Catalog.Events.Contains(e.FullName!)) || tags.Any(t => x.Catalog.Tags.Contains(t.FullName!)))
            .Select(x => x.Store)
            .ToList();

        // Several stores claim the model: the events may still say which one
        var candidates = byModel.Count > 1
            ? bySignature.Count == 0 ? byModel : byModel.Where(bySignature.Contains).ToList()
            : bySignature;
        if (candidates.Count == 1) return candidates[0];
        if (byModel.Count > 1 && candidates.Count == 0) candidates = byModel;

        // Nothing claims it, or several stores do: the default store, when it can take it
        if (_default is not null && (candidates.Count == 0 || candidates.Contains(_default))) return _default;

        throw new Engine.SpecCriticalException(ambiguity(step, model, events, candidates));
    }

    private string ambiguity(string step, Type? model, IReadOnlyCollection<Type> events, IReadOnlyList<IEventStore> candidates)
    {
        var subject = model is not null
            ? model.Name
            : events.Count > 0 ? string.Join(", ", events.Select(x => x.Name).Distinct()) : "this step";

        var claim = candidates.Count == 0
            ? $"no event store registers {subject}"
            : $"{candidates.Count} event stores register {subject} ({string.Join(", ", candidates.Select(EventStores.Describe))})";

        return $"'{step}': the host registers {_stores.Count} event stores and {claim}, so Bobcat cannot tell which "
               + $"one it means{(_default is null ? " and the scenario names no default store (pass storeName, or register exactly one main store)" : "")}. Address the store explicitly: "
               + "scenario.In<IYourStore>() for a store registered under a marker interface (AddMartenStore<T>, "
               + "AddPolecatStore<T>, AddFisherStore<T>), or scenario.In(\"name\") by its IEventStore.Identity name. "
               + $"Stores: {string.Join(", ", _stores.Select(EventStores.Describe))}.";
    }

    private Task<StoreCatalog> catalogOf(IEventStore store)
    {
        lock (_gate)
        {
            if (!_catalogs.TryGetValue(store, out var catalog))
            {
                catalog = StoreCatalog.ReadAsync(store);
                _catalogs[store] = catalog;
            }

            return catalog;
        }
    }
}
