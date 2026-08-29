using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

/// <summary>
/// GH-4180. The single seam that generated deduplication code calls into, in message handlers,
/// HTTP endpoints, and gRPC methods alike.
///
/// <para>
/// This exists so that codegen has ONE dependency to resolve rather than reaching through
/// <c>IWolverineRuntime.Storage.Deduplication</c> and computing the expiry inline. HTTP and gRPC
/// chains have no <see cref="Runtime.MessageContext" /> to hang the call off — the way
/// <c>AssertEagerIdempotencyAsync</c> hangs off one for message handlers — so a plain injectable
/// service is the only shape that generates identically in all three.
/// </para>
/// </summary>
public interface IMessageDeduplicator
{
    /// <summary>
    /// Claim <paramref name="deduplicationId" />. Returns <see langword="true" /> when the caller is the
    /// first to claim it and execution should continue, <see langword="false" /> when it has already
    /// been claimed inside the configured <see cref="DurabilitySettings.DeduplicationWindow" />.
    /// </summary>
    /// <param name="ancillaryStoreMarker">
    /// The <see cref="Configuration.IChain.AncillaryStoreType" /> of the chain being executed, or null for the
    /// main store. Routing the claim to the same store the handler writes to is what makes the claim
    /// and the work land in one transaction when the chain is transactional.
    /// </param>
    ValueTask<bool> TryClaimAsync(string deduplicationId, Type? ancillaryStoreMarker,
        CancellationToken cancellation);

    /// <summary>
    /// Release a claim so the id may be claimed again. Called from the failure path of a
    /// non-transactional chain — see <see cref="IDeduplicationStore.ReleaseAsync" /> for why skipping it
    /// would permanently poison the id.
    /// </summary>
    ValueTask ReleaseAsync(string deduplicationId, Type? ancillaryStoreMarker, CancellationToken cancellation);
}

internal class MessageDeduplicator : IMessageDeduplicator
{
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<MessageDeduplicator> _logger;

    public MessageDeduplicator(IWolverineRuntime runtime, ILogger<MessageDeduplicator> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public async ValueTask<bool> TryClaimAsync(string deduplicationId, Type? ancillaryStoreMarker,
        CancellationToken cancellation)
    {
        var store = storeFor(ancillaryStoreMarker);

        if (!store.Enabled)
        {
            // The flag is on but this store has no implementation -- NullDeduplicationStore answers
            // "yes, that's new" to everything. Failing here is the whole reason IDeduplicationStore.Enabled
            // exists: letting it through would report the application as protected while every duplicate
            // ran, with no log line and no failing test to distinguish it from a working configuration.
            throw new InvalidOperationException(
                $"Logical message deduplication is enabled, but the message store backing this chain does not implement it. See {nameof(IDeduplicationStore)} for the providers that do. GH-4180");
        }

        // Stored rather than computed at read time, so that shortening the window later cannot
        // retroactively un-claim ids that were recorded under the longer one.
        var expires = DateTimeOffset.UtcNow.Add(_runtime.Options.Durability.DeduplicationWindow);

        var claimed = await store.TryClaimAsync(deduplicationId, expires, cancellation).ConfigureAwait(false);

        if (!claimed)
        {
            // GH-4180: a duplicate that vanishes silently is indistinguishable from a message that was
            // lost, which is the failure mode this whole feature is supposed to remove rather than move.
            // Information rather than Debug on purpose -- this is a business event ("that work was
            // already done"), not pipeline noise, and it is rare by construction.
            _logger.LogInformation(
                "Discarding duplicate work for logical deduplication id '{DeduplicationId}'; it was already claimed within the {Window} deduplication window",
                deduplicationId, _runtime.Options.Durability.DeduplicationWindow);
        }

        return claimed;
    }

    public async ValueTask ReleaseAsync(string deduplicationId, Type? ancillaryStoreMarker,
        CancellationToken cancellation)
    {
        try
        {
            await storeFor(ancillaryStoreMarker).ReleaseAsync(deduplicationId, cancellation).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Never let the compensating release mask the original handler failure -- the caller is
            // already unwinding an exception that matters more than this one. The cost of swallowing it
            // is that the id stays claimed until it expires, so say so loudly enough to be actionable.
            _logger.LogError(e,
                "Failed to release the logical deduplication claim '{DeduplicationId}' after a failed execution. Retries of this work will be discarded as duplicates until the claim expires",
                deduplicationId);
        }
    }

    private IDeduplicationStore storeFor(Type? ancillaryStoreMarker)
    {
        var store = ancillaryStoreMarker == null
            ? _runtime.Storage
            : _runtime.Stores.FindAncillaryStore(ancillaryStoreMarker);

        return store.Deduplication;
    }
}
