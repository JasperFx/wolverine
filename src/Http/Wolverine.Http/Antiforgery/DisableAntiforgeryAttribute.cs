namespace Wolverine.Http;

/// <summary>
/// Disables antiforgery token validation for this endpoint, even if it uses form binding.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class DisableAntiforgeryAttribute : Attribute
{
}
