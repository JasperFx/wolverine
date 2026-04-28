namespace Wolverine.Persistence.ClaimCheck.Internal;

internal static class ClaimCheckHeaders
{
    /// <summary>
    /// Header-name prefix used for individual claim-check tokens. The full
    /// header name is <c>claim-check.{propertyName}</c>.
    /// </summary>
    public const string Prefix = "claim-check.";
}
