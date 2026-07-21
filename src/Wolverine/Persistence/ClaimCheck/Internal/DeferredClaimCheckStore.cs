using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Persistence.ClaimCheck.Internal;

/// <summary>
/// An <see cref="IClaimCheckStore"/> proxy used when the real store is registered in the
/// application's DI container (e.g. via the <c>...FromServices</c> overloads) and therefore
/// cannot be resolved at Wolverine configuration time. The Wolverine runtime calls
/// <see cref="AttachProvider"/> once the container has been built; the first
/// store / load / delete call then resolves and caches the concrete
/// <see cref="IClaimCheckStore"/> out of the container. See GH-3564.
/// </summary>
internal sealed class DeferredClaimCheckStore : IClaimCheckStore
{
    private readonly object _lock = new();
    private IServiceProvider? _provider;
    private volatile IClaimCheckStore? _resolved;

    /// <summary>
    /// Hand the built application service provider to this store. Called once by the
    /// Wolverine runtime during startup, before any message is serialized.
    /// </summary>
    public void AttachProvider(IServiceProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    private IClaimCheckStore resolve()
    {
        var resolved = _resolved;
        if (resolved is not null)
        {
            return resolved;
        }

        lock (_lock)
        {
            if (_resolved is not null)
            {
                return _resolved;
            }

            if (_provider is null)
            {
                throw new InvalidOperationException(
                    "The claim-check store has not been bound to the application service provider yet. " +
                    "UseClaimCheck deferred to a DI-registered IClaimCheckStore, but the Wolverine runtime " +
                    "has not finished starting. Off-loading a [Blob] payload before the host is started is not supported.");
            }

            var store = _provider.GetService<IClaimCheckStore>();
            if (store is null || ReferenceEquals(store, this))
            {
                throw new InvalidOperationException(
                    "UseClaimCheck was configured to resolve an IClaimCheckStore from the service container " +
                    "(for example via a ...FromServices overload), but no concrete IClaimCheckStore is registered. " +
                    "Register one in the service collection, or assign a store explicitly inside the UseClaimCheck callback.");
            }

            _resolved = store;
            return store;
        }
    }

    public Task<ClaimCheckToken> StoreAsync(ReadOnlyMemory<byte> payload, string contentType,
        CancellationToken cancellationToken = default)
        => resolve().StoreAsync(payload, contentType, cancellationToken);

    public Task<ReadOnlyMemory<byte>> LoadAsync(ClaimCheckToken token,
        CancellationToken cancellationToken = default)
        => resolve().LoadAsync(token, cancellationToken);

    public Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
        => resolve().DeleteAsync(token, cancellationToken);
}
