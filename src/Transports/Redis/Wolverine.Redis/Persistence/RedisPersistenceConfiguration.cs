using JasperFx.Core.Reflection;

namespace Wolverine.Redis;

/// <summary>
/// What Wolverine should do at startup when the Redis server is configured in a way that would silently
/// destroy the data it is being asked to keep — an <c>allkeys-*</c> eviction policy, or no persistence
/// at all. See <see cref="RedisPersistenceConfiguration.DurabilityCheck" />.
/// </summary>
public enum RedisDurabilityCheck
{
    /// <summary>
    /// Log a warning naming the offending setting and start anyway. The default.
    /// </summary>
    Warn,

    /// <summary>
    /// Refuse to start. Use this where losing a saga is worse than failing to deploy.
    /// </summary>
    Throw,

    /// <summary>
    /// Do not probe the server at all.
    /// </summary>
    Disabled
}

/// <summary>
/// Declares which types Wolverine may read and write in Redis, and how each is addressed.
/// </summary>
/// <remarks>
/// Registration is explicit, type by type, which is also what keeps the frame provider selective:
/// Wolverine takes the first provider whose <c>CanPersist</c> claims a type, so a provider claiming
/// everything Redis could theoretically hold would compete with Marten and EF Core for their own
/// documents depending on registration order.
/// </remarks>
public class RedisPersistenceConfiguration
{
    private readonly Dictionary<Type, RedisDocumentMapping> _mappings = new();

    /// <summary>
    /// What to do at startup about a Redis server configured as a cache. See
    /// <see cref="RedisDurabilityCheck" />.
    /// </summary>
    public RedisDurabilityCheck DurabilityCheck { get; set; } = RedisDurabilityCheck.Warn;

    /// <summary>
    /// Store documents of type <typeparamref name="T" /> in Redis, last-write-wins.
    /// </summary>
    public RedisPersistenceConfiguration Store<T>(Action<RedisDocumentMapping> configure) where T : class
    {
        return Store(typeof(T), configure);
    }

    public RedisPersistenceConfiguration Store(Type entityType, Action<RedisDocumentMapping> configure)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(configure);

        // A saga registered here would get the document frames: a blind SET with no compare-and-swap,
        // silently losing a concurrent write to the same saga. The two registrations are separate
        // precisely so that CanApply can claim saga chains and nothing else, so refuse the mix-up at
        // the call site rather than let it downgrade the guarantee.
        if (entityType.CanBeCastTo<Saga>())
        {
            throw new InvalidRedisMappingException(entityType,
                $"It is a saga, and Store<T>() writes are last-write-wins. Register it with Saga<{entityType.NameInCode()}>(...) instead, which writes with an optimistic-concurrency check.");
        }

        return register(entityType, false, configure);
    }

    /// <summary>
    /// Keep the state of saga type <typeparamref name="T" /> in Redis. Every write is a compare-and-swap
    /// against the revision the message read, and a lost race is reported as
    /// <see cref="SagaConcurrencyException" />.
    /// </summary>
    public RedisPersistenceConfiguration Saga<T>(Action<RedisDocumentMapping> configure) where T : Saga
    {
        return Saga(typeof(T), configure);
    }

    public RedisPersistenceConfiguration Saga(Type sagaType, Action<RedisDocumentMapping> configure)
    {
        ArgumentNullException.ThrowIfNull(sagaType);
        ArgumentNullException.ThrowIfNull(configure);

        if (!sagaType.CanBeCastTo<Saga>())
        {
            throw new InvalidRedisMappingException(sagaType,
                $"It does not inherit from Wolverine's Saga, so it has no completion state to honour. Register it with Store<{sagaType.NameInCode()}>(...) instead.");
        }

        return register(sagaType, true, configure);
    }

    private RedisPersistenceConfiguration register(Type entityType, bool isSaga,
        Action<RedisDocumentMapping> configure)
    {
        if (entityType.IsValueType)
        {
            throw new InvalidRedisMappingException(entityType, "Only reference types can be stored in Redis.");
        }

        if (_mappings.TryGetValue(entityType, out var existing) && existing.IsSaga != isSaga)
        {
            throw new InvalidRedisMappingException(entityType,
                $"It is already registered with {(existing.IsSaga ? "Saga" : "Store")}<T>(). A type is either a saga or a document, not both.");
        }

        if (existing == null)
        {
            existing = new RedisDocumentMapping(entityType, isSaga);
            _mappings[entityType] = existing;
        }

        configure(existing);

        // Fail at this call site rather than at codegen time, where the mistake is much harder to place.
        existing.Compile();

        return this;
    }

    internal IReadOnlyCollection<RedisDocumentMapping> Mappings => _mappings.Values;

    internal bool TryFindMapping(Type entityType, out RedisDocumentMapping mapping)
    {
        return _mappings.TryGetValue(entityType, out mapping!);
    }

    internal bool IsRegisteredSaga(Type entityType)
    {
        return _mappings.TryGetValue(entityType, out var mapping) && mapping.IsSaga;
    }

    internal RedisDocumentMapping MappingFor(Type entityType)
    {
        return _mappings.TryGetValue(entityType, out var mapping)
            ? mapping
            : throw new InvalidRedisMappingException(entityType,
                $"It was never registered. Add it with Store<{entityType.NameInCode()}>(...) or Saga<{entityType.NameInCode()}>(...) inside UseRedisPersistence().");
    }
}
