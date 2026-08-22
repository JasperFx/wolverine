namespace Wolverine.Runtime.Interop.MassTransit;

/// <summary>
/// Translates a MassTransit <c>MessageData</c> repository address into the id Wolverine's
/// <see cref="Wolverine.Persistence.IClaimCheckStore"/> uses to look a payload up. See GH-3510.
/// </summary>
/// <remarks>
/// MassTransit's address format is owned by the repository implementation, not by MassTransit itself,
/// so there are several shapes in the wild. These are transcribed from MassTransit's own source the same
/// way <see cref="MassTransitHeaders"/> is:
/// <list type="bullet">
/// <item><c>urn:file:{key}</c> — used by both the file-system and Amazon S3 repositories, which replace
/// path separators with colons. The bucket/container is <b>not</b> part of the address; it comes from the
/// repository's own configuration, which is why the Wolverine store must be pointed at the same
/// bucket.</item>
/// <item>An absolute <c>https://…/{container}/{blob}</c> URI — used by the Azure Storage repository,
/// which returns the blob's own URI. The container is the first path segment and is dropped, matching
/// what Azure's <c>BlobUriBuilder.BlobName</c> yields for a standard account URI.</item>
/// <item><c>urn:msgdata:{id}</c> — the in-memory repository. Nothing outside the producing process can
/// resolve it, so this is rejected with an explanatory error rather than a lookup failure.</item>
/// </list>
/// </remarks>
internal static class MassTransitMessageDataAddress
{
    private const string GzipSuffix = ".gz";

    /// <summary>
    /// Resolve <paramref name="address"/> to a claim-check payload id, and report whether the payload is
    /// gzip-compressed (the Azure repository appends <c>.gz</c> to the blob name when compression is on).
    /// </summary>
    public static string ToPayloadId(Uri address, out bool compressed)
    {
        ArgumentNullException.ThrowIfNull(address);

        var id = resolve(address);

        compressed = id.EndsWith(GzipSuffix, StringComparison.OrdinalIgnoreCase);
        return id;
    }

    private static string resolve(Uri address)
    {
        if (!address.IsAbsoluteUri)
        {
            throw new NotSupportedException(
                $"'{address}' is not an absolute MassTransit MessageData address and cannot be resolved.");
        }

        if (address.Scheme.Equals("urn", StringComparison.OrdinalIgnoreCase))
        {
            // A urn has no path structure, so the whole body arrives as a single segment: "file:a:b:c".
            var parts = address.Segments[0].Split(':');

            if (parts.Length >= 2 && parts[0].Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                // MassTransit substitutes colons for path separators on the way out. Object-store keys
                // always use '/', so rejoin that way rather than with the local separator.
                return string.Join('/', parts.Skip(1));
            }

            if (parts.Length >= 2 && parts[0].Equals("msgdata", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"MassTransit MessageData address '{address}' refers to MassTransit's in-memory repository, " +
                    "whose payloads never leave the producing process and cannot be read by Wolverine. Configure " +
                    "the MassTransit side with a shared repository (file system, Amazon S3, Azure Storage) and " +
                    "point Wolverine's claim-check store at the same location.");
            }

            throw new NotSupportedException(
                $"'{address}' is not a recognised MassTransit MessageData address. Supply an address mapper to " +
                "ReadMessageDataFrom(...) if your MassTransit repository uses a custom address format.");
        }

        // Azure Storage returns the blob's own URI; the first path segment is the container, which the
        // Wolverine store already knows about, so everything after it is the payload id.
        var path = address.AbsolutePath.TrimStart('/');
        var slash = path.IndexOf('/');

        if (slash < 0 || slash == path.Length - 1)
        {
            throw new NotSupportedException(
                $"MassTransit MessageData address '{address}' has no blob name after its container segment.");
        }

        return Uri.UnescapeDataString(path[(slash + 1)..]);
    }
}
