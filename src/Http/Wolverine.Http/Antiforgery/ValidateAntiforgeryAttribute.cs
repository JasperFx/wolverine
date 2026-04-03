namespace Wolverine.Http;

/// <summary>
/// Requires antiforgery token validation for this endpoint, even if it does not use form binding.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class ValidateAntiforgeryAttribute : Attribute
{
}
