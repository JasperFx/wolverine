using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        var store = configuration.Store ?? new FileSystemClaimCheckStore(ClaimCheckConfiguration.DefaultFileSystemDirectory());

        // If UseClaimCheck has already been applied to this options instance,
        // unwrap and re-wrap so the operation is idempotent (new store wins).
        var current = options.DefaultSerializer;
        if (current is ClaimCheckMessageSerializer existing)
        {
            options.DefaultSerializer = new ClaimCheckMessageSerializer(existing.Inner, store);
        }
        else
        {
            options.DefaultSerializer = new ClaimCheckMessageSerializer(current, store);
        }

        // Replace any DI-registered IMessageSerializer with the decorator and expose
        // the IClaimCheckStore so user code can resolve it for ad-hoc operations.
        options.Services.RemoveAll<IClaimCheckStore>();
        options.Services.AddSingleton(store);

        return options;
    }
}
