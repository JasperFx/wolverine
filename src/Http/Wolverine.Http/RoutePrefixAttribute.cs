namespace Wolverine.Http;

/// <summary>
/// Apply a route prefix to all Wolverine HTTP endpoints within the decorated class.
/// The prefix will be prepended to all route patterns defined by endpoint methods in the class.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class RoutePrefixAttribute : Attribute
{
    public string Prefix { get; }

    public RoutePrefixAttribute(string prefix)
    {
        Prefix = prefix?.Trim('/') ?? throw new ArgumentNullException(nameof(prefix));
    }
}
