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
}
