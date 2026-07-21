namespace Wolverine.Persistence.ClaimCheck.Internal;

/// <summary>
/// One configured routing rule: a predicate over the outgoing message type and/or envelope, and the
/// keyed store (plus optional threshold override) to use when it matches. See GH-3508.
/// </summary>
internal sealed record ClaimCheckRoute(
    string StoreKey,
    Func<Type, bool>? TypeMatch,
    Func<Envelope, bool>? EnvelopeMatch,
    long? Threshold)
{
    public bool Matches(Type? messageType, Envelope? envelope)
    {
        if (TypeMatch is not null && (messageType is null || !TypeMatch(messageType)))
        {
            return false;
        }

        if (EnvelopeMatch is not null && (envelope is null || !EnvelopeMatch(envelope)))
        {
            return false;
        }

        // At least one matcher is always present by construction, so a rule with both null never reaches here.
        return true;
    }
}

/// <summary>
/// The store + key + effective threshold selected for one send. <see cref="StoreKey"/> is null when the
/// global default store was selected (no store-key header is stamped in that case, keeping single-store
/// envelopes byte-for-byte identical to pre-GH-3508 behavior).
/// </summary>
internal readonly record struct ClaimCheckSelection(IClaimCheckStore Store, string? StoreKey, long? Threshold);

/// <summary>
/// Resolves which <see cref="IClaimCheckStore"/> (and auto-offload threshold) an envelope should use.
///
/// On <b>send</b>, routes are evaluated in registration order against the message type and envelope; the
/// first match wins, otherwise the global default is used. The selected route's key is stamped onto the
/// envelope so the payload can be found again.
///
/// On <b>receive</b>, the router does <i>not</i> re-evaluate routes — it reads the store-key header the
/// sender stamped and looks the store up by key. This is what makes per-endpoint routing round-trip: the
/// sending and receiving endpoint URIs differ, but the key travels with the message. Envelopes with no
/// store-key header (single-store apps, or messages sent before GH-3508) fall back to the default store.
/// </summary>
internal sealed class ClaimCheckStoreRouter
{
    private readonly IReadOnlyList<ClaimCheckRoute> _routes;
    private readonly IReadOnlyDictionary<string, IClaimCheckStore> _namedStores;

    public ClaimCheckStoreRouter(
        IClaimCheckStore defaultStore,
        long? defaultThreshold,
        IReadOnlyList<ClaimCheckRoute> routes,
        IReadOnlyDictionary<string, IClaimCheckStore> namedStores)
    {
        DefaultStore = defaultStore ?? throw new ArgumentNullException(nameof(defaultStore));
        DefaultThreshold = defaultThreshold;
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _namedStores = namedStores ?? throw new ArgumentNullException(nameof(namedStores));
    }

    public IClaimCheckStore DefaultStore { get; }
    public long? DefaultThreshold { get; }

    public ClaimCheckSelection ResolveForSend(Type? messageType, Envelope? envelope)
    {
        foreach (var route in _routes)
        {
            if (route.Matches(messageType, envelope))
            {
                return new ClaimCheckSelection(_namedStores[route.StoreKey], route.StoreKey,
                    route.Threshold ?? DefaultThreshold);
            }
        }

        return new ClaimCheckSelection(DefaultStore, null, DefaultThreshold);
    }

    public IClaimCheckStore ResolveForReceive(Envelope envelope)
    {
        if (envelope.TryGetHeader(ClaimCheckHeaders.StoreHeaderName, out var key) && !string.IsNullOrEmpty(key))
        {
            if (_namedStores.TryGetValue(key, out var store))
            {
                return store;
            }

            throw new InvalidOperationException(
                $"Envelope {envelope.Id} references claim-check store '{key}', but no store is registered under that key on this node. " +
                "The receiving host must configure the same UseClaimCheck store routes as the sender (see GH-3508).");
        }

        return DefaultStore;
    }
}
