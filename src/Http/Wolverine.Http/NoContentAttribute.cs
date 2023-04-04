namespace Wolverine.Http;

/// <summary>
/// Marks a Wolverine Http handler as having no content
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class NoContentAttribute : Attribute
{
    
}