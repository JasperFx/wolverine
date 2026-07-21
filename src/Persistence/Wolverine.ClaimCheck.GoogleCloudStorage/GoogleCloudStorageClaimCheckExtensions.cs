using Google.Cloud.Storage.V1;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.GoogleCloudStorage;

/// <summary>
/// Extension methods for configuring a Google Cloud Storage backed
/// <see cref="IClaimCheckStore"/> from a Wolverine <see cref="ClaimCheckConfiguration"/>.
/// </summary>
public static class GoogleCloudStorageClaimCheckExtensions
{
    /// <summary>
    /// Use an explicit, already-constructed <see cref="StorageClient"/> and the given bucket name
    /// as the backing store for Wolverine claim checks.
    /// </summary>
    /// <param name="config">The claim check configuration to attach to.</param>
    /// <param name="client">An already-configured <see cref="StorageClient"/> instance.</param>
    /// <param name="bucketName">Name of the existing GCS bucket used to hold payloads.</param>
    public static ClaimCheckConfiguration UseGoogleCloudStorage(
        this ClaimCheckConfiguration config,
        StorageClient client,
        string bucketName)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must be provided", nameof(bucketName));
        }

        config.Store = new GoogleCloudStorageClaimCheckStore(client, bucketName);
        return config;
    }

    /// <summary>
    /// Resolve <see cref="StorageClient"/> from the application service container at runtime and use
    /// the given bucket name as the backing store for Wolverine claim checks. This is convenient when
    /// the storage client is registered through standard DI, and mirrors the Amazon S3
    /// <c>UseAmazonS3FromServices</c> pattern.
    /// </summary>
    /// <param name="config">The claim check configuration to attach to.</param>
    /// <param name="bucketName">Name of the existing GCS bucket used to hold payloads.</param>
    public static ClaimCheckConfiguration UseGoogleCloudStorageFromServices(
        this ClaimCheckConfiguration config,
        string bucketName)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must be provided", nameof(bucketName));
        }

        // The StorageClient is not necessarily available at configuration time — register the store
        // as a DI singleton built from the resolved client. Mirrors UseAmazonS3FromServices.
        config.Options.Services.AddSingleton<IClaimCheckStore>(sp =>
            new GoogleCloudStorageClaimCheckStore(sp.GetRequiredService<StorageClient>(), bucketName));

        return config;
    }
}
