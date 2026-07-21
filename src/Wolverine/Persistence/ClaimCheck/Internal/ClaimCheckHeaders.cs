namespace Wolverine.Persistence.ClaimCheck.Internal;

internal static class ClaimCheckHeaders
{
    /// <summary>
    /// Header-name prefix used for individual claim-check tokens. The full
    /// header name is <c>claim-check.{propertyName}</c>.
    /// </summary>
    public const string Prefix = "claim-check.";

    /// <summary>
    /// Header name carrying the claim-check token for a whole serialized body that was
    /// auto-offloaded because it exceeded the configured size threshold (GH-3504). The
    /// <c>$body</c> suffix cannot collide with a per-property token since property names
    /// never begin with <c>$</c>.
    /// </summary>
    public const string BodyHeaderName = Prefix + "$body";

    /// <summary>
    /// Header name carrying the routing key of the <see cref="IClaimCheckStore"/> that an envelope's
    /// claim-check payloads were off-loaded to (GH-3508). Absent when the global default store was used,
    /// so single-store envelopes are unchanged. The receiver resolves the store by this key rather than
    /// re-evaluating routes, which is what lets per-endpoint routing survive the send/receive URI change.
    /// </summary>
    public const string StoreHeaderName = Prefix + "$store";
}
