using Wolverine.Fisher;

namespace Wolverine.Http.Fisher;

/// <summary>
///     Marks a parameter to an HTTP endpoint as being part of the Fisher event sourcing
///     "aggregate handler" workflow
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class AggregateAttribute : WriteAggregateAttribute
{
    public AggregateAttribute()
    {
    }

    public AggregateAttribute(string? routeOrParameterName) : base(routeOrParameterName)
    {
    }
}
