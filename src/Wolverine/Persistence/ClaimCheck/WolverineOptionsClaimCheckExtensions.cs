using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.ClaimCheck.Internal;

namespace Wolverine.Persistence;

/// <summary>
/// Extension methods that wire the Wolverine Claim Check / DataBus pipeline into
/// a <see cref="WolverineOptions"/>.
/// </summary>
public static class WolverineOptionsClaimCheckExtensions
{
    /// <summary>
    /// Enable the Claim Check pipeline. Properties on outgoing messages decorated
    /// with <see cref="BlobAttribute"/> will be uploaded to the configured
    /// <see cref="IClaimCheckStore"/>, replaced with a header token in the envelope,
    /// and rehydrated automatically on the receiving side.
    /// </summary>
    /// <param name="options">The <see cref="WolverineOptions"/> being configured.</param>
    /// <param name="configure">
    /// Optional configuration callback. If a store is not assigned during the callback
    /// a <see cref="FileSystemClaimCheckStore"/> rooted at the system temp folder is used.
    /// </param>
    public static WolverineOptions UseClaimCheck(
        this WolverineOptions options,
        Action<ClaimCheckConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuration = new ClaimCheckConfiguration(options);
        configure?.Invoke(configuration);

        IClaimCheckStore store;
        if (configuration.Store is not null)
        {
            // An explicit store was assigned during configuration (e.g. UseFileSystem, or
            // UseAmazonS3 with an already-built client). It wins outright: capture it directly
            // in the serializer and expose it in DI for ad-hoc resolution.
            store = configuration.Store;
            options.Services.RemoveAll<IClaimCheckStore>();
            options.Services.AddSingleton(store);
            options.DeferredClaimCheckStore = null;
        }
        else if (options.Services.Any(d => d.ServiceType == typeof(IClaimCheckStore)))
        {
            // No explicit store, but one is already registered in the container — this is the
            // ...FromServices path (GH-3564). The concrete store depends on services that do not
            // exist until the container is built, so defer resolution: the serializer captures a
            // proxy that the runtime binds to the built provider at startup. The DI registration
            // is left intact so both the serializer and user code (GetRequiredService
            // <IClaimCheckStore>()) resolve the real backend rather than the file-system fallback.
            store = options.DeferredClaimCheckStore ??= new DeferredClaimCheckStore();
        }
        else
        {
            // Nothing configured: fall back to a local file-system store rooted under the temp
            // folder, and expose it in DI so user code can resolve it for ad-hoc operations.
            store = new FileSystemClaimCheckStore(ClaimCheckConfiguration.DefaultFileSystemDirectory());
            options.Services.RemoveAll<IClaimCheckStore>();
            options.Services.AddSingleton(store);
            options.DeferredClaimCheckStore = null;
        }

        // Build the router: the resolved default store + global threshold, plus any per-message /
        // per-endpoint routes registered during configuration (GH-3508).
        var router = new ClaimCheckStoreRouter(store, configuration.AutoOffloadThreshold,
            configuration.Routes, configuration.NamedStores);

        // GH-3509: register the background sweeper when a TTL was configured. The settings singleton is
        // replaced outright so a second UseClaimCheck call sweeps the router that actually won, while
        // TryAddEnumerable keys on (IHostedService, ClaimCheckSweeper) and therefore never registers a
        // second sweeper. With no TTL configured nothing is registered at all, so the default behavior —
        // Wolverine never deletes a payload — is unchanged.
        options.Services.RemoveAll<ClaimCheckSweepSettings>();

        if (configuration.PayloadTimeToLive.HasValue)
        {
            if (configuration.SweepInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(configure),
                    "ClaimCheckConfiguration.SweepInterval must be a positive duration.");
            }

            if (configuration.SweepBatchSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(configure),
                    "ClaimCheckConfiguration.SweepBatchSize must be a positive number of payloads.");
            }

            options.Services.AddSingleton(new ClaimCheckSweepSettings(router,
                configuration.PayloadTimeToLive.Value, configuration.SweepInterval, configuration.SweepBatchSize));

            options.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ClaimCheckSweeper>());
        }

        // If UseClaimCheck has already been applied to this options instance,
        // unwrap and re-wrap so the operation is idempotent (new store wins).
        var current = options.DefaultSerializer;
        var inner = current is ClaimCheckMessageSerializer existing ? existing.Inner : current;
        options.DefaultSerializer = new ClaimCheckMessageSerializer(inner, router);

        return options;
    }
}
