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

    internal IReadOnlyCollection<S3DocumentMapping> Mappings => _mappings.Values;

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
