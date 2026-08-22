namespace Wolverine.Persistence;

/// <summary>
/// File system backed <see cref="IClaimCheckStore"/>. Each payload is written
/// as a single binary file under <see cref="Directory"/> with a
/// <c>.bin</c> extension; a sidecar <c>.meta</c> file records the original
/// content type so the round trip is lossless.
/// </summary>
/// <remarks>
/// Suitable for single-node scenarios, integration tests, and as a default
/// when no other backend has been configured. Production deployments that
/// span multiple machines should use a shared object-store backend (Azure
/// Blob, S3, etc.).
/// </remarks>
public class FileSystemClaimCheckStore : IClaimCheckStoreWithExpiration
{
    private const string PayloadExtension = ".bin";
    private const string MetadataExtension = ".meta";

    /// <summary>
    /// The directory in which claim-check payloads are stored. Created on demand.
    /// </summary>
    public string Directory { get; }

    public FileSystemClaimCheckStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A directory path must be supplied.", nameof(directory));
        }

        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public async Task<ClaimCheckToken> StoreAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            throw new ArgumentException("contentType must be provided", nameof(contentType));
        }

        var id = Guid.NewGuid().ToString("N");

        var payloadPath = PayloadPathFor(id);
        var metadataPath = MetadataPathFor(id);

        await File.WriteAllBytesAsync(payloadPath, payload.ToArray(), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(metadataPath, contentType, cancellationToken).ConfigureAwait(false);

        return new ClaimCheckToken(id, contentType, payload.Length);
    }

    public async Task<ReadOnlyMemory<byte>> LoadAsync(
        ClaimCheckToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        var payloadPath = PayloadPathFor(token.Id);
        if (!File.Exists(payloadPath))
        {
            throw new FileNotFoundException(
                $"No claim-check payload was found for id '{token.Id}' under '{Directory}'.",
                payloadPath);
        }

        var bytes = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    public Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        var payloadPath = PayloadPathFor(token.Id);
        var metadataPath = MetadataPathFor(token.Id);

        if (File.Exists(payloadPath))
        {
            File.Delete(payloadPath);
        }

        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// GH-3509 sweep support: delete the payload/metadata pairs whose payload file was last written
    /// before <paramref name="cutoff"/>. The <c>.bin</c> file's last-write time is the age of record —
    /// it is written first and never touched again, so it is a faithful stand-in for "stored at".
    /// </summary>
    public Task<int> DeleteExpiredPayloadsAsync(
        DateTimeOffset cutoff,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0)
        {
            return Task.FromResult(0);
        }

        var deleted = 0;

        foreach (var payloadPath in System.IO.Directory.EnumerateFiles(Directory, "*" + PayloadExtension))
        {
            if (deleted >= maxCount || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Another node's sweeper can delete the same file between the enumeration and this check,
            // so every filesystem call here has to tolerate the file already being gone.
            try
            {
                if (File.GetLastWriteTimeUtc(payloadPath) >= cutoff.UtcDateTime)
                {
                    continue;
                }

                File.Delete(payloadPath);

                var metadataPath = Path.ChangeExtension(payloadPath, MetadataExtension);
                if (File.Exists(metadataPath))
                {
                    File.Delete(metadataPath);
                }

                deleted++;
            }
            catch (FileNotFoundException)
            {
                // Already swept by someone else; not an error.
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }

        return Task.FromResult(deleted);
    }

    private string PayloadPathFor(string id) => Path.Combine(Directory, id + PayloadExtension);
    private string MetadataPathFor(string id) => Path.Combine(Directory, id + MetadataExtension);
}
