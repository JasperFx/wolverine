using JasperFx.Core.Reflection;

namespace Wolverine.AmazonS3;

/// <summary>
/// Declares which document types Wolverine may read and write in S3, and how each is addressed.
/// </summary>
/// <remarks>
/// Registration is explicit, type by type, which is also what keeps the frame provider selective:
/// Wolverine takes the first provider whose <c>CanPersist</c> claims a type, so a provider claiming
/// everything an object store could theoretically hold would compete with Marten and EF Core for
/// their own documents depending on registration order.
/// </remarks>
public class AmazonS3Configuration
{
    private readonly Dictionary<Type, S3DocumentMapping> _mappings = new();

    /// <summary>
    /// Store documents of type <typeparamref name="T" /> in S3.
    /// </summary>
    public AmazonS3Configuration Store<T>(Action<S3DocumentMapping> configure) where T : class
    {
        return Store(typeof(T), configure);
    }

    public AmazonS3Configuration Store(Type entityType, Action<S3DocumentMapping> configure)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(configure);

        if (entityType.IsValueType)
        {
            throw new InvalidS3DocumentMappingException(entityType, "Only reference types can be stored as S3 objects.");
        }

        // GH-4160. A saga registered through Store<T>() would be silently useless and worse than
        // useless: a saga chain picks its persistence on CanApply rather than CanPersist, and this
        // provider claims a chain only for a type registered through Saga<T>(). Through Store<T>() the
        // fallback is the IN-MEMORY saga persistor -- host starts, bucket and key function ignored,
        // saga state in process memory. Refuse it where the mistake is made.
        if (entityType.CanBeCastTo<Saga>())
        {
            throw new InvalidS3DocumentMappingException(entityType,
                $"Register a saga with Saga<{entityType.NameInCode()}>(...) instead. Store<T>() does not claim saga CHAINS, so a saga registered through it would silently be kept by the in-memory saga persistor -- bucket and key function ignored -- rather than in S3.");
        }

        if (!_mappings.TryGetValue(entityType, out var mapping))
        {
            mapping = new S3DocumentMapping(entityType);
            _mappings[entityType] = mapping;
        }

        configure(mapping);

        // Fail at this call site rather than at codegen time, where the mistake is much harder to place.
        mapping.Compile();

        return this;
    }

    /// <summary>
    ///     Persist the saga type <typeparamref name="T" /> in S3, using conditional writes for optimistic
    ///     concurrency.
    /// </summary>
    /// <remarks>
    ///     Deliberately a separate call from <see cref="Store{T}" />, for two reasons. It makes "this saga
    ///     lives in this bucket" something the application said rather than something it got by accident;
    ///     and it is what lets <c>CanApply</c> claim saga chains <em>only</em>, so <c>[Transactional]</c>
    ///     and <c>AutoApplyTransactions</c> can never pick this provider as the transaction owner of an
    ///     ordinary chain that happens to touch an S3 document. See GH-4160.
    /// </remarks>
    public AmazonS3Configuration Saga<T>(Action<S3DocumentMapping> configure) where T : Saga
    {
        return Saga(typeof(T), configure);
    }

    public AmazonS3Configuration Saga(Type sagaType, Action<S3DocumentMapping> configure)
    {
        ArgumentNullException.ThrowIfNull(sagaType);
        ArgumentNullException.ThrowIfNull(configure);

        if (!sagaType.CanBeCastTo<Saga>())
        {
            throw new InvalidS3DocumentMappingException(sagaType,
                $"It does not derive from Saga. Register an ordinary document with Store<{sagaType.NameInCode()}>(...) instead.");
        }

        if (!_mappings.TryGetValue(sagaType, out var mapping))
        {
            mapping = new S3DocumentMapping(sagaType) { IsSaga = true };
            _mappings[sagaType] = mapping;
        }

        configure(mapping);
        mapping.Compile();

        return this;
    }

    internal IReadOnlyCollection<S3DocumentMapping> Mappings => _mappings.Values;

    internal bool TryFindSagaMapping(Type sagaType, out S3DocumentMapping mapping)
    {
        return _mappings.TryGetValue(sagaType, out mapping!) && mapping.IsSaga;
    }

    internal bool TryFindMapping(Type entityType, out S3DocumentMapping mapping)
    {
        return _mappings.TryGetValue(entityType, out mapping!);
    }

    internal S3DocumentMapping MappingFor(Type entityType)
    {
        return _mappings.TryGetValue(entityType, out var mapping)
            ? mapping
            : throw new InvalidS3DocumentMappingException(entityType,
                $"It was never registered. Add it with Store<{entityType.NameInCode()}>(...) inside UseAmazonS3Persistence().");
    }
}
