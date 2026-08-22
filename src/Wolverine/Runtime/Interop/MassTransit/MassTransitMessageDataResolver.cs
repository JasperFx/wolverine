using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Wolverine.Persistence;

namespace Wolverine.Runtime.Interop.MassTransit;

/// <summary>
/// Attaches <see cref="MassTransitMessageDataConverter{T}"/> to every message property marked with
/// <see cref="BlobAttribute"/>, so those properties — and only those — are read as MassTransit
/// <c>MessageData</c> references. See GH-3510.
/// </summary>
/// <remarks>
/// The opt-in marker is deliberately the existing <c>[Blob]</c> attribute rather than a new
/// MassTransit-specific one: a property that is large enough for MassTransit to have off-loaded is the
/// same property Wolverine would off-load, so a message contract shared across a migration needs no extra
/// annotation. Scoping by attribute also matters for correctness — a blanket converter over every
/// <c>byte[]</c> / <c>string</c> property would try to interpret ordinary strings as claim-check
/// references.
///
/// This runs as a <see cref="DefaultJsonTypeInfoResolver"/> modifier on the MassTransit serializer's own
/// <c>JsonSerializerOptions</c>, so it never affects Wolverine's normal serialization path.
/// </remarks>
internal static class MassTransitMessageDataResolver
{
    public static Action<JsonTypeInfo> ModifierFor(IClaimCheckStore store, Func<Uri, string>? addressToId)
    {
        ArgumentNullException.ThrowIfNull(store);

        return typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object)
            {
                return;
            }

            foreach (var property in typeInfo.Properties)
            {
                if (property.AttributeProvider?.IsDefined(typeof(BlobAttribute), inherit: true) != true)
                {
                    continue;
                }

                property.CustomConverter = converterFor(property.PropertyType, store, addressToId);
            }
        };
    }

    /// <summary>
    /// Instantiated by an explicit type switch rather than <c>MakeGenericType</c>: core Wolverine is
    /// analyzed for AOT/trim compatibility, and a reflective generic instantiation trips IL3050. The
    /// closed set here is exactly the set of property types <see cref="BlobAttribute"/> supports.
    /// </summary>
    private static System.Text.Json.Serialization.JsonConverter converterFor(Type propertyType,
        IClaimCheckStore store, Func<Uri, string>? addressToId)
    {
        if (propertyType == typeof(byte[]))
        {
            return new MassTransitMessageDataConverter<byte[]>(store, addressToId);
        }

        if (propertyType == typeof(string))
        {
            return new MassTransitMessageDataConverter<string>(store, addressToId);
        }

        if (propertyType == typeof(ReadOnlyMemory<byte>))
        {
            return new MassTransitMessageDataConverter<ReadOnlyMemory<byte>>(store, addressToId);
        }

        if (propertyType == typeof(Stream))
        {
            return new MassTransitMessageDataConverter<Stream>(store, addressToId);
        }

        if (propertyType == typeof(MemoryStream))
        {
            return new MassTransitMessageDataConverter<MemoryStream>(store, addressToId);
        }

        throw new NotSupportedException(
            $"A [Blob] property of type {propertyType.FullName} cannot be read as MassTransit MessageData. " +
            "Supported types are byte[], ReadOnlyMemory<byte>, string, and Stream.");
    }
}
