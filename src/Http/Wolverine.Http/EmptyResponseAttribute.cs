namespace Wolverine.Http;

/// <summary>
/// Marks a Wolverine Http handler as returning no response body. This will make
/// Wolverine ignore the first "return value" as the response
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class EmptyResponseAttribute : Attribute
{
}