using Wolverine.Polecat;

namespace Wolverine.Http.Polecat;

/// <summary>
///     Marks a parameter to an HTTP endpoint as being part of the Polecat event sourcing
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
