using JasperFx.Core.Reflection;

namespace Wolverine.AzureBlobStorage;

/// <summary>
/// Declares which document types Wolverine may read and write in Azure Blob Storage, and how each is
/// addressed.
/// </summary>
/// <remarks>
/// Registration is explicit, type by type, which is also what keeps the frame provider selective:
/// Wolverine takes the first provider whose <c>CanPersist</c> claims a type, so a provider claiming
/// everything an object store could theoretically hold would compete with Marten and EF Core for
/// their own documents depending on registration order.
/// </remarks>
public class AzureBlobStorageConfiguration
{
    private readonly Dictionary<Type, BlobDocumentMapping> _mappings = new();

    /// <summary>
    /// Store documents of type <typeparamref name="T" /> as blobs.
    /// </summary>
    public AzureBlobStorageConfiguration Store<T>(Action<BlobDocumentMapping> configure) where T : class
    {
        return Store(typeof(T), configure);
    }

    public AzureBlobStorageConfiguration Store(Type entityType, Action<BlobDocumentMapping> configure)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(configure);

        if (entityType.IsValueType)
        {
            throw new InvalidBlobDocumentMappingException(entityType,
                "Only reference types can be stored as blobs.");
        }

        // GH-4160. A saga registered through Store<T>() would be silently useless and worse than
        // useless: a saga chain picks its persistence on CanApply rather than CanPersist, and this
        // provider claims a chain only for a type registered through Saga<T>(). Through Store<T>() the
        // fallback is the IN-MEMORY saga persistor -- host starts, container and blob name function
        // ignored, saga state in process memory. Refuse it where the mistake is made.
        if (entityType.CanBeCastTo<Saga>())
        {
            throw new InvalidBlobDocumentMappingException(entityType,
                $"Register a saga with Saga<{entityType.NameInCode()}>(...) instead. Store<T>() does not claim saga CHAINS, so a saga registered through it would silently be kept by the in-memory saga persistor -- container and blob name function ignored -- rather than in Azure Blob Storage.");
        }

        if (!_mappings.TryGetValue(entityType, out var mapping))
        {
            mapping = new BlobDocumentMapping(entityType);
            _mappings[entityType] = mapping;
        }

        configure(mapping);

        // Fail at this call site rather than at codegen time, where the mistake is much harder to place.
        mapping.Compile();

        return this;
    }

    /// <summary>
    ///     Persist the saga type <typeparamref name="T" /> in Azure Blob Storage, using conditional
    ///     writes for optimistic concurrency.
    /// </summary>
    /// <remarks>
    ///     Deliberately a separate call from <see cref="Store{T}" />, for two reasons. It makes "this saga
    ///     lives in this container" something the application said rather than something it got by
    ///     accident; and it is what lets <c>CanApply</c> claim saga chains <em>only</em>, so
    ///     <c>[Transactional]</c> and <c>AutoApplyTransactions</c> can never pick this provider as the
    ///     transaction owner of an ordinary chain that happens to touch a blob document. See GH-4160.
    /// </remarks>
    public AzureBlobStorageConfiguration Saga<T>(Action<BlobDocumentMapping> configure) where T : Saga
    {
        return Saga(typeof(T), configure);
    }

    public AzureBlobStorageConfiguration Saga(Type sagaType, Action<BlobDocumentMapping> configure)
    {
        ArgumentNullException.ThrowIfNull(sagaType);
        ArgumentNullException.ThrowIfNull(configure);

        if (!sagaType.CanBeCastTo<Saga>())
        {
            throw new InvalidBlobDocumentMappingException(sagaType,
                $"It does not derive from Saga. Register an ordinary document with Store<{sagaType.NameInCode()}>(...) instead.");
        }

        if (!_mappings.TryGetValue(sagaType, out var mapping))
        {
            mapping = new BlobDocumentMapping(sagaType) { IsSaga = true };
            _mappings[sagaType] = mapping;
        }

        configure(mapping);
        mapping.Compile();

        return this;
    }

    internal IReadOnlyCollection<BlobDocumentMapping> Mappings => _mappings.Values;

    internal bool TryFindSagaMapping(Type sagaType, out BlobDocumentMapping mapping)
    {
        return _mappings.TryGetValue(sagaType, out mapping!) && mapping.IsSaga;
    }

    internal bool TryFindMapping(Type entityType, out BlobDocumentMapping mapping)
    {
        return _mappings.TryGetValue(entityType, out mapping!);
    }

    internal BlobDocumentMapping MappingFor(Type entityType)
    {
        return _mappings.TryGetValue(entityType, out var mapping)
            ? mapping
            : throw new InvalidBlobDocumentMappingException(entityType,
                $"It was never registered. Add it with Store<{entityType.NameInCode()}>(...) inside UseAzureBlobStoragePersistence().");
    }
}
