using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.AmazonS3;

/// <summary>
/// Extension methods for configuring an Amazon S3 backed
/// <see cref="IClaimCheckStore"/> from a Wolverine
/// <see cref="ClaimCheckConfiguration"/>.
/// </summary>
public static class AmazonS3ClaimCheckExtensions
{
    /// <summary>
    /// Use an explicit, already-constructed <see cref="IAmazonS3"/> client and
    /// the given bucket name as the backing store for Wolverine claim checks.
    /// </summary>
    /// <param name="config">The claim check configuration to attach to.</param>
    /// <param name="client">An already-configured <see cref="IAmazonS3"/> instance.</param>
    /// <param name="bucketName">Name of the existing S3 bucket used to hold payloads.</param>
    public static ClaimCheckConfiguration UseAmazonS3(
        this ClaimCheckConfiguration config,
        IAmazonS3 client,
        string bucketName)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must be provided", nameof(bucketName));
        }

        config.Store = new AmazonS3ClaimCheckStore(client, bucketName);
        return config;
    }

    /// <summary>
    /// Resolve <see cref="IAmazonS3"/> from the application service container
    /// at runtime and use the given bucket name as the backing store for
    /// Wolverine claim checks. This is convenient when the AWS client is
    /// registered through standard DI (e.g. <c>AWSSDK.Extensions.NETCore.Setup</c>'s
    /// <c>services.AddAWSService&lt;IAmazonS3&gt;()</c>).
    /// </summary>
    /// <param name="config">The claim check configuration to attach to.</param>
    /// <param name="bucketName">Name of the existing S3 bucket used to hold payloads.</param>
    public static ClaimCheckConfiguration UseAmazonS3FromServices(
        this ClaimCheckConfiguration config,
        string bucketName)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must be provided", nameof(bucketName));
        }

        // Register the store as a DI singleton built from the resolved IAmazonS3.
        // We deliberately do not assign config.Store here because the IAmazonS3
        // client is not yet available at configuration time — the Wolverine
        // runtime will pick the store up out of the service container once it
        // has been built.
        config.Options.Services.AddSingleton<IClaimCheckStore>(sp =>
            new AmazonS3ClaimCheckStore(sp.GetRequiredService<IAmazonS3>(), bucketName));

        return config;
    }
}
