namespace Wolverine.Persistence;

/// <summary>
/// Marks a property on a Wolverine message as a "claim check" payload that
/// should be stored out-of-band in an <see cref="IClaimCheckStore"/> rather
/// than embedded inside the serialized envelope body.
/// </summary>
/// <remarks>
/// Supported property types are <c>byte[]</c>, <c>ReadOnlyMemory&lt;byte&gt;</c>,
/// <c>System.IO.Stream</c> and <c>string</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class BlobAttribute : Attribute
{
    /// <summary>
    /// Default content type used when no explicit value is supplied.
    /// </summary>
    public const string DefaultContentType = "application/octet-stream";

    /// <summary>
    /// Construct a <see cref="BlobAttribute"/> using the default
    /// <c>application/octet-stream</c> content type.
    /// </summary>
    public BlobAttribute() : this(DefaultContentType)
    {
    }

    /// <summary>
    /// Construct a <see cref="BlobAttribute"/> declaring the MIME content type
    /// of the payload (e.g. <c>image/png</c>, <c>application/json</c>).
    /// </summary>
    public BlobAttribute(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException("Content type must be provided.", nameof(contentType));
        }

        ContentType = contentType;
    }

    /// <summary>
    /// MIME content type of the payload to store via the claim-check pipeline.
    /// </summary>
    public string ContentType { get; }
}
