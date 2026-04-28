using System.Reflection;

namespace Wolverine.Persistence.ClaimCheck.Internal;

/// <summary>
/// Strategy that knows how to read and write a single <see cref="BlobAttribute"/>-decorated
/// property on a message instance: convert the property value to a byte payload for storage,
/// and reconstitute the property value from a freshly loaded byte payload.
/// </summary>
internal sealed class BlobPropertyAccessor
{
    public BlobPropertyAccessor(PropertyInfo property, BlobAttribute attribute)
    {
        Property = property;
        ContentType = attribute.ContentType;
        HeaderName = ClaimCheckHeaders.Prefix + property.Name;

        var t = property.PropertyType;

        if (t == typeof(byte[]))
        {
            ReadPayload = obj =>
            {
                var value = (byte[]?)property.GetValue(obj);
                return value is { Length: > 0 } ? value : null;
            };
            ApplyLoaded = (obj, bytes) => property.SetValue(obj, bytes.ToArray());
            Clear = obj => property.SetValue(obj, null);
        }
        else if (t == typeof(ReadOnlyMemory<byte>) || t == typeof(ReadOnlyMemory<byte>?))
        {
            ReadPayload = obj =>
            {
                var raw = property.GetValue(obj);
                if (raw is null)
                {
                    return null;
                }

                var memory = (ReadOnlyMemory<byte>)raw;
                return memory.IsEmpty ? null : memory.ToArray();
            };
            ApplyLoaded = (obj, bytes) =>
            {
                ReadOnlyMemory<byte> mem = bytes.ToArray();
                property.SetValue(obj, mem);
            };
            Clear = obj => property.SetValue(obj, ReadOnlyMemory<byte>.Empty);
        }
        else if (typeof(Stream).IsAssignableFrom(t))
        {
            ReadPayload = obj =>
            {
                var stream = (Stream?)property.GetValue(obj);
                if (stream is null)
                {
                    return null;
                }

                using var ms = new MemoryStream();
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                return bytes.Length == 0 ? null : bytes;
            };
            ApplyLoaded = (obj, bytes) => property.SetValue(obj, new MemoryStream(bytes.ToArray(), writable: false));
            Clear = obj => property.SetValue(obj, null);
        }
        else if (t == typeof(string))
        {
            ReadPayload = obj =>
            {
                var text = (string?)property.GetValue(obj);
                return string.IsNullOrEmpty(text) ? null : System.Text.Encoding.UTF8.GetBytes(text);
            };
            ApplyLoaded = (obj, bytes) =>
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes.Span);
                property.SetValue(obj, text);
            };
            Clear = obj => property.SetValue(obj, null);
        }
        else
        {
            throw new NotSupportedException(
                $"[Blob] is not supported on property '{property.DeclaringType?.Name}.{property.Name}' of type '{t.FullName}'. " +
                "Supported types are byte[], ReadOnlyMemory<byte>, System.IO.Stream and string.");
        }
    }

    public PropertyInfo Property { get; }
    public string ContentType { get; }
    public string HeaderName { get; }

    /// <summary>Returns the raw bytes to ship to the store, or null/empty if the property has no payload.</summary>
    public Func<object, byte[]?> ReadPayload { get; }

    /// <summary>Reconstitute the property value from loaded bytes.</summary>
    public Action<object, ReadOnlyMemory<byte>> ApplyLoaded { get; }

    /// <summary>Null-out the property after the payload has been replaced with a token.</summary>
    public Action<object> Clear { get; }
}
