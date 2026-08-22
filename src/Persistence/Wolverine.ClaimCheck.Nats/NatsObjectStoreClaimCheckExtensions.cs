using NATS.Client.Core;
using NATS.Client.ObjectStore;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.Nats;

/// <summary>
/// Extension methods for configuring a NATS JetStream Object Store backed
/// <see cref="IClaimCheckStore"/> from a Wolverine <see cref="ClaimCheckConfiguration"/>.
/// </summary>
public static class NatsObjectStoreClaimCheckExtensions
{
    /// <summary>
    /// Use an explicit, already-connected <see cref="INatsConnection"/> and the given object-store
    /// bucket name as the backing store for Wolverine claim checks.
    /// </summary>
    /// <param name="config">The claim check configuration to attach to.</param>
    /// <param name="connection">An already-connected <see cref="INatsConnection"/>.</param>
    /// <param name="bucketName">Name of the object-store bucket used to hold payloads. Created on first use if it does not exist.</param>
    /// <param name="maxAge">
    /// Optional native bucket TTL. When Wolverine creates the bucket it is configured with this max age,
    /// so the NATS server expires off-loaded payloads itself. This is the cheapest way to keep the bucket
    /// from growing without bound and it keeps working while the application is down — prefer it over
    /// <c>DeletePayloadsOlderThan(...)</c> where you control bucket creation. See GH-4006.
    /// </param>
    public static ClaimCheckConfiguration UseNatsObjectStore(
        this ClaimCheckConfiguration config,
        INatsConnection connection,
        string bucketName,
        TimeSpan? maxAge = null)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must be provided", nameof(bucketName));
        }

        config.Store = new NatsObjectStoreClaimCheckStore(connection, bucketName, maxAge);
        return config;
    }

    /// <summary>
    /// Use an explicit, already-constructed <see cref="INatsObjContext"/> and the given object-store
    /// bucket name as the backing store for Wolverine claim checks.
    /// </summary>
    public static ClaimCheckConfiguration UseNatsObjectStore(
        this ClaimCheckConfiguration config,
        INatsObjContext context,
        string bucketName,
        TimeSpan? maxAge = null)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must be provided", nameof(bucketName));
        }

        config.Store = new NatsObjectStoreClaimCheckStore(context, bucketName, maxAge);
        return config;
    }
}
