using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5415. Which event-sourced saga types an event store keeps, and how. Each store integration (Marten, Polecat,
/// Fisher) calls this while its store is configured, to register an Inline snapshot for every
/// <see cref="EventSourcedSaga" /> type that belongs to it, refuse an Async one, and record which lifecycle each saga
/// ended up with for the saga explorer.
/// </summary>
internal sealed class EventSourcedSagaRegistration
{
    private readonly ConcurrentDictionary<Type, ProjectionLifecycle> _lifecycles = new();
    private readonly object _gate = new();
    private Type[]? _sagaTypes;

    /// <summary>
    /// Every event-sourced saga type Wolverine's handler discovery finds. Read while a store is configured, which can
    /// happen before the handler graph is compiled, so it runs the same discovery the handler graph will
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The same conventional handler scan HandlerGraph runs; an AOT app pre-generates its handlers.")]
    public IReadOnlyList<Type> SagaTypes(WolverineOptions options)
    {
        if (_sagaTypes != null) return _sagaTypes;

        lock (_gate)
        {
            _sagaTypes ??= options.HandlerGraph.Discovery.FindCalls(options)
                .Select(x => x.Item1)
                .Where(x => x.CanBeCastTo<EventSourcedSaga>() && x.IsConcrete())
                .Distinct()
                .ToArray();

            return _sagaTypes;
        }
    }

    /// <summary>
    /// The event-sourced saga types kept by the store marked with <paramref name="storeType" />, or by the main store
    /// when it is null: a saga type marked with an ancillary store attribute (<c>[MartenStore]</c>,
    /// <c>[PolecatStore]</c>, <c>[FisherStore]</c>, <c>[Storage]</c>) belongs to that store, any other to the main one
    /// </summary>
    public IReadOnlyList<Type> SagaTypesFor(WolverineOptions options, Type? storeType)
        => SagaTypes(options).Where(x => StoreTypeOf(x) == storeType).ToArray();

    public static Type? StoreTypeOf(Type sagaType)
        => sagaType.GetCustomAttributes(true).OfType<IAncillaryStoreAttribute>().FirstOrDefault()?.StoreType;

    /// <summary>
    /// Record how <paramref name="sagaType" /> is kept: the Inline snapshot Wolverine registered, or the
    /// application's own registration, which wins. An Async registration is refused
    /// </summary>
    public void Record(Type sagaType, ProjectionLifecycle lifecycle)
    {
        if (lifecycle == ProjectionLifecycle.Async)
        {
            throw new InvalidSagaException(
                $"{sagaType.FullNameInCode()} is an event-sourced saga, so it cannot be kept by an Async projection: its handlers would read a snapshot behind its own stream. Leave it to Wolverine's Inline snapshot, or register it Live.");
        }

        _lifecycles[sagaType] = lifecycle;
    }

    /// <summary>
    /// Refuse a saga identity its event store cannot key a stream by: an int or long id (anything but a Guid or a string,
    /// or a strong-typed id wrapping one), or a key that does not match <paramref name="streamIdentity" />, when known
    /// </summary>
    public static void AssertIdentity(Type sagaType, StreamIdentity? streamIdentity, string? storeName)
    {
        var idType = EventSourcedSagaPolicy.IdTypeOf(sagaType);
        var keyType = EventSourcedSagaPolicy.KeyTypeOf(idType);

        if (keyType != typeof(Guid) && keyType != typeof(string))
        {
            throw new InvalidSagaException(
                $"{sagaType.FullNameInCode()} is an event-sourced saga keyed by {idType.NameInCode()}. Event-sourced sagas need a Guid or string id, or a strong-typed id wrapping one.");
        }

        if (streamIdentity is not { } identity) return;

        var expected = keyType == typeof(Guid) ? StreamIdentity.AsGuid : StreamIdentity.AsString;
        if (identity != expected)
        {
            throw new InvalidSagaException(
                $"{sagaType.FullNameInCode()} is an event-sourced saga keyed by {idType.NameInCode()}, which does not match the event store's stream identity ({identity}). " +
                $"Key it by {(identity == StreamIdentity.AsGuid ? "Guid" : "string")}, or use a {storeName ?? "store"} store with StreamIdentity.{expected}.");
        }
    }

    /// <summary>How the saga is kept, if a store registered it</summary>
    public ProjectionLifecycle? LifecycleOf(Type sagaType)
        => _lifecycles.TryGetValue(sagaType, out var lifecycle) ? lifecycle : null;

    /// <summary>Wolverine's own event types, with the aliases every store registers them under</summary>
    public static IReadOnlyList<(Type EventType, string Alias)> FrameworkEvents => EventSourcedSagaEventNames.All;

    /// <summary>The <see cref="Archived" /> event Wolverine appends when it archives a completed saga's stream</summary>
    public static Archived ArchivedForRetention() => new("saga completed");
}
