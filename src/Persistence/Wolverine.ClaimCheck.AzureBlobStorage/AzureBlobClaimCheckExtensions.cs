using Azure.Storage.Blobs;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.AzureBlobStorage;

public static class AzureBlobClaimCheckExtensions
{
    /// <summary>
    /// Configure Wolverine's Claim Check pipeline to persist payloads in
    /// Azure Blob Storage using a connection string and container name.
    /// </summary>
    public static ClaimCheckConfiguration UseAzureBlobStorage(
        this ClaimCheckConfiguration config,
        string connectionString,
        string containerName)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string must be provided", nameof(connectionString));
        if (string.IsNullOrWhiteSpace(containerName))
            throw new ArgumentException("Container name must be provided", nameof(containerName));

        config.Store = new AzureBlobClaimCheckStore(connectionString, containerName);
        return config;
    }

    /// <summary>
    /// Configure Wolverine's Claim Check pipeline to persist payloads in
    /// Azure Blob Storage using a pre-built <see cref="BlobContainerClient"/>.
    /// </summary>
    public static ClaimCheckConfiguration UseAzureBlobStorage(
        this ClaimCheckConfiguration config,
        BlobContainerClient containerClient)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (containerClient is null) throw new ArgumentNullException(nameof(containerClient));

        config.Store = new AzureBlobClaimCheckStore(containerClient);
        return config;
    }
}
