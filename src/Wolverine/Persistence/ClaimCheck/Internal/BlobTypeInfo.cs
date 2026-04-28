using System.Collections.Concurrent;
using System.Reflection;

namespace Wolverine.Persistence.ClaimCheck.Internal;

/// <summary>
/// Reflection cache: looks at a given message type once and caches the set of
/// <see cref="BlobAttribute"/>-decorated properties (or the fact that there are none).
/// </summary>
internal sealed class BlobTypeInfo
{
    private static readonly ConcurrentDictionary<Type, BlobTypeInfo> _cache = new();
    public static readonly BlobTypeInfo Empty = new(Array.Empty<BlobPropertyAccessor>());

    public IReadOnlyList<BlobPropertyAccessor> Properties { get; }
    public bool HasBlobs => Properties.Count > 0;

    private BlobTypeInfo(IReadOnlyList<BlobPropertyAccessor> properties)
    {
        Properties = properties;
    }

    public static BlobTypeInfo For(Type type)
    {
        return _cache.GetOrAdd(type, static t =>
        {
            var props = t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Property: p, Attribute: p.GetCustomAttribute<BlobAttribute>()))
                .Where(x => x.Attribute is not null)
                .Where(x => x.Property.GetMethod is not null && x.Property.SetMethod is not null)
                .Select(x => new BlobPropertyAccessor(x.Property, x.Attribute!))
                .ToArray();

            return props.Length == 0 ? Empty : new BlobTypeInfo(props);
        });
    }
}
